using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    internal sealed class JarvisDatasetRefinementOutcome
    {
        internal bool Handled { get; set; }
        internal string UserMessage { get; set; }
    }

    /// <summary>
    /// Shell-scoped validated dataset cache. Follow-up filters/sorts/limits may be
    /// executed locally when every required field already exists in the dataset.
    /// The model may interpret the follow-up into a tiny transform plan, but never
    /// receives the full dataset and never executes the transform itself.
    /// </summary>
    internal sealed class JarvisDatasetSession
    {
        // The desktop selects only the logical agent. Provider and model are
        // authoritative Verilic routing decisions and must never be hardcoded here.
        private const string RuntimeAiAgent = "Jarvis";

        private readonly object _sync = new object();
        private string _businessQuestion;
        private JObject _dataset;

        internal bool HasDataset
        {
            get { lock (_sync) return _dataset != null && _dataset["rows"] is JArray; }
        }

        internal bool TryCapture(string businessQuestion, string datasetJson)
        {
            if (string.IsNullOrWhiteSpace(datasetJson)) return false;
            try
            {
                JObject parsed = JObject.Parse(datasetJson);
                if (!(parsed["rows"] is JArray)) return false;
                lock (_sync)
                {
                    _businessQuestion = businessQuestion ?? string.Empty;
                    _dataset = (JObject)parsed.DeepClone();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void Clear()
        {
            lock (_sync)
            {
                _businessQuestion = null;
                _dataset = null;
            }
        }

        internal static bool LooksLikeRefinement(string userText)
        {
            string value = NormalizeText(userText);
            if (value.Length == 0 || value.Length > 220) return false;
            string[] hints =
            {
                "μονο ", "απο αυτ", "χωρις ", "κρατα ", "βγαλε ", "φιλτρα",
                "πανω απο", "κατω απο", "μεγαλυτερ", "μικροτερ", "ταξινομ",
                "πρωτ", "τελευται", "only ", "from these", "without ", "filter ",
                "sort ", "greater than", "less than", "top "
            };
            return hints.Any(value.Contains);
        }

        internal async Task<JarvisDatasetRefinementOutcome> TryRefineAsync(
            XSupport xSupport,
            string userText,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var outcome = new JarvisDatasetRefinementOutcome { Handled = false };
            JObject source;
            string originalQuestion;
            lock (_sync)
            {
                source = _dataset == null ? null : (JObject)_dataset.DeepClone();
                originalQuestion = _businessQuestion ?? string.Empty;
            }
            if (source == null || !(source["rows"] is JArray) || !LooksLikeRefinement(userText))
                return outcome;

            JObject plan = await BuildRefinementPlanAsync(
                xSupport, originalQuestion, source, userText, cancellationToken).ConfigureAwait(false);
            if (plan == null || (bool?)plan["canRefine"] != true)
                return outcome;

            string issue;
            JObject refined = ApplyPlan(source, plan, out issue);
            if (refined == null)
            {
                DebugLog.Log("[ORCH-DATASET] local refinement rejected: " + (issue ?? "unknown"));
                return outcome;
            }

            lock (_sync)
            {
                _dataset = (JObject)refined.DeepClone();
                _businessQuestion = originalQuestion + " -> " + (userText ?? string.Empty);
            }

            string refinedJson = refined.ToString(Formatting.None);
            JarvisPresentationResult presentation = await JarvisPresentationComposer.ComposeReportAsync(
                xSupport,
                userText,
                refinedJson,
                cancellationToken).ConfigureAwait(false);

            string intro = presentation == null ? null : presentation.Intro;
            if (string.IsNullOrWhiteSpace(intro))
                intro = "Έγινε το φιλτράρισμα πάνω στα ήδη ανακτημένα δεδομένα, χωρίς νέο query.";

            string table = JarvisPresentationComposer.BuildMarkdownTable(refinedJson, 250);
            outcome.Handled = true;
            outcome.UserMessage = intro + "\n\n" + table;
            DebugLog.Log("[ORCH-DATASET] local refinement applied; rows=" + ((JArray)refined["rows"]).Count);
            return outcome;
        }

        private static async Task<JObject> BuildRefinementPlanAsync(
            XSupport xSupport,
            string originalQuestion,
            JObject dataset,
            string userText,
            CancellationToken cancellationToken)
        {
            JObject catalog = BuildCompactCatalog(dataset);
            JObject request = new JObject
            {
                ["max_tokens"] = 1200,
                ["output_config"] = new JObject { ["effort"] = "low" },
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = "Είσαι ο Jarvis local dataset refinement planner. Αποφασίζεις αν το follow-up μπορεί να απαντηθεί ΜΟΝΟ από τις υπάρχουσες στήλες του validated dataset. " +
                                   "Δεν βλέπεις και δεν επεξεργάζεσαι όλες τις γραμμές. Αν χρειάζεται νέα πληροφορία ή στήλη, βάλε canRefine=false. " +
                                   "Αν γίνεται τοπικά, επέστρεψε JSON μόνο: {\"canRefine\":true,\"filters\":[{\"column\":\"...\",\"op\":\"eq|neq|contains|not_contains|gt|gte|lt|lte\",\"value\":\"...\"}],\"sort\":[{\"column\":\"...\",\"direction\":\"asc|desc\"}],\"limit\":null}. " +
                                   "Χρησιμοποίησε αποκλειστικά column names που υπάρχουν στο catalog. Μην εφεύρεις mapping αν το catalog δεν το υποστηρίζει."
                    }
                },
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JObject
                        {
                            ["originalQuestion"] = originalQuestion ?? string.Empty,
                            ["followUp"] = userText ?? string.Empty,
                            ["catalog"] = catalog
                        }.ToString(Formatting.None)
                    }
                }
            };

            AgentProxyResponse response = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                .SendAsync(xSupport, RuntimeAiAgent, request.ToString(Formatting.None), cancellationToken)
                .ConfigureAwait(false);
            if (response == null || !response.Success || string.IsNullOrWhiteSpace(response.RawResponseJson))
                return null;

            try
            {
                JObject root = JObject.Parse(response.RawResponseJson);
                JArray content = root["content"] as JArray ?? new JArray();
                JObject textBlock = content.OfType<JObject>().FirstOrDefault(x =>
                    string.Equals((string)x["type"], "text", StringComparison.OrdinalIgnoreCase));
                string text = textBlock == null ? null : (string)textBlock["text"];
                if (string.IsNullOrWhiteSpace(text)) return null;
                int first = text.IndexOf('{');
                int last = text.LastIndexOf('}');
                if (first >= 0 && last > first) text = text.Substring(first, last - first + 1);
                return JObject.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        private static JObject BuildCompactCatalog(JObject dataset)
        {
            JArray rows = dataset["rows"] as JArray ?? new JArray();
            var columns = new List<string>();
            foreach (JObject row in rows.OfType<JObject>().Take(50))
                foreach (JProperty p in row.Properties())
                    if (!columns.Any(x => string.Equals(x, p.Name, StringComparison.OrdinalIgnoreCase)))
                        columns.Add(p.Name);

            var catalog = new JObject { ["rowCount"] = rows.Count, ["columns"] = new JArray() };
            JArray columnArray = (JArray)catalog["columns"];
            int budget = 2600;
            foreach (string column in columns.Take(30))
            {
                var values = new List<string>();
                foreach (JObject row in rows.OfType<JObject>().Take(200))
                {
                    JToken token = FindValue(row, column);
                    if (token == null || token.Type == JTokenType.Null) continue;
                    string value = token.Type == JTokenType.String ? token.ToString() : token.ToString(Formatting.None);
                    if (value.Length > 80) value = value.Substring(0, 80);
                    if (!values.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)))
                        values.Add(value);
                    if (values.Count >= 8) break;
                }
                JObject item = new JObject { ["name"] = column, ["examples"] = new JArray(values) };
                string serialized = item.ToString(Formatting.None);
                if (budget - serialized.Length < 0) break;
                budget -= serialized.Length;
                columnArray.Add(item);
            }
            return catalog;
        }

        private static JObject ApplyPlan(JObject source, JObject plan, out string issue)
        {
            issue = null;
            JArray rows = source["rows"] as JArray;
            if (rows == null) { issue = "dataset has no rows"; return null; }
            List<JObject> current = rows.OfType<JObject>().Select(x => (JObject)x.DeepClone()).ToList();
            var availableColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject row in current.Take(50))
                foreach (JProperty p in row.Properties()) availableColumns.Add(p.Name);

            JArray filters = plan["filters"] as JArray ?? new JArray();
            foreach (JObject filter in filters.OfType<JObject>())
            {
                string column = (string)filter["column"];
                string op = ((string)filter["op"] ?? string.Empty).Trim().ToLowerInvariant();
                JToken wanted = filter["value"];
                if (string.IsNullOrWhiteSpace(column) || !availableColumns.Contains(column))
                { issue = "unknown filter column: " + column; return null; }
                if (!IsAllowedOperator(op)) { issue = "unsupported filter operator: " + op; return null; }
                current = current.Where(row => Matches(FindValue(row, column), op, wanted)).ToList();
            }

            JArray sort = plan["sort"] as JArray ?? new JArray();
            IOrderedEnumerable<JObject> ordered = null;
            foreach (JObject item in sort.OfType<JObject>())
            {
                string column = (string)item["column"];
                string direction = ((string)item["direction"] ?? "asc").Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(column) || !availableColumns.Contains(column))
                { issue = "unknown sort column: " + column; return null; }
                Func<JObject, IComparable> selector = row => ComparableValue(FindValue(row, column));
                if (ordered == null)
                    ordered = direction == "desc" ? current.OrderByDescending(selector) : current.OrderBy(selector);
                else
                    ordered = direction == "desc" ? ordered.ThenByDescending(selector) : ordered.ThenBy(selector);
            }
            if (ordered != null) current = ordered.ToList();

            int? limit = (int?)plan["limit"];
            if (limit.HasValue && limit.Value > 0) current = current.Take(Math.Min(limit.Value, 1000)).ToList();

            JObject result = (JObject)source.DeepClone();
            result["rows"] = new JArray(current);
            result["rowCount"] = current.Count;
            result["totalRowCount"] = current.Count;
            result["truncated"] = false;
            result["jarvisLocalRefinement"] = true;
            return result;
        }

        private static bool IsAllowedOperator(string op)
        {
            return op == "eq" || op == "neq" || op == "contains" || op == "not_contains" ||
                   op == "gt" || op == "gte" || op == "lt" || op == "lte";
        }

        private static bool Matches(JToken actual, string op, JToken wanted)
        {
            if (actual == null || actual.Type == JTokenType.Null) return op == "neq" || op == "not_contains";
            double a;
            double b;
            if ((op == "gt" || op == "gte" || op == "lt" || op == "lte") &&
                double.TryParse(actual.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out a) &&
                double.TryParse(wanted == null ? string.Empty : wanted.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out b))
            {
                if (op == "gt") return a > b;
                if (op == "gte") return a >= b;
                if (op == "lt") return a < b;
                return a <= b;
            }
            string left = NormalizeText(actual.ToString());
            string right = NormalizeText(wanted == null ? string.Empty : wanted.ToString());
            if (op == "eq") return left == right;
            if (op == "neq") return left != right;
            if (op == "contains") return left.Contains(right);
            if (op == "not_contains") return !left.Contains(right);
            return false;
        }

        private static IComparable ComparableValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return string.Empty;
            double number;
            if (double.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return number;
            DateTime date;
            if (DateTime.TryParse(token.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date;
            return NormalizeText(token.ToString());
        }

        private static JToken FindValue(JObject row, string column)
        {
            if (row == null) return null;
            JProperty p = row.Properties().FirstOrDefault(x => string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase));
            return p == null ? null : p.Value;
        }

        private static string NormalizeText(string value)
        {
            string source = (value ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(source.Length);
            foreach (char c in source)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    builder.Append(c);
            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
