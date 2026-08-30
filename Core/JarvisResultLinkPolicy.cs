using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Presentation-only policy for addressable task results. It never invents
    /// object identities or URLs: links are emitted only from authoritative
    /// executor outputs. Local artifact links deliberately carry a canonical raw
    /// Windows path because the Jarvis renderer stores that target in its private
    /// file-link registry and dispatches open_file to the C# host on click.
    /// </summary>
    internal static class JarvisResultLinkPolicy
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
                (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
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

            // The target is authoritative runtime data, not model-authored markdown.
            // Never add markdown escaping to the target itself. In particular, "_"
            // must remain "_" and never become "\\_", otherwise File.Exists sees a
            // different path when the UI dispatches open_file.
            links.Add("[" + EscapeMarkdownLabel(label) + "](" + fullPath + ")");
        }

        private static void AddFileArtifactLink(JObject outputs, List<string> links)
        {
            // Top-level path is authoritative when both projections are present.
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
                .Replace("]", "\\]");
        }

        private static int ReadInt(JObject outputs, string property)
        {
            if (outputs == null || outputs[property] == null) return 0;
            int value;
            return int.TryParse(outputs[property].ToString(), out value) ? value : 0;
        }

        private static string ReadString(JObject outputs, string property)
        {
            if (outputs == null || outputs[property] == null || outputs[property].Type == JTokenType.Null) return string.Empty;
            return outputs[property].ToString();
        }
    }
}
