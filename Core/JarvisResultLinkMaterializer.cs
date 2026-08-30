using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic presentation implementation for GLOBAL.ADDRESSABLE_RESULT_LINK.
    /// The behavioral policy itself lives in the central Policies Inventory.
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
                    return "[" + display + "](doc:" + sosource + ":" + findoc + ")";
            }

            if (column == "SOACTION" || column == "SOACTIONID")
            {
                int soaction = ReadInt(row, columnName);
                if (soaction > 0)
                    return "[" + display + "](doc:2021:" + soaction + ")";
            }

            if (column == "MTRL")
            {
                int mtrl = ReadInt(row, columnName);
                if (mtrl > 0)
                    return "[" + display + "](item:" + mtrl + ")";
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

        private static void AddSoft1CrmLinks(JarvisTaskExecutionResult result, List<string> links)
        {
            if (!string.Equals(result.TaskType, "CreateCrmTask", StringComparison.OrdinalIgnoreCase)) return;
            JArray ids = result.Outputs["soaction_ids"] as JArray;
            if (ids == null) return;
            foreach (JToken idToken in ids)
            {
                int id;
                if (idToken == null || !int.TryParse(idToken.ToString(), out id) || id <= 0) continue;
                links.Add("[Άνοιγμα εργασίας " + id + "](doc:2021:" + id + ")");
            }
        }

        private static void AddSoft1DocumentLink(JObject outputs, List<string> links)
        {
            int sosource = ReadInt(outputs, "sosource");
            int findoc = ReadInt(outputs, "findoc");
            if (sosource > 0 && findoc > 0)
                links.Add("[Άνοιγμα παραστατικού " + findoc + "](doc:" + sosource + ":" + findoc + ")");
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
                links.Add("[Άνοιγμα είδους " + mtrl + "](item:" + mtrl + ")");
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
