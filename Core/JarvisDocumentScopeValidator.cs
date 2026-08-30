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
    /// decomposition. Document category is derived from authoritative FINDOC metadata
    /// exposed by SERIES + FPRMS; SOSOURCE remains object/navigation identity and is
    /// never treated as a business document-category discriminator.
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
        /// against authoritative SERIES.NAME + FPRMS.NAME metadata. Both tables are
        /// part of the FINDOC query contract; SOSOURCE alone is never sufficient.
        /// </summary>
        internal static bool TryBuildDocumentSqlPredicate(
            string documentScope,
            string seriesExpression,
            string fprmsExpression,
            out string predicate)
        {
            predicate = string.Empty;
            string scope = NormalizeScope(documentScope);
            string s = (seriesExpression ?? string.Empty).Trim();
            string p = (fprmsExpression ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(scope) || scope == "documents" || scope == "movements" ||
                string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(p))
                return false;

            string credit = MatchEither(s, p, "Πιστω", "Credit");
            string order = MatchEither(s, p, "Παραγγελ", "Order");
            string quotation = MatchEither(s, p, "Προσφορ", "Quotation", "Quote");
            string invoice = MatchEither(s, p, "Τιμολ", "Invoice");
            string delivery = MatchEither(s, p, "Δελτίο Αποστο", "Δελτιο Αποστο", "Delivery Note");

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
                    predicate = invoice + " AND NOT " + credit + " AND NOT " + order + " AND NOT " + quotation;
                    return true;
                case "delivery":
                    predicate = delivery + " AND NOT " + credit + " AND NOT " + order + " AND NOT " + quotation + " AND NOT " + invoice;
                    return true;
                default:
                    return false;
            }
        }

        // Compatibility for older callers. New FINDOC planning must use the two-table overload.
        internal static bool TryBuildSeriesSqlPredicate(string documentScope, string seriesExpression, out string predicate)
        {
            predicate = string.Empty;
            return false;
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
                string[] metadata = ReadDocumentTypeTexts(row).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string category = ClassifyCombined(metadata);
                if (string.IsNullOrWhiteSpace(category)) continue;
                sawClassifiableMetadata = true;
                if (!string.Equals(category, scope, StringComparison.OrdinalIgnoreCase))
                    violations.Add(metadata.Length == 0 ? category : string.Join(" / ", metadata));
            }

            if (!sawClassifiableMetadata)
                return new[]
                {
                    "Specific document_scope='" + scope +
                    "' cannot be verified because returned FINDOC rows contain no classifiable SERIES/FPRMS metadata."
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
                if (!(name.Contains("series") || name.Contains("fprms") || name.Contains("type") ||
                      name.Contains("σειρ") || name.Contains("τυπ") || name.Contains("παραμετρ") ||
                      name.Contains("parameter")))
                    continue;
                if (property.Value == null || property.Value.Type == JTokenType.Null) continue;
                string value = property.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value)) yield return value.Trim();
            }
        }

        private static string ClassifyCombined(IEnumerable<string> values)
        {
            string[] categories = (values ?? Enumerable.Empty<string>())
                .Select(ClassifySingle)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Precedence matters when SERIES and FPRMS expose overlapping wording.
            if (categories.Contains("credit", StringComparer.OrdinalIgnoreCase)) return "credit";
            if (categories.Contains("order", StringComparer.OrdinalIgnoreCase)) return "order";
            if (categories.Contains("quotation", StringComparer.OrdinalIgnoreCase)) return "quotation";
            if (categories.Contains("invoice", StringComparer.OrdinalIgnoreCase)) return "invoice";
            if (categories.Contains("delivery", StringComparer.OrdinalIgnoreCase)) return "delivery";
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

        private static string MatchEither(string seriesExpression, string fprmsExpression, params string[] needles)
        {
            var terms = new List<string>();
            foreach (string needle in needles ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(needle)) continue;
                string escaped = needle.Replace("'", "''");
                terms.Add(seriesExpression + ".NAME LIKE N'%" + escaped + "%'");
                terms.Add(fprmsExpression + ".NAME LIKE N'%" + escaped + "%'");
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
                if (category != UnicodeCategory.NonSpacingMark &&
                    category != UnicodeCategory.SpacingCombiningMark &&
                    category != UnicodeCategory.EnclosingMark)
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
