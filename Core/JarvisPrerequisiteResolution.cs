using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    internal enum JarvisPrerequisiteResolutionKind
    {
        ResolvedFromIntent,
        ResolvedFromRouting,
        LookupPlanned,
        DependencyPending,
        OwnerAgentPending,
        NeedsUserInput,
        Invalid
    }

    internal sealed class JarvisPrerequisiteLookupDefinition
    {
        public string Source { get; set; }
        public string Strategy { get; set; }
        public string[] Tools { get; set; }
        public string Output { get; set; }
        public string AmbiguityPolicy { get; set; }
    }

    internal sealed class JarvisPrerequisiteResolutionItem
    {
        public string InputName { get; set; }
        public bool Required { get; set; }
        public JarvisPrerequisiteResolutionKind Kind { get; set; }
        public JToken Value { get; set; }
        public JarvisPrerequisiteLookupDefinition Lookup { get; set; }
        public string Reason { get; set; }
    }

    internal sealed class JarvisValidatedTaskNode
    {
        public JarvisValidatedTaskNode()
        {
            Prerequisites = new List<JarvisPrerequisiteResolutionItem>();
            ValidationIssues = new List<string>();
        }

        public string ObjectId { get; set; }
        public string IntentFragment { get; set; }
        public string TaskType { get; set; }
        public JarvisTaskDescriptor Descriptor { get; set; }
        public List<JarvisPrerequisiteResolutionItem> Prerequisites { get; private set; }
        public List<string> ValidationIssues { get; private set; }

        public bool IsStructurallyValid { get { return Descriptor != null && ValidationIssues.Count == 0 && Prerequisites.All(x => x != null && x.Kind != JarvisPrerequisiteResolutionKind.Invalid); } }
        public bool NeedsUserInput { get { return Prerequisites.Any(x => x != null && x.Kind == JarvisPrerequisiteResolutionKind.NeedsUserInput); } }
        public bool HasLookupWork { get { return Prerequisites.Any(x => x != null && x.Kind == JarvisPrerequisiteResolutionKind.LookupPlanned); } }
        public bool HasOwnerAgentWork { get { return Prerequisites.Any(x => x != null && x.Kind == JarvisPrerequisiteResolutionKind.OwnerAgentPending); } }
        public bool HasPendingDependencies { get { return Prerequisites.Any(x => x != null && x.Kind == JarvisPrerequisiteResolutionKind.DependencyPending); } }
        public bool ReadyForDependencyBinding { get { return IsStructurallyValid && !NeedsUserInput; } }
    }

    internal static class JarvisPrerequisiteResolution
    {
        internal static JarvisValidatedTaskNode BuildNode(JarvisIntentObject intentObject)
        {
            var node = new JarvisValidatedTaskNode();
            if (intentObject == null) { node.ValidationIssues.Add("Intent object is null."); return node; }
            node.ObjectId = intentObject.ObjectId;
            node.IntentFragment = intentObject.IntentFragment;
            node.TaskType = intentObject.ResolvedTaskType;
            if (!intentObject.IsResolved)
            {
                node.ValidationIssues.Add("Intent object '" + (intentObject.ObjectId ?? string.Empty) + "' is not routing-resolved and cannot enter prerequisite planning.");
                return node;
            }
            JarvisTaskDescriptor descriptor = JarvisTaskRegistry.Find(intentObject.ResolvedTaskType);
            if (descriptor == null) { node.ValidationIssues.Add("Resolved task is not registered: " + intentObject.ResolvedTaskType); return node; }
            node.Descriptor = descriptor;
            ValidateIntentInputs(intentObject, descriptor, node);
            foreach (string requiredInput in descriptor.RequiredInputs)
            {
                JarvisPrerequisiteResolutionItem item = ResolveRequiredInput(intentObject, descriptor, requiredInput);
                node.Prerequisites.Add(item);
                if (item == null || item.Kind == JarvisPrerequisiteResolutionKind.Invalid)
                    node.ValidationIssues.Add("Invalid prerequisite contract for " + descriptor.TaskType + "." + requiredInput);
            }
            foreach (string optionalInput in descriptor.OptionalInputs ?? new string[0])
            {
                JToken supplied;
                if (!intentObject.InputHints.TryGetValue(optionalInput, out supplied) || !HasValue(supplied)) continue;
                if (IsSemanticDependencyMarker(descriptor.TaskType, optionalInput, supplied))
                {
                    node.Prerequisites.Add(new JarvisPrerequisiteResolutionItem
                    {
                        InputName = optionalInput,
                        Required = false,
                        Kind = JarvisPrerequisiteResolutionKind.DependencyPending,
                        Reason = "Structured semantic marker requires an explicit registered upstream binding."
                    });
                }
                else
                {
                    node.Prerequisites.Add(new JarvisPrerequisiteResolutionItem
                    {
                        InputName = optionalInput,
                        Required = false,
                        Kind = JarvisPrerequisiteResolutionKind.ResolvedFromIntent,
                        Value = supplied.DeepClone(),
                        Reason = "Optional structured semantic input is explicitly present in this intent object."
                    });
                }
            }
            return node;
        }

        internal static IReadOnlyList<JarvisValidatedTaskNode> BuildNodes(JarvisIntentObjectSet objectSet)
        {
            if (objectSet == null) return new JarvisValidatedTaskNode[0];
            return objectSet.Objects.Where(x => x != null && x.IsResolved).Select(BuildNode).ToArray();
        }

        private static void ValidateIntentInputs(JarvisIntentObject intentObject, JarvisTaskDescriptor descriptor, JarvisValidatedTaskNode node)
        {
            var allowed = new HashSet<string>(descriptor.RequiredInputs.Concat(descriptor.OptionalInputs), StringComparer.OrdinalIgnoreCase);
            foreach (string name in intentObject.InputHints.Keys)
                if (!allowed.Contains(name))
                    node.ValidationIssues.Add("Intent object '" + intentObject.ObjectId + "' supplied unknown input '" + name + "' for task " + descriptor.TaskType + ".");
        }

        private static JarvisPrerequisiteResolutionItem ResolveRequiredInput(JarvisIntentObject intentObject, JarvisTaskDescriptor descriptor, string inputName)
        {
            JToken supplied;
            if (intentObject.InputHints.TryGetValue(inputName, out supplied) && HasValue(supplied))
                return new JarvisPrerequisiteResolutionItem { InputName = inputName, Required = true, Kind = JarvisPrerequisiteResolutionKind.ResolvedFromIntent, Value = supplied.DeepClone(), Reason = "Value is explicitly present in this intent object." };

            JarvisPrerequisiteResolutionItem deterministic = ResolveDeterministicIntentValue(intentObject, descriptor, inputName);
            if (deterministic != null) return deterministic;

            // Composition inputs are deliberately dependency-first. A task that says
            // "send its/their information" must consume a registered upstream result;
            // Jarvis must not ask the user to retype content already produced by another object.
            if (IsCompositionDependency(descriptor.TaskType, inputName))
                return new JarvisPrerequisiteResolutionItem { InputName = inputName, Required = true, Kind = JarvisPrerequisiteResolutionKind.DependencyPending, Reason = "Content is expected from another autonomous task object and must be bound through a registered dependency rule." };

            JarvisPrerequisiteLookupDefinition explicitLookup = FindExplicitLookup(descriptor.TaskType, inputName);
            if (explicitLookup != null)
                return new JarvisPrerequisiteResolutionItem { InputName = inputName, Required = true, Kind = JarvisPrerequisiteResolutionKind.LookupPlanned, Lookup = explicitLookup, Reason = "Required input has an explicit Jarvis lookup definition." };

            JarvisPrerequisiteLookupDefinition inventoryLookup = FindInventoryLookup(descriptor, inputName);
            if (inventoryLookup != null)
                return new JarvisPrerequisiteResolutionItem { InputName = inputName, Required = true, Kind = JarvisPrerequisiteResolutionKind.LookupPlanned, Lookup = inventoryLookup, Reason = "Required input can be produced by an upstream tool declared in the tool prerequisite inventory." };

            // A logical task contract and a native tool-call argument contract are
            // not the same boundary. These values are intentionally left for the
            // registered owner agent to materialize from its atomic intent fragment,
            // current session context and ONLY the tools registered for that task.
            // They must not be converted into fake cross-object dependencies and
            // they must not make Jarvis claim that a capability is unavailable.
            if (IsOwnerAgentResolvable(descriptor.TaskType, inputName))
                return new JarvisPrerequisiteResolutionItem
                {
                    InputName = inputName,
                    Required = true,
                    Kind = JarvisPrerequisiteResolutionKind.OwnerAgentPending,
                    Reason = "Task-local execution argument is resolved by the registered owner agent from the atomic intent fragment and its scoped tools/context."
                };

            if (descriptor.DependencyCapabilities != null && descriptor.DependencyCapabilities.Length > 0)
                return new JarvisPrerequisiteResolutionItem { InputName = inputName, Required = true, Kind = JarvisPrerequisiteResolutionKind.DependencyPending, Reason = "No local literal/lookup is available yet; task declares upstream dependency capabilities. Cross-object binding runs later." };

            return new JarvisPrerequisiteResolutionItem { InputName = inputName, Required = true, Kind = JarvisPrerequisiteResolutionKind.NeedsUserInput, Reason = "Required business input is not present and no safe lookup/dependency/owner-agent path is registered." };
        }

        private static bool IsSemanticDependencyMarker(string taskType, string inputName, JToken value)
        {
            if (value == null || value.Type != JTokenType.String) return false;
            string marker = value.ToString();
            if (string.Equals(taskType, "ExportData", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(inputName, "source_result", StringComparison.OrdinalIgnoreCase))
                return string.Equals(marker, "__UPSTREAM_REPORT__", StringComparison.Ordinal);
            if (string.Equals(taskType, "SendEmail", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(inputName, "artifact_reference", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(inputName, "attachmentFilePath", StringComparison.OrdinalIgnoreCase)))
                return string.Equals(marker, "__UPSTREAM_EXPORT__", StringComparison.Ordinal);
            return false;
        }

        private static bool IsCompositionDependency(string taskType, string inputName)
        {
            return string.Equals(taskType, "SendEmail", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(inputName, "body", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOwnerAgentResolvable(string taskType, string inputName)
        {
            string key = (taskType ?? string.Empty) + ":" + (inputName ?? string.Empty);
            switch (key.ToLowerInvariant())
            {
                // Recipient is normally present in the atomic fragment as a literal
                // email address/name and Echo owns any contact lookup needed to resolve it.
                case "sendemail:to":
                    return true;

                // CRM/calendar wording is intentionally natural-language. Echo owns
                // conversion of the fragment into title/description/date/subject/start
                // and may use the scoped query/contact tools for identity resolution.
                case "createcrmtask:title":
                case "createcrmtask:description":
                case "createcrmtask:fromdate":
                case "createcrmtask:assignee":
                case "createcalendarevent:subject":
                case "createcalendarevent:start":
                    return true;

                default:
                    return false;
            }
        }

        private static JarvisPrerequisiteResolutionItem ResolveDeterministicIntentValue(JarvisIntentObject intentObject, JarvisTaskDescriptor descriptor, string inputName)
        {
            if (string.Equals(inputName, "sourceInstruction", StringComparison.OrdinalIgnoreCase))
                return ResolvedRoutingValue(inputName, new JValue(intentObject.IntentFragment ?? string.Empty), "Source instruction is the autonomous intent fragment itself.");
            if (string.Equals(inputName, "confidence", StringComparison.OrdinalIgnoreCase))
            {
                double score = intentObject.RoutingDecision != null && intentObject.RoutingDecision.Winner != null ? intentObject.RoutingDecision.Winner.Score : 0.0;
                return ResolvedRoutingValue(inputName, new JValue(score), "Confidence comes from the validated routing decision, not from user input.");
            }
            if (string.Equals(descriptor.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) && string.Equals(inputName, "subject", StringComparison.OrdinalIgnoreCase))
                return ResolvedRoutingValue(inputName, new JValue("S1 Jarvis"), "No explicit subject was supplied; the email task uses the neutral Jarvis subject rather than requiring redundant user input.");
            if (IsIntentTextContract(descriptor.TaskType, inputName))
                return ResolvedRoutingValue(inputName, new JValue(intentObject.IntentFragment ?? string.Empty), "This task contract consumes the intent fragment as its business request/question.");
            return null;
        }

        private static bool IsIntentTextContract(string taskType, string inputName)
        {
            string key = (taskType ?? string.Empty) + ":" + (inputName ?? string.Empty);
            switch (key.ToLowerInvariant())
            {
                case "reportdata:business_question": case "exportdata:export_request": case "findtrader:trader_identity": case "readinbox:email_request": case "readcalendar:calendar_request": case "courierdocuments:courier_request": case "internetresearch:research_question": case "helplookup:help_question": return true;
                default: return false;
            }
        }

        private static JarvisPrerequisiteResolutionItem ResolvedRoutingValue(string inputName, JToken value, string reason)
        {
            return new JarvisPrerequisiteResolutionItem { InputName = inputName, Required = true, Kind = JarvisPrerequisiteResolutionKind.ResolvedFromRouting, Value = value, Reason = reason };
        }

        private static JarvisPrerequisiteLookupDefinition FindExplicitLookup(string taskType, string inputName)
        {
            string key = (taskType ?? string.Empty) + ":" + (inputName ?? string.Empty);
            switch (key.ToLowerInvariant())
            {
                case "createorder:trdrid": return L("Soft1", "trader_by_identity", A("find_trader_by_afm", "query_data"), "trdrId", "ask_user");
                case "createorder:lines": return L("Soft1", "order_lines_item_by_code_or_name", A("query_data"), "lines", "ask_user");
                case "createorder:series": return L("Soft1/config", "series_for_sosource", A("query_data"), "series", "ask_user");
                case "createorder:sosource": return L("business_rule", "document_type_from_intent", A(), "sosource", "ask_user");
                case "createcrmtask:assignee": return L("Soft1", "soft1_user_or_assignee_identity", A("query_data"), "assignee", "ask_user");
                case "resolvedocumentconversion:findoc": return L("Soft1", "document_reference_to_findoc", A("query_data", "open_document"), "findoc", "ask_user");
                case "replyemail:messageid": return L("Outlook", "message_identity", A("filter_email_inbox", "read_email"), "messageId", "ask_user");
                case "createcouriervoucher:findocid": case "cancelcouriervoucher:findocid": return L("Soft1", "document_reference_to_findoc", A("query_data", "show_courier_documents"), "findocId", "ask_user");
                default: return null;
            }
        }

        private static JarvisPrerequisiteLookupDefinition FindInventoryLookup(JarvisTaskDescriptor descriptor, string inputName)
        {
            if (descriptor == null || string.IsNullOrWhiteSpace(inputName)) return null;
            var allowedUpstream = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string toolName in descriptor.Tools ?? new string[0])
            {
                JarvisToolPrerequisiteDescriptor prerequisite = JarvisToolRegistry.FindPrerequisites(toolName);
                if (prerequisite == null) continue;
                foreach (string upstream in prerequisite.UpstreamTools ?? new string[0]) allowedUpstream.Add(upstream);
            }
            foreach (string upstreamTool in allowedUpstream)
            {
                JarvisToolPrerequisiteDescriptor upstream = JarvisToolRegistry.FindPrerequisites(upstreamTool);
                if (upstream == null) continue;
                string produced = (upstream.Produces ?? new string[0]).FirstOrDefault(x => string.Equals(x, inputName, StringComparison.OrdinalIgnoreCase));
                if (produced != null) return L("tool_inventory", "resolve_" + inputName + "_via_" + upstreamTool, A(upstreamTool), produced, "ask_user");
            }
            return null;
        }

        private static bool HasValue(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null || value.Type == JTokenType.Undefined) return false;
            if (value.Type == JTokenType.String) return !string.IsNullOrWhiteSpace(value.ToString());
            return true;
        }
        private static JarvisPrerequisiteLookupDefinition L(string source, string strategy, string[] tools, string output, string ambiguityPolicy)
        {
            return new JarvisPrerequisiteLookupDefinition { Source = source ?? string.Empty, Strategy = strategy ?? string.Empty, Tools = tools ?? new string[0], Output = output ?? string.Empty, AmbiguityPolicy = ambiguityPolicy ?? "ask_user" };
        }
        private static string[] A(params string[] values) { return values ?? new string[0]; }
    }
}
