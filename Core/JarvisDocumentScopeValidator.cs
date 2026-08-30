using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic validation for structured document scopes emitted by semantic
    /// decomposition. It validates returned document-type metadata; it does not
    /// infer business scope from arbitrary user wording.
    /// </summary>
    internal static class JarvisDocumentScopeValidator
    {
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
            foreach (JObject row in rows.OfType<JObject>())
            {
                foreach (string typeText in ReadDocumentTypeTexts(row))
                {
                    string category = Classify(typeText);
                    if (string.IsNullOrWhiteSpace(category)) continue;
                    if (!string.Equals(category, scope, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add(typeText.Trim());
                        break;
                    }
                    break;
                }
            }

            if (violations.Count == 0) return new string[0];
            return new[]
            {
                "Report result violates structured document_scope='" + scope + "'. Conflicting document types: " +
                string.Join(", ", violations.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            };
        }

        private static string NormalizeScope(string value)
        {
            string v = (value ?? string.Empty).Trim().ToLowerInvariant();
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
                string name = (property.Name ?? string.Empty).ToLowerInvariant();
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
            string v = (value ?? string.Empty).ToLowerInvariant();
            if (v.Contains("πιστω") || v.Contains("credit")) return "credit";
            if (v.Contains("παραγγελ") || v.Contains("order")) return "order";
            if (v.Contains("προσφορ") || v.Contains("quotation") || v.Contains("quote")) return "quotation";
            if (v.Contains("δελτιο αποστο") || v.Contains("δελτίο αποστο") || v.Contains("delivery note")) return "delivery";
            if (v.Contains("τιμολογ") || v.Contains("invoice")) return "invoice";
            return string.Empty;
        }
    }
}
