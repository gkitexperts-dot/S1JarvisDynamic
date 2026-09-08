using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using S1Jarvis.Access.Verilic;
using S1Jarvis.Core;
using Softone;

namespace S1Jarvis.Access
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisSkills — Skills Library client + cache + matching.
    // Boot: LoadAsync(xSupport) μία φορά μετά το verify (fail-soft).
    // Per-task: Match(agentName, taskText) → κείμενο για extraInstructions.
    // Matching: branch ("" = όλα) → subagent ("" = όλοι) → keywords → γενικά πρώτα.
    // Auth: Recognition (ίδιο με verify) μέσω VerilicRecognitionHttp.
    // ══════════════════════════════════════════════════════════════════════
    public static class JarvisSkills
    {
        public sealed class Skill
        {
            public string Id, BranchCode, SubAgent, Title, Instruction;
            public List<string> Keywords;
        }

        private static readonly object _lock = new object();
        private static List<Skill> _cache = new List<Skill>();
        private static string _branch = "";

        public static int Count { get { lock (_lock) return _cache.Count; } }

        public static async Task LoadAsync(XSupport xSupport)
        {
            var resp = await VerilicRecognitionHttp.PostAsync(xSupport, JarvisProducts.Jarvis, "api/licensing/v1/skills", null, "skills");
            if (resp == null) return;
            var list = (resp["skills"] as JArray ?? new JArray()).Select(s => new Skill
            {
                Id = s["id"]?.ToString(), BranchCode = s["branchCode"]?.ToString() ?? "",
                SubAgent = s["subAgent"]?.ToString() ?? "", Title = s["title"]?.ToString() ?? "",
                Instruction = s["instruction"]?.ToString() ?? "",
                Keywords = (s["keywords"] as JArray)?.Select(k => k.ToString()).ToList() ?? new List<string>()
            }).ToList();
            lock (_lock) { _cache = list; _branch = xSupport.ConnectionInfo.BranchId.ToString(); }
            DebugLog.Log("[skills] loaded count=" + list.Count);
        }

        public static string Match(string agentName, string taskText)
        {
            List<Skill> snap; string branch;
            lock (_lock) { snap = _cache; branch = _branch; }
            if (snap.Count == 0) return null;
            string task = (taskText ?? "").ToLowerInvariant(), agent = (agentName ?? "").Trim();
            var matched = snap
                .Where(s => !string.IsNullOrWhiteSpace(s.Instruction))
                .Where(s => string.IsNullOrEmpty(s.BranchCode) || s.BranchCode == branch)
                .Where(s => string.IsNullOrEmpty(s.SubAgent) || string.Equals(s.SubAgent, agent, StringComparison.OrdinalIgnoreCase))
                .Where(s => s.Keywords.Count == 0 || s.Keywords.Any(k => !string.IsNullOrWhiteSpace(k) && task.Contains(k.ToLowerInvariant())))
                .OrderBy(s => string.IsNullOrEmpty(s.BranchCode) ? 0 : 1).ThenBy(s => string.IsNullOrEmpty(s.SubAgent) ? 0 : 1)
                .ToList();
            if (matched.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (var s in matched)
                sb.Append("• ").Append(string.IsNullOrWhiteSpace(s.Title) ? "" : s.Title.Trim() + ": ").AppendLine(s.Instruction.Trim());
            DebugLog.Log("[skills] matched=" + matched.Count + " agent=" + agent);
            return sb.ToString().TrimEnd();
        }
    }
}
