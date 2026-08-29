using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    internal enum JarvisIntentObjectStatus
    {
        Pending,
        Resolved,
        NeedsDynamicPass,
        NeedsClarification,
        Invalid
    }

    internal sealed class JarvisIntentObject
    {
        public JarvisIntentObject()
        {
            CandidateScores = new List<JarvisRoutingCandidateScore>();
            ValidationIssues = new List<string>();
            Status = JarvisIntentObjectStatus.Pending;
            RoutingPass = JarvisRoutingResolutionPass.DefaultMetadata;
        }

        public string ObjectId { get; set; }
        public string IntentFragment { get; set; }
        public List<JarvisRoutingCandidateScore> CandidateScores { get; private set; }
        public JarvisRoutingDecision RoutingDecision { get; set; }
        public JarvisRoutingResolutionPass RoutingPass { get; set; }
        public JarvisIntentObjectStatus Status { get; set; }
        public string ResolvedTaskType { get; set; }
        public List<string> ValidationIssues { get; private set; }

        public bool IsResolved
        {
            get
            {
                return Status == JarvisIntentObjectStatus.Resolved &&
                    !string.IsNullOrWhiteSpace(ResolvedTaskType) &&
                    ValidationIssues.Count == 0;
            }
        }
    }

    internal sealed class JarvisIntentObjectSet
    {
        public JarvisIntentObjectSet(string originalPrompt)
        {
            OriginalPrompt = originalPrompt ?? string.Empty;
            Objects = new List<JarvisIntentObject>();
        }

        public string OriginalPrompt { get; private set; }
        public List<JarvisIntentObject> Objects { get; private set; }

        public bool AllResolved
        {
            get { return Objects.Count > 0 && Objects.All(x => x != null && x.IsResolved); }
        }

        public bool NeedsClarification
        {
            get { return Objects.Any(x => x != null && x.Status == JarvisIntentObjectStatus.NeedsClarification); }
        }

        public bool NeedsDynamicPass
        {
            get { return Objects.Any(x => x != null && x.Status == JarvisIntentObjectStatus.NeedsDynamicPass); }
        }
    }

    /// <summary>
    /// Step 6 orchestration contract.
    ///
    /// A user prompt is decomposed once into autonomous Intent Objects. Each
    /// object then runs through its own routing lifecycle (Pass 1 -> Pass 2 ->
    /// Pass 3) and is validated independently. Only resolved/validated objects
    /// may later participate in prerequisite resolution and dependency binding.
    ///
    /// This class deliberately does NOT execute tools and does NOT build the
    /// final task DAG. It establishes the safe semantic boundary that must exist
    /// before Main Chat is connected to the new orchestration path.
    /// </summary>
    internal static class JarvisIntentOrchestration
    {
        internal static string BuildDecomposerSystemPrompt()
        {
            return
                "Είσαι ο semantic decomposer του Jarvis. Σπάσε το user prompt σε ανεξάρτητα atomic intent objects. " +
                "Μην εκτελείς εργαλεία, μην απαντάς στον χρήστη και μην δημιουργείς dependencies σε αυτό το στάδιο. " +
                "Κάθε object πρέπει να αντιστοιχεί σε ένα μόνο business outcome. " +
                "Για κάθε object επέστρεψε ranked task candidates ΜΟΝΟ από το TASK_CATALOG. " +
                "Μην επινοείς agents, tools, capabilities ή task types. " +
                "Δώσε confidence από 0.0 έως 1.0 για κάθε candidate και κράτησε το intentFragment όσο γίνεται πιστό στο αντίστοιχο κομμάτι του prompt. " +
                "Αν ένα σύνθετο prompt ζητά παραγγελία, export, email και calendar, δημιούργησε τέσσερα διαφορετικά objects. " +
                "Μην βάζεις whole-prompt confidence. Κάθε object έχει το δικό του confidence/ranking. " +
                "Επέστρεψε ΜΟΝΟ έγκυρο JSON στο schema: " +
                "{\"intentObjects\":[{\"id\":\"o1\",\"intentFragment\":\"...\",\"candidates\":[{\"taskType\":\"CreateOrder\",\"confidence\":0.94},{\"taskType\":\"...\",\"confidence\":0.20}]}]}";
        }

        internal static string BuildDecomposerUserPayload(string userPrompt)
        {
            return new JObject
            {
                ["userPrompt"] = userPrompt ?? string.Empty,
                ["catalog"] = JObject.Parse(JarvisSemanticPlanning.BuildTaskCatalogJson())["TASK_CATALOG"]
            }.ToString(Formatting.None);
        }

        internal static bool TryParsePass1(
            string responseJson,
            string originalPrompt,
            out JarvisIntentObjectSet objectSet,
            out string[] issues)
        {
            objectSet = new JarvisIntentObjectSet(originalPrompt);
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(responseJson))
            {
                errors.Add("Intent decomposer returned an empty response.");
                issues = errors.ToArray();
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(responseJson);
            }
            catch (JsonException ex)
            {
                errors.Add("Intent decomposer returned invalid JSON: " + ex.Message);
                issues = errors.ToArray();
                return false;
            }

            JArray array = root["intentObjects"] as JArray;
            if (array == null)
            {
                errors.Add("Intent decomposer response is missing intentObjects array.");
                issues = errors.ToArray();
                return false;
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JObject item in array.OfType<JObject>())
            {
                JarvisIntentObject intentObject = ParseIntentObject(item, errors);
                if (intentObject == null)
                    continue;

                if (!ids.Add(intentObject.ObjectId))
                {
                    errors.Add("Duplicate intent object id: " + intentObject.ObjectId);
                    continue;
                }

                EvaluatePass1(intentObject);
                objectSet.Objects.Add(intentObject);
            }

            if (objectSet.Objects.Count == 0)
                errors.Add("Intent decomposer produced no usable intent objects.");

            issues = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return issues.Length == 0;
        }

        internal static void ApplyDynamicPass(
            JarvisIntentObject intentObject,
            IEnumerable<JarvisRoutingKnowledgeRecord> knowledge,
            int currentCompany)
        {
            if (intentObject == null || intentObject.Status != JarvisIntentObjectStatus.NeedsDynamicPass)
                return;

            JarvisRoutingDecision decision = JarvisRoutingDecisionPolicy.EvaluateAfterDynamic(
                intentObject.CandidateScores,
                knowledge,
                currentCompany);

            intentObject.RoutingDecision = decision;
            intentObject.RoutingPass = JarvisRoutingResolutionPass.DynamicKnowledge;

            if (decision != null &&
                decision.Kind == JarvisRoutingDecisionKind.ResolveAfterDynamic &&
                decision.Winner != null &&
                JarvisTaskRegistry.Find(decision.Winner.TaskType) != null)
            {
                intentObject.ResolvedTaskType = decision.Winner.TaskType;
                intentObject.Status = JarvisIntentObjectStatus.Resolved;
                return;
            }

            intentObject.ResolvedTaskType = null;
            intentObject.Status = JarvisIntentObjectStatus.NeedsClarification;
            intentObject.RoutingPass = JarvisRoutingResolutionPass.UserClarification;
        }

        internal static void ApplyDynamicPass(
            JarvisIntentObjectSet objectSet,
            XSupport xSupport)
        {
            if (objectSet == null || xSupport == null || xSupport.ConnectionInfo == null)
                return;

            JarvisIntentObject[] pending = objectSet.Objects
                .Where(x => x != null && x.Status == JarvisIntentObjectStatus.NeedsDynamicPass)
                .ToArray();
            if (pending.Length == 0)
                return;

            IReadOnlyList<JarvisRoutingKnowledgeRecord> knowledge =
                JarvisDynamicRoutingRepository.LoadActiveKnowledge(xSupport);
            int currentCompany = xSupport.ConnectionInfo.CompanyId;

            foreach (JarvisIntentObject intentObject in pending)
            {
                IEnumerable<JarvisRoutingKnowledgeRecord> relevant = FilterKnowledgeForObject(
                    knowledge,
                    intentObject);
                ApplyDynamicPass(intentObject, relevant, currentCompany);
            }
        }

        internal static string BuildClarificationDiagnostic(JarvisIntentObject intentObject)
        {
            if (intentObject == null)
                return string.Empty;

            JarvisRoutingCandidateScore winner = intentObject.RoutingDecision == null
                ? null
                : intentObject.RoutingDecision.Winner;
            JarvisRoutingCandidateScore runnerUp = intentObject.RoutingDecision == null
                ? null
                : intentObject.RoutingDecision.RunnerUp;

            return new JObject
            {
                ["objectId"] = intentObject.ObjectId ?? string.Empty,
                ["intentFragment"] = intentObject.IntentFragment ?? string.Empty,
                ["winner"] = winner == null ? null : winner.TaskType,
                ["winnerScore"] = winner == null ? (double?)null : winner.Score,
                ["runnerUp"] = runnerUp == null ? null : runnerUp.TaskType,
                ["runnerUpScore"] = runnerUp == null ? (double?)null : runnerUp.Score,
                ["reason"] = intentObject.RoutingDecision == null ? string.Empty : intentObject.RoutingDecision.Reason
            }.ToString(Formatting.None);
        }

        private static JarvisIntentObject ParseIntentObject(JObject item, List<string> errors)
        {
            string id = item["id"] == null ? string.Empty : item["id"].ToString().Trim();
            string fragment = item["intentFragment"] == null
                ? string.Empty
                : item["intentFragment"].ToString().Trim();

            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add("Intent object without id.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(fragment))
            {
                errors.Add("Intent object '" + id + "' has empty intentFragment.");
                return null;
            }

            var result = new JarvisIntentObject
            {
                ObjectId = id,
                IntentFragment = fragment
            };

            JArray candidates = item["candidates"] as JArray;
            if (candidates == null || candidates.Count == 0)
            {
                result.ValidationIssues.Add("No candidates returned for intent object " + id + ".");
                result.Status = JarvisIntentObjectStatus.Invalid;
                return result;
            }

            foreach (JObject candidate in candidates.OfType<JObject>())
            {
                string taskType = candidate["taskType"] == null
                    ? string.Empty
                    : candidate["taskType"].ToString().Trim();

                double confidence;
                if (string.IsNullOrWhiteSpace(taskType) || JarvisTaskRegistry.Find(taskType) == null)
                {
                    result.ValidationIssues.Add("Unknown taskType candidate '" + taskType + "'.");
                    continue;
                }

                if (!TryReadConfidence(candidate["confidence"], out confidence))
                {
                    result.ValidationIssues.Add("Invalid confidence for taskType '" + taskType + "'.");
                    continue;
                }

                result.CandidateScores.Add(new JarvisRoutingCandidateScore
                {
                    TaskType = taskType,
                    Score = confidence,
                    Source = "DEFAULT",
                    Company = 0,
                    Reason = "Semantic Pass-1 candidate for one autonomous intent object."
                });
            }

            if (result.CandidateScores.Count == 0)
            {
                result.ValidationIssues.Add("No valid registered task candidates remain after validation.");
                result.Status = JarvisIntentObjectStatus.Invalid;
            }

            return result;
        }

        private static void EvaluatePass1(JarvisIntentObject intentObject)
        {
            if (intentObject == null || intentObject.Status == JarvisIntentObjectStatus.Invalid)
                return;

            if (intentObject.ValidationIssues.Count > 0)
            {
                intentObject.Status = JarvisIntentObjectStatus.Invalid;
                return;
            }

            JarvisRoutingDecision decision = JarvisRoutingDecisionPolicy.EvaluateDefault(
                intentObject.CandidateScores);
            intentObject.RoutingDecision = decision;
            intentObject.RoutingPass = JarvisRoutingResolutionPass.DefaultMetadata;

            if (decision != null &&
                decision.Kind == JarvisRoutingDecisionKind.ResolveFromDefault &&
                decision.Winner != null &&
                JarvisTaskRegistry.Find(decision.Winner.TaskType) != null)
            {
                intentObject.ResolvedTaskType = decision.Winner.TaskType;
                intentObject.Status = JarvisIntentObjectStatus.Resolved;
            }
            else
            {
                intentObject.ResolvedTaskType = null;
                intentObject.Status = JarvisIntentObjectStatus.NeedsDynamicPass;
            }
        }

        private static IEnumerable<JarvisRoutingKnowledgeRecord> FilterKnowledgeForObject(
            IEnumerable<JarvisRoutingKnowledgeRecord> knowledge,
            JarvisIntentObject intentObject)
        {
            if (intentObject == null)
                return Enumerable.Empty<JarvisRoutingKnowledgeRecord>();

            string fragment = intentObject.IntentFragment ?? string.Empty;
            HashSet<string> candidateTypes = new HashSet<string>(
                intentObject.CandidateScores.Select(x => x.TaskType),
                StringComparer.OrdinalIgnoreCase);

            return (knowledge ?? Enumerable.Empty<JarvisRoutingKnowledgeRecord>())
                .Where(x => x != null && x.IsActive)
                .Where(x =>
                    candidateTypes.Contains(x.TaskType ?? string.Empty) ||
                    ContainsMeaningfulText(fragment, x.PromptText) ||
                    ContainsMeaningfulText(fragment, x.IntentDescription))
                .ToArray();
        }

        private static bool ContainsMeaningfulText(string fragment, string pattern)
        {
            if (string.IsNullOrWhiteSpace(fragment) || string.IsNullOrWhiteSpace(pattern))
                return false;

            string left = fragment.Trim();
            string right = pattern.Trim();
            if (right.Length < 3)
                return false;

            return left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0 ||
                right.IndexOf(left, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryReadConfidence(JToken token, out double value)
        {
            value = 0.0;
            if (token == null)
                return false;

            try
            {
                value = token.Value<double>();
            }
            catch
            {
                return false;
            }

            return !double.IsNaN(value) &&
                !double.IsInfinity(value) &&
                value >= 0.0 &&
                value <= 1.0;
        }
    }
}
