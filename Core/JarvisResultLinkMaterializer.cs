using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic presentation implementation for GLOBAL.ADDRESSABLE_RESULT_LINK.
    /// The behavioral policy and URI templates live in the central Policies Inventory.
    /// This component only materializes links from authoritative executor/dataset outputs.
    /// </summary>
    internal static class JarvisResultLinkMaterializer
    {
        internal static string[] BuildMarkdownLinks(JarvisTaskExecutionResult result)
        {
            var links = new List<string>();
            if (result == null || result.Outputs == null) return links.ToArray();

            AddSoft1CrmLinks(result, links);
            AddSoft1DocumentLink(result.Outputs, links);
            AddTraderLink(result.Outputs, links);
            AddItemLink(result.Outputs, links);
            AddExternalLink(result.Outputs, "webLink", "Άνοιγμα στο Outlook", links);
            AddExternalLink(result.Outputs, "pdfLink", "Άνοιγμα PDF", links);
            AddFileLink(result.Outputs, "path", links);
            AddFileArtifactLink(result.Outputs, links);

            return links
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Compatibility bridge for the still-live mature agent loop. The legacy
        /// processor stores every tool_use and tool_result in the conversation
        /// protocol. Those tool results are verified runtime outputs and therefore
        /// may be used by the same central addressable-link policy. Model prose is
        /// never treated as identity evidence here.
        /// </summary>
        internal static string[] BuildMarkdownLinksFromLegacyTrace(
            IList<JObject> conversation,
            int startIndex)
        {
            var links = new List<string>();
            if (conversation == null || conversation.Count == 0) return links.ToArray();

            int first = Math.Max(0, Math.Min(startIndex, conversation.Count));
            var toolNamesById = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = first; i < conversation.Count; i++)
            {
                JObject message = conversation[i];
                JArray blocks = message == null ? null : message["content"] as JArray;
                if (blocks == null) continue;

                foreach (JObject block in blocks.OfType<JObject>())
                {
                    string type = block["type"] == null ? string.Empty : block["type"].ToString();
                    if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        string id = block["id"] == null ? string.Empty : block["id"].ToString();
                        string name = block["name"] == null ? string.Empty : block["name"].ToString();
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                            toolNamesById[id] = name;
                        continue;
                    }

                    if (!string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase) ||
                        (bool?)block["is_error"] == true)
                        continue;

                    string toolUseId = block["tool_use_id"] == null ? string.Empty : block["tool_use_id"].ToString();
                    string toolName;
                    if (string.IsNullOrWhiteSpace(toolUseId) ||
                        !toolNamesById.TryGetValue(toolUseId, out toolName))
                        continue;

                    AddLegacyToolResultLinks(toolName, block["content"] == null ? string.Empty : block["content"].ToString(), links);
                }
            }

            return links
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Adds verified canonical links without duplicating a target already
        /// present in model-authored text. The caller still sends the combined
        /// text through JarvisPresentationGateway.FinalizeFreeform afterwards.
        /// </summary>
        internal static string AppendMissingVerifiedLinks(string text, IEnumerable<string> verifiedLinks)
        {
            string value = text ?? string.Empty;
            if (verifiedLinks == null) return value;

            var missing = new List<string>();
            foreach (string link in verifiedLinks.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string target = ReadMarkdownTarget(link);
                if (!string.IsNullOrWhiteSpace(target) &&
                    value.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                missing.Add(link.Trim());
            }

            if (missing.Count == 0) return value;
            return value.TrimEnd() + (value.Length == 0 ? string.Empty : "\n\n") + string.Join("\n", missing.ToArray());
        }

        /// <summary>
        /// Materializes a link for one table cell only when the same row contains
        /// the authoritative identity required by the registered URI mapping.
        /// It never guesses SOSOURCE/object ids from labels or series names.
        /// </summary>
        internal static string MaterializeDatasetCell(JObject row, string columnName, string displayValue)
        {
            string display = EscapeMarkdownLabel(string.IsNullOrWhiteSpace(displayValue)
                ? JarvisPolicySettings.Presentation.NullDisplay
                : displayValue);
            if (row == null || string.IsNullOrWhiteSpace(columnName)) return string.Empty;

            string column = columnName.Trim().ToUpperInvariant();

            if (column == "FINCODE" || column == "FINDOC")
            {
                int sosource = ReadInt(row, "SOSOURCE");
                int findoc = ReadInt(row, "FINDOC");
                if (sosource > 0 && findoc > 0)
                    return "[" + display + "](" + BuildDocumentUri(sosource, findoc) + ")";
            }

            if (column == "SOACTION" || column == "SOACTIONID")
            {
                int soaction = ReadInt(row, columnName);
                if (soaction > 0)
                    return "[" + display + "](" + BuildCrmTaskUri(soaction) + ")";
            }

            if (column == "MTRL")
            {
                int mtrl = ReadInt(row, columnName);
                if (mtrl > 0)
                    return "[" + display + "](" + BuildItemUri(mtrl) + ")";
            }

            if (column == "PATH" || column == "FILE_PATH")
            {
                string path = ReadString(row, columnName);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(path.Trim());
                        return "[" + display + "](" + fullPath + ")";
                    }
                    catch { return string.Empty; }
                }
            }

            if (column == "URL" || column == "WEBLINK" || column == "PDFLINK")
            {
                string url = ReadString(row, columnName);
                if (!string.IsNullOrWhiteSpace(url) &&
                    (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                    return "[" + display + "](" + url.Trim() + ")";
            }

            return string.Empty;
        }

        private static void AddLegacyToolResultLinks(string toolName, string rawResult, List<string> links)
        {
            if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(rawResult) || links == null) return;

            JObject parsed = null;
            try { parsed = JObject.Parse(rawResult); } catch { }

            if (string.Equals(toolName, "create_crm_task", StringComparison.OrdinalIgnoreCase))
            {
                if (parsed == null || (bool?)parsed["success"] != true) return;

                JArray rows = parsed["results"] as JArray;
                if (rows != null)
                {
                    foreach (JObject row in rows.OfType<JObject>())
                    {
                        int id = ReadInt(row, "soactionId");
                        if (id > 0) links.Add("[Άνοιγμα εργασίας " + id + "](" + BuildCrmTaskUri(id) + ")");
                    }
                }

                int directId = ReadInt(parsed, "soactionId");
                if (directId > 0) links.Add("[Άνοιγμα εργασίας " + directId + "](" + BuildCrmTaskUri(directId) + ")");
                return;
            }

            if (string.Equals(toolName, "export_query_to_file", StringComparison.OrdinalIgnoreCase))
            {
                if (parsed == null || (parsed["success"] != null && (bool?)parsed["success"] != true)) return;
                AddFileLink(parsed, "path", links);
                return;
            }

            if (string.Equals(toolName, "export_shown_table", StringComparison.OrdinalIgnoreCase))
            {
                if (parsed != null)
                {
                    if (parsed["success"] != null && (bool?)parsed["success"] != true) return;
                    AddFileLink(parsed, "path", links);
                }
                else
                {
                    var pathWrapper = new JObject { ["path"] = rawResult.Trim().Trim('"') };
                    AddFileLink(pathWrapper, "path", links);
                }
                return;
            }

            if (string.Equals(toolName, "create_outlook_event", StringComparison.OrdinalIgnoreCase))
            {
                if (parsed == null || (bool?)parsed["success"] != true) return;
                AddExternalLink(parsed, "webLink", "Άνοιγμα στο Outlook", links);
                return;
            }

            if (string.Equals(toolName, "open_document", StringComparison.OrdinalIgnoreCase))
            {
                if (parsed == null) return;
                AddSoft1DocumentLink(parsed, links);
            }
        }

        private static void AddSoft1CrmLinks(JarvisTaskExecutionResult result, List<string> links)
        {
            if (!string.Equals(result.TaskType, "CreateCrmTask", StringComparison.OrdinalIgnoreCase)) return;
            JArray ids = result.Outputs["soaction_ids"] as JArray;
            if (ids == null) return;
            foreach (JToken idToken in ids)
            {
                int id;
                if (idToken == null || !int.TryParse(idToken.ToString(), out id) || id <= 0) continue;
                links.Add("[Άνοιγμα εργασίας " + id + "](" + BuildCrmTaskUri(id) + ")");
            }
        }

        private static void AddSoft1DocumentLink(JObject outputs, List<string> links)
        {
            int sosource = ReadInt(outputs, "sosource");
            int findoc = ReadInt(outputs, "findoc");
            if (sosource > 0 && findoc > 0)
                links.Add("[Άνοιγμα παραστατικού " + findoc + "](" + BuildDocumentUri(sosource, findoc) + ")");
        }

        private static void AddTraderLink(JObject outputs, List<string> links)
        {
            int trdrId = ReadInt(outputs, "trdrId");
            string objectName = ReadString(outputs, "objectName");
            if (trdrId > 0 && !string.IsNullOrWhiteSpace(objectName))
                links.Add("[Άνοιγμα συναλλασσόμενου " + trdrId + "](trader:" + objectName.Trim().ToUpperInvariant() + ":" + trdrId + ")");
        }

        private static void AddItemLink(JObject outputs, List<string> links)
        {
            int mtrl = ReadInt(outputs, "mtrl");
            if (mtrl > 0)
                links.Add("[Άνοιγμα είδους " + mtrl + "](" + BuildItemUri(mtrl) + ")");
        }

        private static void AddExternalLink(JObject outputs, string property, string label, List<string> links)
        {
            string url = ReadString(outputs, property);
            if (!string.IsNullOrWhiteSpace(url) &&
                (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                links.Add("[" + EscapeMarkdownLabel(label) + "](" + url.Trim() + ")");
        }

        private static void AddFileLink(JObject outputs, string property, List<string> links)
        {
            string path = ReadString(outputs, property);
            if (string.IsNullOrWhiteSpace(path)) return;

            string fullPath;
            string label;
            try
            {
                fullPath = Path.GetFullPath(path.Trim());
                label = Path.GetFileName(fullPath);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(label)) label = "Άνοιγμα αρχείου";

            // The UI stores this canonical target in its private file-link registry
            // and dispatches open_file to the C# host. Never markdown-escape the
            // authoritative path itself; doing so changes File.Exists semantics.
            links.Add("[" + EscapeMarkdownLabel(label) + "](" + fullPath + ")");
        }

        private static void AddFileArtifactLink(JObject outputs, List<string> links)
        {
            if (!string.IsNullOrWhiteSpace(ReadString(outputs, "path"))) return;

            JToken artifact = outputs["file_artifact"];
            if (artifact == null) return;
            if (artifact.Type == JTokenType.String)
            {
                var wrapper = new JObject { ["path"] = artifact.ToString() };
                AddFileLink(wrapper, "path", links);
                return;
            }

            JObject obj = artifact as JObject;
            if (obj != null) AddFileLink(obj, "path", links);
        }

        private static string BuildCrmTaskUri(int soactionId)
        {
            return JarvisPolicySettings.Presentation.CrmTaskUriTemplate
                .Replace("{soactionId}", soactionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static string BuildDocumentUri(int sosource, int findoc)
        {
            return JarvisPolicySettings.Presentation.DocumentUriTemplate
                .Replace("{sosource}", sosource.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{findoc}", findoc.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static string BuildItemUri(int mtrl)
        {
            return JarvisPolicySettings.Presentation.ItemUriTemplate
                .Replace("{mtrl}", mtrl.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static string EscapeMarkdownLabel(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("[", "\\[")
                .Replace("]", "\\]")
                .Replace("|", "\\|")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }

        private static string ReadMarkdownTarget(string markdownLink)
        {
            string value = markdownLink ?? string.Empty;
            int open = value.IndexOf("](", StringComparison.Ordinal);
            if (open < 0) return string.Empty;
            int close = value.LastIndexOf(')');
            if (close <= open + 2) return string.Empty;
            return value.Substring(open + 2, close - open - 2).Trim();
        }

        private static int ReadInt(JObject outputs, string property)
        {
            if (outputs == null || string.IsNullOrWhiteSpace(property)) return 0;
            JProperty p = outputs.Properties().FirstOrDefault(x => string.Equals(x.Name, property, StringComparison.OrdinalIgnoreCase));
            if (p == null || p.Value == null) return 0;
            int value;
            return int.TryParse(p.Value.ToString(), out value) ? value : 0;
        }

        private static string ReadString(JObject outputs, string property)
        {
            if (outputs == null || string.IsNullOrWhiteSpace(property)) return string.Empty;
            JProperty p = outputs.Properties().FirstOrDefault(x => string.Equals(x.Name, property, StringComparison.OrdinalIgnoreCase));
            if (p == null || p.Value == null || p.Value.Type == JTokenType.Null) return string.Empty;
            return p.Value.ToString();
        }
    }
}
