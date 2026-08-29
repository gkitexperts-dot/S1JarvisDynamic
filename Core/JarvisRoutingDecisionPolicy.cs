using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    internal enum JarvisRoutingDecisionKind
    {
        ResolveFromDefault,
        UseDynamicPass,
        ResolveAfterDynamic,
        ClarifyWithUser
    }

    internal sealed class JarvisRoutingCandidateScore
    {
        public string TaskType { get; set; }
        public double Score { get; set; }
        public string Source { get; set; }
        public int? KnowledgeId { get; set; }
        public int Company { get; set; }
        public string Reason { get; set; }
    }

    internal sealed class JarvisRoutingDecision
    {
        public JarvisRoutingDecisionKind Kind { get; set; }
        public JarvisRoutingCandidateScore Winner { get; set; }
        public JarvisRoutingCandidateScore RunnerUp { get; set; }
        public bool IsAmbiguous { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Central policy for semantic routing confidence and Pass-1/Pass-2 merge.
    ///
    /// Design rules:
    /// 1. Strong, unambiguous default routing wins without a DB lookup.
    /// 2. Medium/ambiguous default routing opens Pass 2.
    /// 3. Dynamic knowledge can reinforce default routing.
    /// 4. Dynamic knowledge is not an unrestricted override. A conflicting
    ///    dynamic candidate must be very strong and clearly ahead, otherwise
    ///    the result escalates to user clarification.
    /// 5. Company-specific approved knowledge is preferred over global rows.
    /// 6. Historical success/failure adjusts dynamic confidence conservatively.
    ///
    /// These constants are intentionally centralized so later tuning can be
    /// performed without changing repository/planner code.
    /// </summary>
    internal static class JarvisRoutingDecisionPolicy
    {
        // Pass 1: accept only when confidence is high and the top candidate is
        // clearly separated from the runner-up.
        internal const double DefaultAcceptThreshold = 0.82;
        internal const double DefaultMinimumForDynamicPass = 0.45;
        internal const double AmbiguityMargin = 0.12;

        // Pass 2: a dynamic candidate must itself be strong enough to resolve.
        internal const double DynamicAcceptThreshold = 0.78;

        // A dynamic candidate that DISAGREES with the default winner is held to
        // a stricter standard because table knowledge is an aid, not authority
        // over the hardcoded task contract/catalog.
        internal const double ConflictingDynamicThreshold = 0.88;
        internal const double ConflictingDynamicLead = 0.18;

        // Dynamic score contributions. Confidence remains the dominant signal.
        private const double CompanySpecificBonus = 0.06;
        private const double MaxPriorityBonus = 0.05;
        private const double MaxHistoryBonus = 0.06;
        private const double MaxHistoryPenalty = 0.10;

        internal static JarvisRoutingDecision EvaluateDefault(
            IEnumerable<JarvisRoutingCandidateScore> candidates)
        {
            JarvisRoutingCandidateScore[] ranked = Rank(candidates);
            JarvisRoutingCandidateScore winner = ranked.FirstOrDefault();
            JarvisRoutingCandidateScore runnerUp = ranked.Skip(1).FirstOrDefault();

            if (winner == null)
            {
                return new JarvisRoutingDecision
                {
                    Kind = JarvisRoutingDecisionKind.UseDynamicPass,
                    Reason = "Pass 1 produced no task candidate."
                };
            }

            bool ambiguous = IsAmbiguous(winner, runnerUp);
            if (winner.Score >= DefaultAcceptThreshold && !ambiguous)
            {
                return new JarvisRoutingDecision
                {
                    Kind = JarvisRoutingDecisionKind.ResolveFromDefault,
                    Winner = winner,
                    RunnerUp = runnerUp,
                    IsAmbiguous = false,
                    Reason = "Pass 1 is high-confidence and clearly separated."
                };
            }

            return new JarvisRoutingDecision
            {
                Kind = JarvisRoutingDecisionKind.UseDynamicPass,
                Winner = winner,
                RunnerUp = runnerUp,
                IsAmbiguous = ambiguous,
                Reason = winner.Score < DefaultMinimumForDynamicPass
                    ? "Pass 1 confidence is low; consult approved dynamic knowledge."
                    : ambiguous
                        ? "Pass 1 candidates are too close; consult approved dynamic knowledge."
                        : "Pass 1 confidence is medium; consult approved dynamic knowledge."
            };
        }

        internal static JarvisRoutingDecision EvaluateAfterDynamic(
            IEnumerable<JarvisRoutingCandidateScore> defaultCandidates,
            IEnumerable<JarvisRoutingKnowledgeRecord> knowledge,
            int currentCompany)
        {
            JarvisRoutingCandidateScore[] defaultRanked = Rank(defaultCandidates);
            JarvisRoutingCandidateScore defaultWinner = defaultRanked.FirstOrDefault();

            JarvisRoutingCandidateScore[] dynamicRanked = (knowledge ?? Enumerable.Empty<JarvisRoutingKnowledgeRecord>())
                .Where(IsUsableKnowledge)
                .Select(x => ScoreKnowledge(x, currentCompany))
                .Where(x => x != null)
                .GroupBy(x => x.TaskType, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(x => x.Score)
                .ToArray();

            if (dynamicRanked.Length == 0)
            {
                return new JarvisRoutingDecision
                {
                    Kind = JarvisRoutingDecisionKind.ClarifyWithUser,
                    Winner = defaultWinner,
                    RunnerUp = defaultRanked.Skip(1).FirstOrDefault(),
                    IsAmbiguous = true,
                    Reason = "Pass 2 contains no usable approved dynamic knowledge."
                };
            }

            JarvisRoutingCandidateScore dynamicWinner = dynamicRanked[0];
            JarvisRoutingCandidateScore dynamicRunnerUp = dynamicRanked.Skip(1).FirstOrDefault();
            bool dynamicAmbiguous = IsAmbiguous(dynamicWinner, dynamicRunnerUp);

            if (defaultWinner == null)
            {
                if (dynamicWinner.Score >= DynamicAcceptThreshold && !dynamicAmbiguous)
                {
                    return ResolvedDynamic(dynamicWinner, dynamicRunnerUp,
                        "No Pass-1 winner; Pass 2 produced a strong, unambiguous candidate.");
                }

                return Clarify(dynamicWinner, dynamicRunnerUp,
                    "Pass 2 did not produce a sufficiently strong, unambiguous candidate.");
            }

            if (string.Equals(defaultWinner.TaskType, dynamicWinner.TaskType, StringComparison.OrdinalIgnoreCase))
            {
                double reinforced = Reinforce(defaultWinner.Score, dynamicWinner.Score);
                var combined = new JarvisRoutingCandidateScore
                {
                    TaskType = defaultWinner.TaskType,
                    Score = reinforced,
                    Source = "DEFAULT+DYNAMIC",
                    KnowledgeId = dynamicWinner.KnowledgeId,
                    Company = dynamicWinner.Company,
                    Reason = "Default and dynamic routing agree."
                };

                if (reinforced >= DefaultAcceptThreshold)
                {
                    return new JarvisRoutingDecision
                    {
                        Kind = JarvisRoutingDecisionKind.ResolveAfterDynamic,
                        Winner = combined,
                        RunnerUp = dynamicRunnerUp,
                        IsAmbiguous = false,
                        Reason = "Pass 2 reinforces the Pass-1 task."
                    };
                }

                return Clarify(combined, dynamicRunnerUp,
                    "Pass 1 and Pass 2 agree, but combined confidence is still insufficient.");
            }

            // Conflict: dynamic knowledge must be exceptionally strong AND far
            // enough ahead of the default result. Otherwise ask the user.
            double lead = dynamicWinner.Score - defaultWinner.Score;
            if (dynamicWinner.Score >= ConflictingDynamicThreshold &&
                lead >= ConflictingDynamicLead &&
                !dynamicAmbiguous)
            {
                return ResolvedDynamic(dynamicWinner, defaultWinner,
                    "Pass 2 conflicts with Pass 1 but is exceptionally strong and clearly ahead.");
            }

            return Clarify(defaultWinner, dynamicWinner,
                "Pass 1 and Pass 2 disagree without enough evidence for a safe automatic choice.");
        }

        internal static JarvisRoutingCandidateScore ScoreKnowledge(
            JarvisRoutingKnowledgeRecord knowledge,
            int currentCompany)
        {
            if (!IsUsableKnowledge(knowledge))
                return null;

            double score = Clamp01(knowledge.Confidence);

            if (knowledge.Company != 0 && knowledge.Company == currentCompany)
                score += CompanySpecificBonus;

            // Priority is intentionally capped. Even very large values cannot
            // turn weak semantic knowledge into a strong match by themselves.
            if (knowledge.Priority > 0)
                score += Math.Min(MaxPriorityBonus, knowledge.Priority * 0.005);

            int success = Math.Max(0, knowledge.SuccessCount);
            int fail = Math.Max(0, knowledge.FailCount);
            int total = success + fail;
            if (total > 0)
            {
                double successRate = (double)success / total;
                double evidence = Math.Min(1.0, total / 20.0);

                if (successRate >= 0.5)
                    score += MaxHistoryBonus * ((successRate - 0.5) / 0.5) * evidence;
                else
                    score -= MaxHistoryPenalty * ((0.5 - successRate) / 0.5) * evidence;
            }

            return new JarvisRoutingCandidateScore
            {
                TaskType = knowledge.TaskType == null ? string.Empty : knowledge.TaskType.Trim(),
                Score = Clamp01(score),
                Source = string.IsNullOrWhiteSpace(knowledge.Source) ? "DYNAMIC" : knowledge.Source,
                KnowledgeId = knowledge.Id,
                Company = knowledge.Company,
                Reason = "Approved CCCJROUTKNOW candidate."
            };
        }

        private static bool IsUsableKnowledge(JarvisRoutingKnowledgeRecord knowledge)
        {
            if (knowledge == null || !knowledge.IsActive || string.IsNullOrWhiteSpace(knowledge.TaskType))
                return false;

            // Dynamic knowledge may route only to a registered business task.
            return JarvisTaskRegistry.Find(knowledge.TaskType) != null;
        }

        private static JarvisRoutingCandidateScore[] Rank(IEnumerable<JarvisRoutingCandidateScore> candidates)
        {
            return (candidates ?? Enumerable.Empty<JarvisRoutingCandidateScore>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.TaskType))
                .Select(x => new JarvisRoutingCandidateScore
                {
                    TaskType = x.TaskType.Trim(),
                    Score = Clamp01(x.Score),
                    Source = x.Source,
                    KnowledgeId = x.KnowledgeId,
                    Company = x.Company,
                    Reason = x.Reason
                })
                .GroupBy(x => x.TaskType, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(x => x.Score)
                .ToArray();
        }

        private static bool IsAmbiguous(
            JarvisRoutingCandidateScore winner,
            JarvisRoutingCandidateScore runnerUp)
        {
            if (winner == null || runnerUp == null)
                return false;

            return (winner.Score - runnerUp.Score) < AmbiguityMargin;
        }

        private static double Reinforce(double defaultScore, double dynamicScore)
        {
            // Conservative evidence combination: dynamic evidence can strengthen
            // the default answer but cannot add its full score linearly.
            return Clamp01(defaultScore + ((1.0 - defaultScore) * dynamicScore * 0.50));
        }

        private static JarvisRoutingDecision ResolvedDynamic(
            JarvisRoutingCandidateScore winner,
            JarvisRoutingCandidateScore runnerUp,
            string reason)
        {
            return new JarvisRoutingDecision
            {
                Kind = JarvisRoutingDecisionKind.ResolveAfterDynamic,
                Winner = winner,
                RunnerUp = runnerUp,
                IsAmbiguous = false,
                Reason = reason
            };
        }

        private static JarvisRoutingDecision Clarify(
            JarvisRoutingCandidateScore winner,
            JarvisRoutingCandidateScore runnerUp,
            string reason)
        {
            return new JarvisRoutingDecision
            {
                Kind = JarvisRoutingDecisionKind.ClarifyWithUser,
                Winner = winner,
                RunnerUp = runnerUp,
                IsAmbiguous = true,
                Reason = reason
            };
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }
    }
}
