using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic enforcement for structured document scopes emitted by semantic
    /// decomposition. It owns the canonical classification semantics used both to
    /// constrain SQL on authoritative SERIES metadata and to validate returned rows.
    /// It never infers scope from arbitrary model output.
    /// </summary>
    internal static class JarvisDocumentScopeValidator
    {
        internal static string InferExplicitScope(string text)
        {
            string v = NormalizeText(text);
            if (string.IsNullOrWhiteSpace(v)) return string.Empty;
            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (v.Contains("τιμολογ") || v.Contains("invoice")) categories.Add("invoice");
            if (v.Contains("παραγγελ") || v.Contains(" purchase order") || v.Contains(" sales order")) categories.Add("order");
            if (v.Contains("προσφορ") || v.Contains("quotation") || v.Contains("quote")) categories.Add("quotation");
            if (v.Contains("πιστω") || v.Contains("credit note") || v.Contains("credit memo")) categories.Add("credit");
            if (v.Contains("δελτιο αποστο") || v.Contains("delivery note")) categories.Add("delivery");
            return categories.Count == 1 ? categories.First() : string.Empty;
        }

        /// <summary>
        /// Builds the deterministic SQL predicate for a canonical document_scope
        /// against authoritative SERIES.NAME metadata. SOSOURCE alone is not a
        /// document category and must never be used as the category discriminator.
        /// </summary>
        internal static bool TryBuildSeriesSqlPredicate(string documentScope, string seriesExpression, out string predicate)
        {
            predicate = string.Empty;
            string scope = NormalizeScope(documentScope);
            string s = (seriesExpression ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(scope) || scope == "documents" || scope == "movements" || string.IsNullOrWhiteSpace(s))
                return false;

            string credit = "(" + s + ".NAME LIKE N'%Πιστω%' OR " + s + ".NAME LIKE N'%Credit%')";
            string order = "(" + s + ".NAME LIKE N'%Παραγγελ%' OR " + s + ".NAME LIKE N'%Order%')";
            string quotation = "(" + s + ".NAME LIKE N'%Προσφορ%' OR " + s + ".NAME LIKE N'%Quotation%' OR " + s + ".NAME LIKE N'%Quote%')";
            string invoice = "(" + s + ".NAME LIKE N'%Τιμολ%' OR " + s + ".NAME LIKE N'%Invoice%')";
            string delivery = "(" + s + ".NAME LIKE N'%Δελτίο Αποστο%' OR " + s + ".NAME LIKE N'%Δελτιο Αποστο%' OR " + s + ".NAME LIKE N'%Delivery Note%')";

            switch (scope)
            {
                case "credit":
                    predicate = credit;
                    return true;
                case "order":
                    predicate = order + " AND NOT " + credit;
                    return true;
                case "quotation":
                    predicate = quotation + " AND NOT " + credit;
                    return true;
                case "invoice":
                    // Credit notes often contain the word "invoice" in their label.
                    // Credit therefore has precedence. Combined Invoice + Delivery
                    // series remain invoices, matching the canonical presentation rule.
                    predicate = invoice + " AND NOT " + credit + " AND NOT " + order + " AND NOT " + quotation;
                    return true;
                case "delivery":
                    predicate = delivery + " AND NOT " + credit + " AND NOT " + order + " AND NOT " + quotation + " AND NOT " + invoice;
                    return true;
                default:
                    return false;
            }
        }

        internal static string[] Validate(string documentScope, string datasetJson)
        {
            string scope = NormalizeScope(documentScope);
            if (string.IsNullOrWhiteSpace(scope) || scope == "documents" || scope == "movements")
                return new string[0];

            JObject dataset;
            try { dataset = JObject.Parse(datasetJson ?? "{}"); }
            catch { return new[] { "Structured document-scope validation could not parse the report dataset." }; }

            JArray rows = dataset["rows"] as JArray;
            if (rows == null || rows.Count == 0) return new string[0];

            var violations = new List<string>();
            bool sawClassifiableMetadata = false;
            foreach (JObject row in rows.OfType<JObject>())
            {
                foreach (string typeText in ReadDocumentTypeTexts(row))
                {
                    string category = Classify(typeText);
                    if (string.IsNullOrWhiteSpace(category)) continue;
                    sawClassifiableMetadata = true;
                    if (!string.Equals(category, scope, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(typeText.Trim());
                        break;
                    }
                    break;
                }
            }

            if (!sawClassifiableMetadata)
                return new[]
                {
                    "Specific document_scope='" + scope +
                    "' cannot be verified because returned rows contain no classifiable authoritative document-type metadata."
                };

            if (violations.Count == 0) return new string[0];
            return new[]
            {
                "Report result violates structured document_scope='" + scope + "'. Conflicting document types: " +
                string.Join(", ", violations.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            };
        }

        private static string NormalizeScope(string value)
        {
            string v = NormalizeText(value);
            if (v == "invoice" || v == "invoices") return "invoice";
            if (v == "order" || v == "orders") return "order";
            if (v == "quotation" || v == "quote" || v == "quotes") return "quotation";
            if (v == "credit" || v == "credits") return "credit";
            if (v == "delivery" || v == "delivery_note" || v == "delivery_notes") return "delivery";
            if (v == "documents" || v == "movements") return v;
            return string.Empty;
        }

        private static IEnumerable<string> ReadDocumentTypeTexts(JObject row)
        {
            if (row == null) yield break;
            foreach (JProperty property in row.Properties())
            {
                string name = NormalizeText(property.Name);
                if (!(name.Contains("series") || name.Contains("type") || name.Contains("σειρ") || name.Contains("τυπ")))
                    continue;
                if (property.Value == null || property.Value.Type == JTokenType.Null) continue;
                string value = property.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(Classify(value)))
                    yield return value;
            }
        }

        private static string Classify(string value)
        {
            string v = NormalizeText(value);
            if (v.Contains("πιστω") || v.Contains("credit")) return "credit";
            if (v.Contains("παραγγελ") || v.Contains("order")) return "order";
            if (v.Contains("προσφορ") || v.Contains("quotation") || v.Contains("quote")) return "quotation";
            // Invoice intentionally precedes delivery. A combined "Τιμολόγιο -
            // Δ.Αποστολής" is an invoice for an explicit invoice request, while a
            // credit invoice was already captured by the higher-priority credit rule.
            if (v.Contains("τιμολογ") || v.Contains("invoice")) return "invoice";
            if (v.Contains("δελτιο αποστο") || v.Contains("delivery note")) return "delivery";
            return string.Empty;
        }

        private static string NormalizeText(string value)
        {
            string source = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(source.Length);
            foreach (char c in source)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark &&
                    category != UnicodeCategory.SpacingCombiningMark &&
                    category != UnicodeCategory.EnclosingMark)
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
