using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // DrConfidenceCalculator — ΙΔΙΑ λογική με S1DocReader.ConfidenceCalculator.
    // Αυτόνομο αντίγραφο (απόφαση ιδιοκτήτη: όχι shared library).
    //
    // Hard blocks (→ Blocked): άκυρο ΑΦΜ, συναλλασσόμενος δεν βρέθηκε
    // Soft warnings (→ NeedsReview): γραμμές χωρίς CCCMAPITEMS, duplicate
    // Score: AI 0.40 · ΑΑΔΕ 0.30 · Ιστορικό 0.15 · Profile 0.15,
    //        αναδιανομή βαρών όταν λείπει παράγοντας.
    // AutoCommit: ≥ 0.90 ΚΑΙ κανένα soft warning.
    // ══════════════════════════════════════════════════════════════════════
    public enum DrDecision { AutoCommit, NeedsReview, Blocked }

    public sealed class DrConfidenceResult
    {
        public double AiScore, AadeScore, HistoryScore, ProfileScore, FinalScore;
        public DrDecision Decision;
        public List<string> Reasons = new List<string>();
        public string Breakdown = "";

        public JObject ToJson() => new JObject
        {
            ["aiScore"] = Math.Round(AiScore, 2), ["aadeScore"] = Math.Round(AadeScore, 2),
            ["historyScore"] = Math.Round(HistoryScore, 2), ["profileScore"] = Math.Round(ProfileScore, 2),
            ["finalScore"] = Math.Round(FinalScore, 2), ["decision"] = Decision.ToString(),
            ["reasons"] = new JArray(Reasons), ["breakdown"] = Breakdown
        };
    }

    public static class DrConfidenceCalculator
    {
        public const double AutoCommitThreshold = 0.90;
        private const double WAi = 0.40, WAade = 0.30, WHist = 0.15, WProf = 0.15;

        public static DrConfidenceResult Calculate(
            double aiConfidence,
            AadeMatchScore aade,
            int historySampleSize,
            JObject profile, string docType, JObject detectedLabels,
            bool traderFound, bool duplicateFound, bool allLinesMatched, bool afmValid)
        {
            var r = new DrConfidenceResult();

            if (!traderFound) r.Reasons.Add("Ο συναλλασσόμενος δεν βρέθηκε στο Soft1.");
            if (!afmValid) r.Reasons.Add("Το ΑΦΜ εκδότη δεν είναι έγκυρο.");
            if (r.Reasons.Count > 0)
            {
                r.Decision = DrDecision.Blocked; r.FinalScore = 0;
                r.Breakdown = "BLOCKED: " + string.Join(" | ", r.Reasons);
                return r;
            }

            r.AiScore = Clamp(aiConfidence);
            r.AadeScore = aade?.Score ?? 0;
            r.HistoryScore = historySampleSize > 0 ? Clamp(1.0 - 1.0 / (1.0 + historySampleSize * 0.3)) : 0;

            bool hasProfile = ProfileHasData(profile, docType);
            if (hasProfile && detectedLabels != null && detectedLabels.Count > 0)
            {
                int total = detectedLabels.Properties().Count(p => !string.IsNullOrWhiteSpace(p.Value?.ToString()));
                int matched = CountProfileMatches(profile, docType, detectedLabels);
                r.ProfileScore = total > 0 ? Clamp((double)matched / total) : 0;
            }

            bool hasAade = aade != null && aade.Score > 0;
            bool hasHist = historySampleSize > 0;
            double wA = WAi, wD = hasAade ? WAade : 0, wH = hasHist ? WHist : 0, wP = hasProfile ? WProf : 0;
            r.FinalScore = Clamp((r.AiScore * wA + r.AadeScore * wD + r.HistoryScore * wH + r.ProfileScore * wP) / (wA + wD + wH + wP));

            if (!allLinesMatched) { r.Decision = DrDecision.NeedsReview; r.Reasons.Add("Γραμμές χωρίς αντιστοίχιση Soft1 — απαιτείται έλεγχος."); }
            else if (duplicateFound) { r.Decision = DrDecision.NeedsReview; r.Reasons.Add("⚠ Πιθανή διπλοκαταχώρηση — ελέγξτε πριν καταχωρήσετε."); }
            else r.Decision = r.FinalScore >= AutoCommitThreshold ? DrDecision.AutoCommit : DrDecision.NeedsReview;

            r.Breakdown = $"AI:{r.AiScore:F2} ΑΑΔΕ:{(hasAade ? r.AadeScore.ToString("F2") : "n/a")} " +
                          $"Ιστορικό:{(hasHist ? r.HistoryScore.ToString("F2") : "n/a")} Profile:{(hasProfile ? r.ProfileScore.ToString("F2") : "n/a")} " +
                          $"→ Τελικό:{r.FinalScore:F2} [{r.Decision}]" +
                          (r.Reasons.Count > 0 ? "  •  " + string.Join(" | ", r.Reasons) : "");
            S1Jarvis.Core.DebugLog.Log("[dr-confidence] " + r.Breakdown);
            return r;
        }

        private static bool ProfileHasData(JObject profile, string docType)
        {
            var dt = (profile?["docTypes"] as JArray)?.FirstOrDefault(d => d["type"]?.ToString() == docType) as JObject;
            var af = dt?["activeFields"] as JObject;
            if (af == null) return false;
            return af.Properties().Any(p => (p.Value as JArray)?.Count > 0);
        }

        private static int CountProfileMatches(JObject profile, string docType, JObject detected)
        {
            var dt = (profile?["docTypes"] as JArray)?.FirstOrDefault(d => d["type"]?.ToString() == docType) as JObject;
            var af = dt?["activeFields"] as JObject;
            if (af == null) return 0;
            var allLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var section in af.Properties())
                foreach (var m in (section.Value as JArray) ?? new JArray())
                    foreach (var l in (m["labels"] as JArray) ?? new JArray())
                        allLabels.Add(l.ToString().Trim());
            return detected.Properties().Count(p => !string.IsNullOrWhiteSpace(p.Value?.ToString()) && allLabels.Contains(p.Value.ToString().Trim()));
        }

        private static double Clamp(double v) => Math.Max(0, Math.Min(1, v));
    }
}
