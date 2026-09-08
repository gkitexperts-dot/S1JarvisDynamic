using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using S1Jarvis.Access;
using S1Jarvis.Access.Verilic;
using Softone;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // DrProfileService — Cosmos profile learning για τον DR.
    // ΙΔΙΑ λογική με S1DocReader (HttpProfileService + ProfileLearner).
    // Auth: Recognition με το DocReader product credential (JarvisProducts.JarvisDocReader).
    // Endpoints: /profiles/{load-or-create,save,update-meta,history/record}
    // ══════════════════════════════════════════════════════════════════════
    public static class DrProfileService
    {
        private const string P = JarvisProducts.JarvisDocReader;

        public static async Task<(JObject profile, bool isNew)> LoadOrCreateAsync(XSupport x, string afm, string businessName, string changedBy)
        {
            var r = await VerilicRecognitionHttp.PostAsync(x, P, "profiles/load-or-create",
                new JObject { ["afm"] = afm, ["businessName"] = businessName ?? "", ["changedBy"] = changedBy ?? "jarvis" }, "dr-profile");
            return r == null ? (null, true) : (r["profile"] as JObject, r["isNew"]?.Value<bool>() ?? true);
        }

        public static async Task<bool> SaveAsync(XSupport x, JObject profile, string changedBy)
            => await VerilicRecognitionHttp.PostAsync(x, P, "profiles/save", new JObject { ["profile"] = profile, ["changedBy"] = changedBy ?? "jarvis" }, "dr-profile") != null;

        public static Task UpdateMetaAsync(XSupport x, string afm, string docType, double confidence, bool hadCorrections)
            => VerilicRecognitionHttp.PostAsync(x, P, "profiles/update-meta", new JObject { ["afm"] = afm, ["docType"] = docType ?? "", ["confidence"] = confidence, ["hadCorrections"] = hadCorrections }, "dr-profile");

        public static Task RecordHistoryAsync(XSupport x, string afm, string changedBy, string issuerName, string docType, string docSeries, string docNumber, string docDate, double confidence, string rawJson)
            => VerilicRecognitionHttp.PostAsync(x, P, "profiles/history/record", new JObject
            {
                ["afm"] = afm, ["changedBy"] = changedBy ?? "jarvis", ["issuerName"] = issuerName ?? "", ["docType"] = docType ?? "",
                ["docSeries"] = docSeries ?? "", ["docNumber"] = docNumber ?? "", ["docDate"] = docDate ?? "", ["confidence"] = confidence, ["rawJson"] = rawJson ?? ""
            }, "dr-profile");

        // ProfileLearner — ΙΔΙΟ με DocReader
        public static bool Learn(JObject profile, string docType, JObject detectedLabels)
        {
            if (profile == null || detectedLabels == null || detectedLabels.Count == 0) return false;
            var docTypes = profile["docTypes"] as JArray; if (docTypes == null) { docTypes = new JArray(); profile["docTypes"] = docTypes; }
            var dt = docTypes.FirstOrDefault(d => d["type"]?.ToString() == docType) as JObject;
            if (dt == null) { dt = new JObject { ["type"] = docType, ["activeFields"] = new JObject(), ["extractionMeta"] = new JObject() }; docTypes.Add(dt); }
            var af = dt["activeFields"] as JObject; if (af == null) { af = new JObject(); dt["activeFields"] = af; }
            bool changed = false;
            foreach (var kv in detectedLabels.Properties())
            {
                string label = kv.Value?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(kv.Name) || string.IsNullOrWhiteSpace(label)) continue;
                int dot = kv.Name.IndexOf('.');
                string section = Section(dot > 0 ? kv.Name.Substring(0, dot) : ""), field = dot > 0 ? kv.Name.Substring(dot + 1) : kv.Name;
                if (section == null) continue;
                var list = af[section] as JArray; if (list == null) { list = new JArray(); af[section] = list; }
                var m = list.FirstOrDefault(x => x["key"]?.ToString() == field) as JObject;
                if (m == null) { m = new JObject { ["key"] = field, ["labels"] = new JArray() }; list.Add(m); changed = true; }
                var labels = m["labels"] as JArray; if (labels == null) { labels = new JArray(); m["labels"] = labels; }
                if (!labels.Any(l => l.ToString() == label)) { labels.Add(label); changed = true; }
            }
            return changed;
        }

        private static string Section(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "issuer": return "issuer"; case "recipient": return "recipient";
                case "document_info": case "documentinfo": return "documentInfo";
                case "line_items": case "lineitems": return "lineItems";
                case "totals": return "totals"; case "payment": return "payment"; case "delivery": return "delivery";
                default: return null;
            }
        }
    }
}
