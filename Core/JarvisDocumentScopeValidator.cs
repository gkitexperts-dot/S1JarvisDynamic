using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic enforcement for structured FINDOC document scopes.
    /// FINDOC is the universe of documents. FPRMS is the authoritative business
    /// document-type discriminator. SERIES is descriptive/subtype metadata for
    /// variants of the same FPRMS (for example printed/manual series), and
    /// SOSOURCE is source/object identity for navigation.
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
        /// Builds a deterministic predicate from FPRMS.NAME only. SERIES is joined
        /// and projected for descriptive/subtype context but never changes the
        /// canonical document category selected by FPRMS.
        /// </summary>
        internal static bool TryBuildDocumentSqlPredicate(
            string documentScope,
            string seriesExpression,
            string fprmsExpression,
            out string predicate)
        {
            predicate = string.Empty;
            string scope = NormalizeScope(documentScope);
            string p = (fprmsExpression ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(scope) || scope == "documents" || scope == "movements" || string.IsNullOrWhiteSpace(p))
                return false;

            string credit = MatchFprms(p, "Πιστω", "Credit");
            string order = MatchFprms(p, "Παραγγελ", "Order");
            string quotation = MatchFprms(p, "Προσφορ", "Quotation", "Quote");
            string invoice = MatchFprms(p, "Τιμολ", "Invoice");
            string delivery = MatchFprms(p, "Δελτίο Αποστο", "Δελτιο Αποστο", "Delivery Note");

            switch (scope)
            {
                case "credit": predicate = credit; return true;
                case "order": predicate = order + " AND NOT " + credit; return true;
                case "quotation": predicate = quotation + " AND NOT " + credit; return true;
                case "invoice": predicate = invoice + " AND NOT " + credit + " AND NOT " + order + " AND NOT " + quotation; return true;
                case "delivery": predicate = delivery + " AND NOT " + credit + " AND NOT " + order + " AND NOT " + quotation + " AND NOT " + invoice; return true;
                default: return false;
            }
        }

        internal static string[] Validate(string documentScope, string datasetJson)
        {
            string scope = NormalizeScope(documentScope);
            if (string.IsNullOrWhiteSpace(scope) || scope == "documents" || scope == "movements") return new string[0];

            JObject dataset;
            try { dataset = JObject.Parse(datasetJson ?? "{}"); }
            catch { return new[] { "Structured document-scope validation could not parse the report dataset." }; }

            JArray rows = dataset["rows"] as JArray;
            if (rows == null || rows.Count == 0) return new string[0];

            var violations = new List<string>();
            bool sawFprms = false;
            foreach (JObject row in rows.OfType<JObject>())
            {
                string fprms = ReadMetadata(row, "FPRMS");
                string series = ReadMetadata(row, "SERIES");
                if (string.IsNullOrWhiteSpace(fprms)) continue;
                sawFprms = true;
                string category = ClassifySingle(fprms);
                if (string.IsNullOrWhiteSpace(category)) continue;
                if (!string.Equals(category, scope, StringComparison.OrdinalIgnoreCase))
                    violations.Add(string.IsNullOrWhiteSpace(series) ? fprms : fprms + " / " + series);
            }

            if (!sawFprms)
                return new[] { "Specific document_scope='" + scope + "' cannot be verified because returned FINDOC rows contain no authoritative FPRMS metadata." };

            if (violations.Count == 0) return new string[0];
            return new[]
            {
                "Report result violates structured document_scope='" + scope + "'. Conflicting FPRMS document types: " +
                string.Join(", ", violations.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            };
        }

        private static string ReadMetadata(JObject row, string token)
        {
            if (row == null) return string.Empty;
            JProperty property = row.Properties().FirstOrDefault(x =>
                x.Name.IndexOf(token ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0 &&
                x.Value != null && x.Value.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(x.Value.ToString()));
            return property == null ? string.Empty : property.Value.ToString().Trim();
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

        private static string ClassifySingle(string value)
        {
            string v = NormalizeText(value);
            if (v.Contains("πιστω") || v.Contains("credit")) return "credit";
            if (v.Contains("παραγγελ") || v.Contains("order")) return "order";
            if (v.Contains("προσφορ") || v.Contains("quotation") || v.Contains("quote")) return "quotation";
            if (v.Contains("τιμολογ") || v.Contains("invoice")) return "invoice";
            if (v.Contains("δελτιο αποστο") || v.Contains("delivery note")) return "delivery";
            return string.Empty;
        }

        private static string MatchFprms(string fprmsExpression, params string[] needles)
        {
            var terms = new List<string>();
            foreach (string needle in needles ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(needle)) continue;
                terms.Add(fprmsExpression + ".NAME LIKE N'%" + needle.Replace("'", "''") + "%'");
            }
            return "(" + string.Join(" OR ", terms.ToArray()) + ")";
        }

        private static string NormalizeText(string value)
        {
            string source = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(source.Length);
            foreach (char c in source)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category != UnicodeCategory.NonSpacingMark && category != UnicodeCategory.SpacingCombiningMark && category != UnicodeCategory.EnclosingMark)
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
