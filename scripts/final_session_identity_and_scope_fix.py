from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


# 1) Central policies: session operator + CRM default self-assignment + explicit document scope.
path = "Core/JarvisPolicyRegistry.cs"
text = read(path)
anchor = '            P("GLOBAL.RESULTS_RETURN_TO_JARVIS", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,\n'
insert = '''            P("GLOBAL.AUTHENTICATED_SESSION_OPERATOR", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Both,
                "Κατά την ενεργοποίηση του Jarvis, ο authenticated Soft1 user και company καταγράφονται ως authoritative session identity. Αυτός ο user είναι ο current operator/interlocutor για όλη τη συνεδρία. First-person/self references δεν ζητούν ξανά ταυτότητα από τον χειριστή και δεν επιτρέπεται model guess ή αλλαγή identity χωρίς νέο authenticated session.", priority: 982),

            P("CRM.DEFAULT_ASSIGNEE_CURRENT_OPERATOR", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "CreateCrmTask χωρίς ρητά διαφορετικό assignee ανατίθεται στον authenticated current operator της session. Το orchestration materializes __CURRENT_OPERATOR__ και deterministic actorUserId evidence από τη session identity πριν από tool validation. Αν ο χρήστης ορίσει ρητά άλλον assignee, απαιτείται κανονικό identity resolution και δεν γίνεται silent fallback στον current operator.", agents: A("Jarvis", "Echo"), tasks: A("CreateCrmTask"), domains: A("CRM"), tools: A("create_crm_task"), priority: 981),

            P("DOCUMENT.EXPLICIT_SCOPE_IS_BINDING", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Όταν το user request δηλώνει ρητά μία semantic document category (π.χ. invoice/order/quotation/credit/delivery), το canonical document_scope είναι binding constraint για κάθε σχετικό ReportData/ExportData node. Η κατηγορία δεν επιτρέπεται να χαθεί κατά decomposition/composition και το returned dataset πρέπει να απορρίπτεται fail-closed αν περιέχει άλλη document category.", agents: A("Jarvis", "Atlas"), tasks: A("ReportData", "ExportData"), domains: A("Reporting", "Soft1Documents"), priority: 980),

'''
text = replace_once(text, anchor, insert + anchor, "policy insertion")
write(path, text)


# 2) Runtime context becomes session-scoped and is captured once at Jarvis activation.
path = "Core/JarvisRuntimeContext.cs"
write(path, '''using System;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Authoritative identity/runtime facts for one active Soft1/Jarvis session.
    /// The context is registered once when the Jarvis shell is activated and then
    /// reused by decomposition, orchestration and executors. It is never inferred
    /// by a model and must not be re-requested from the operator.
    /// </summary>
    internal sealed class JarvisRuntimeContext
    {
        private static readonly ConditionalWeakTable<XSupport, JarvisRuntimeContext> Sessions =
            new ConditionalWeakTable<XSupport, JarvisRuntimeContext>();
        private static readonly object Sync = new object();

        internal int CurrentUserId { get; private set; }
        internal int CurrentCompanyId { get; private set; }
        internal string CurrentUserDisplayName { get; private set; }
        internal int CurrentInterlocutorUserId { get; private set; }
        internal DateTime ActivatedAtLocal { get; private set; }
        internal DateTime LocalNow { get { return DateTime.Now; } }

        internal static JarvisRuntimeContext StartSession(XSupport xSupport)
        {
            if (xSupport == null) return Create(null);
            lock (Sync)
            {
                JarvisRuntimeContext existing;
                if (Sessions.TryGetValue(xSupport, out existing)) return existing;
                JarvisRuntimeContext created = Create(xSupport);
                Sessions.Add(xSupport, created);
                DebugLog.Log("[JARVIS-SESSION] activated currentUserId=" + created.CurrentUserId +
                    " currentCompanyId=" + created.CurrentCompanyId +
                    " displayName=" + (created.CurrentUserDisplayName ?? string.Empty));
                return created;
            }
        }

        internal static JarvisRuntimeContext Capture(XSupport xSupport)
        {
            if (xSupport == null) return Create(null);
            lock (Sync)
            {
                JarvisRuntimeContext existing;
                if (Sessions.TryGetValue(xSupport, out existing)) return existing;
            }
            // Compatibility fallback for a non-shell call path. Normal Main Chat
            // always registers the session explicitly from JarvisShell.Loaded.
            return StartSession(xSupport);
        }

        private static JarvisRuntimeContext Create(XSupport xSupport)
        {
            var context = new JarvisRuntimeContext
            {
                ActivatedAtLocal = DateTime.Now,
                CurrentUserDisplayName = string.Empty
            };
            if (xSupport == null || xSupport.ConnectionInfo == null) return context;

            context.CurrentUserId = xSupport.ConnectionInfo.UserId;
            context.CurrentInterlocutorUserId = context.CurrentUserId;
            context.CurrentCompanyId = xSupport.ConnectionInfo.CompanyId;
            try { context.CurrentUserDisplayName = JarvisTools.GetCurrentUserDisplayName(xSupport); }
            catch { context.CurrentUserDisplayName = string.Empty; }
            return context;
        }

        internal JObject ToJson()
        {
            return new JObject
            {
                ["source"] = "authenticated_soft1_session",
                ["currentUserId"] = CurrentUserId,
                ["currentInterlocutorUserId"] = CurrentInterlocutorUserId,
                ["currentUserDisplayName"] = CurrentUserDisplayName ?? string.Empty,
                ["currentCompanyId"] = CurrentCompanyId,
                ["activatedAtLocal"] = ActivatedAtLocal.ToString("o"),
                ["localDateTime"] = LocalNow.ToString("o")
            };
        }

        internal string BuildEnvelope()
        {
            return "[JARVIS_RUNTIME_CONTEXT]\\n" + ToJson().ToString(Formatting.None);
        }
    }
}
''')


# 3) Shell registers identity at activation and passes the exact session context into controlled orchestration.
path = "UI/JarvisShell.OrchestrationShadow.cs"
text = read(path)
text = replace_once(text,
'''        private static void OnLoaded(object sender, RoutedEventArgs e)\n        {\n            JarvisShell shell = sender as JarvisShell;\n            if (shell != null) shell.InstallOrchestrationShadowHook();\n        }''',
'''        private static void OnLoaded(object sender, RoutedEventArgs e)\n        {\n            JarvisShell shell = sender as JarvisShell;\n            if (shell == null) return;\n            shell.InitializeJarvisSessionIdentity();\n            shell.InstallOrchestrationShadowHook();\n        }''',
"shell loaded identity")
text = replace_once(text,
'''        private readonly JarvisActiveOrchestrationContext _orchestrationActiveContext = new JarvisActiveOrchestrationContext();\n\n        private bool _orchestrationShadowHookAttached;''',
'''        private readonly JarvisActiveOrchestrationContext _orchestrationActiveContext = new JarvisActiveOrchestrationContext();\n        private JarvisRuntimeContext _orchestrationSessionContext;\n\n        private bool _orchestrationShadowHookAttached;''',
"shell session field")
text = replace_once(text,
'''        internal void InstallOrchestrationShadowHook()\n        {\n            if (_orchestrationShadowHookAttached || _orchestrationShadowHookInstalling) return;\n            AttachOrchestrationShadowHandlerSafeAsync();\n        }''',
'''        internal void InitializeJarvisSessionIdentity()\n        {\n            if (_orchestrationSessionContext != null) return;\n            _orchestrationSessionContext = JarvisRuntimeContext.StartSession(_xSupport);\n        }\n\n        internal void InstallOrchestrationShadowHook()\n        {\n            if (_orchestrationSessionContext == null) InitializeJarvisSessionIdentity();\n            if (_orchestrationShadowHookAttached || _orchestrationShadowHookInstalling) return;\n            AttachOrchestrationShadowHandlerSafeAsync();\n        }''',
"shell identity method")
text = replace_once(text,
'''                JarvisControlledPilotOutcome pilot = await JarvisExecutionShadowHarness.TryRunControlledPilotAsync(\n                    _xSupport, userText, _orchestrationPendingConfirmation, _orchestrationDatasetSession, _orchestrationActiveContext);''',
'''                JarvisControlledPilotOutcome pilot = await JarvisExecutionShadowHarness.TryRunControlledPilotAsync(\n                    _xSupport, userText, _orchestrationPendingConfirmation, _orchestrationDatasetSession, _orchestrationActiveContext, _orchestrationSessionContext);''',
"shell pass session")
write(path, text)


# 4) Coordinator consumes the exact session context rather than re-identifying the operator per request.
path = "Core/JarvisOrchestrationShadowCoordinator.cs"
text = read(path)
text = replace_once(text,
'''        internal static async Task<JarvisShadowOrchestrationResult> RunAsync(\n            XSupport xSupport,\n            string userPrompt,\n            CancellationToken cancellationToken = default(CancellationToken))''',
'''        internal static async Task<JarvisShadowOrchestrationResult> RunAsync(\n            XSupport xSupport,\n            string userPrompt,\n            JarvisRuntimeContext runtimeContext = null,\n            CancellationToken cancellationToken = default(CancellationToken))''',
"coordinator run signature")
text = replace_once(text,
'''                string decomposerJson = await JarvisShadowSemanticClient.DecomposeAsync(\n                    xSupport, userPrompt, cancellationToken);''',
'''                string decomposerJson = await JarvisShadowSemanticClient.DecomposeAsync(\n                    xSupport, userPrompt, runtimeContext, cancellationToken);''',
"coordinator decompose call")
text = replace_once(text,
'''        internal static async Task<string> DecomposeAsync(\n            XSupport xSupport,\n            string userPrompt,\n            CancellationToken cancellationToken)''',
'''        internal static async Task<string> DecomposeAsync(\n            XSupport xSupport,\n            string userPrompt,\n            JarvisRuntimeContext runtimeContext,\n            CancellationToken cancellationToken)''',
"decompose signature")
text = replace_once(text,
'''                        ["content"] = JarvisIntentOrchestration.BuildDecomposerUserPayload(userPrompt, JarvisRuntimeContext.Capture(xSupport))''',
'''                        ["content"] = JarvisIntentOrchestration.BuildDecomposerUserPayload(userPrompt, runtimeContext ?? JarvisRuntimeContext.Capture(xSupport))''',
"decomposer runtime payload")
write(path, text)


# 5) CRM missing assignee deterministically means the authenticated current operator.
path = "Core/JarvisPrerequisiteResolution.cs"
text = read(path)
anchor = '''            if (string.Equals(descriptor.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) && string.Equals(inputName, "subject", StringComparison.OrdinalIgnoreCase))\n                return ResolvedRoutingValue(inputName, new JValue("S1 Jarvis"), "No explicit subject was supplied; the email task uses the neutral Jarvis subject rather than requiring redundant user input.");'''
replacement = '''            if (string.Equals(descriptor.TaskType, "CreateCrmTask", StringComparison.OrdinalIgnoreCase) && string.Equals(inputName, "assignee", StringComparison.OrdinalIgnoreCase))\n                return ResolvedRoutingValue(inputName, new JValue("__CURRENT_OPERATOR__"), "CRM.DEFAULT_ASSIGNEE_CURRENT_OPERATOR: no explicit different assignee was supplied, so the authenticated session operator is authoritative.");\n            if (string.Equals(descriptor.TaskType, "SendEmail", StringComparison.OrdinalIgnoreCase) && string.Equals(inputName, "subject", StringComparison.OrdinalIgnoreCase))\n                return ResolvedRoutingValue(inputName, new JValue("S1 Jarvis"), "No explicit subject was supplied; the email task uses the neutral Jarvis subject rather than requiring redundant user input.");'''
text = replace_once(text, anchor, replacement, "crm default assignee")
write(path, text)


# 6) Controlled harness consumes the startup identity, materializes CRM actor evidence, and uses the renamed link materializer.
path = "Core/JarvisExecutionShadowHarness.cs"
text = read(path)
text = replace_once(text,
'''        internal static async Task<JarvisControlledPilotOutcome> TryRunControlledPilotAsync(\n            XSupport xSupport, string userPrompt, JarvisPendingConfirmationSession pendingSession,\n            JarvisDatasetSession datasetSession = null, JarvisActiveOrchestrationContext activeContext = null)''',
'''        internal static async Task<JarvisControlledPilotOutcome> TryRunControlledPilotAsync(\n            XSupport xSupport, string userPrompt, JarvisPendingConfirmationSession pendingSession,\n            JarvisDatasetSession datasetSession = null, JarvisActiveOrchestrationContext activeContext = null,\n            JarvisRuntimeContext runtimeContext = null)''',
"harness signature")
text = replace_once(text,
'''                string planningPrompt = activeContext == null ? userPrompt : activeContext.PreparePrompt(userPrompt);\n                JarvisShadowOrchestrationResult planning = await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, planningPrompt);''',
'''                runtimeContext = runtimeContext ?? JarvisRuntimeContext.Capture(xSupport);\n                string planningPrompt = activeContext == null ? userPrompt : activeContext.PreparePrompt(userPrompt);\n                JarvisShadowOrchestrationResult planning = await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, planningPrompt, runtimeContext);''',
"harness session use")
text = replace_once(text, '                ResolveDeterministicRuntimeContext(planning, xSupport);', '                ResolveDeterministicRuntimeContext(planning, runtimeContext);', "runtime resolver call")
text = text.replace('JarvisRuntimeContext runtimeContext = JarvisRuntimeContext.Capture(xSupport);', 'JarvisRuntimeContext reportRuntimeContext = runtimeContext;')
text = text.replace('existingPolicyContext + "\\n" + runtimeContext.BuildEnvelope()', 'existingPolicyContext + "\\n" + reportRuntimeContext.BuildEnvelope()')
text = text.replace('reportInputs["__current_user_id"] = runtimeContext.CurrentUserId;', 'reportInputs["__current_user_id"] = reportRuntimeContext.CurrentUserId;')
text = text.replace('JarvisRuntimeContext exportRuntime = JarvisRuntimeContext.Capture(xSupport);', 'JarvisRuntimeContext exportRuntime = runtimeContext;')
text = replace_once(text,
'''        private static void ResolveDeterministicRuntimeContext(JarvisShadowOrchestrationResult planning, XSupport xSupport)\n        {\n            if (planning == null || planning.Graph == null || xSupport == null || xSupport.ConnectionInfo == null) return;\n            int currentUserId = xSupport.ConnectionInfo.UserId;''',
'''        private static void ResolveDeterministicRuntimeContext(JarvisShadowOrchestrationResult planning, JarvisRuntimeContext runtimeContext)\n        {\n            if (planning == null || planning.Graph == null || runtimeContext == null) return;\n            int currentUserId = runtimeContext.CurrentUserId;''',
"runtime resolver signature")
text = text.replace('JarvisResultLinkPolicy.BuildMarkdownLinks', 'JarvisResultLinkMaterializer.BuildMarkdownLinks')
write(path, text)


# 7) Echo validates CRM against the same session identity and provides actor evidence for self-assignment.
path = "Core/JarvisControlledEchoTaskExecutor.cs"
text = read(path)
text = replace_once(text,
'''                string runtimeNow = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");\n                int currentUserId = xSupport != null && xSupport.ConnectionInfo != null\n                    ? xSupport.ConnectionInfo.UserId\n                    : 0;''',
'''                JarvisRuntimeContext sessionContext = JarvisRuntimeContext.Capture(xSupport);\n                string runtimeNow = sessionContext.LocalNow.ToString("yyyy-MM-ddTHH:mm:ss");\n                int currentUserId = sessionContext.CurrentUserId;''',
"echo session runtime")
text = replace_once(text,
'''                JObject resolutionContext = BuildResolutionContext(dispatchInputs);\n                JarvisToolContractValidator.ApplyResolutionEvidence("create_crm_task", input, resolutionContext);''',
'''                JObject resolutionContext = BuildResolutionContext(dispatchInputs);\n                EnsureCurrentOperatorActorEvidence(resolutionContext, input, sessionContext);\n                JarvisToolContractValidator.ApplyResolutionEvidence("create_crm_task", input, resolutionContext);''',
"crm actor evidence hook")
insert_anchor = '''        private static bool HasValue(JToken value)\n        {'''
insert_method = '''        private static void EnsureCurrentOperatorActorEvidence(JObject resolutionContext, JObject input, JarvisRuntimeContext sessionContext)\n        {\n            if (resolutionContext == null || sessionContext == null || sessionContext.CurrentUserId <= 0) return;\n            if (HasValue(resolutionContext["actorUserId"]) || HasValue(resolutionContext["actorUserIds"])) return;\n\n            JToken assignee = resolutionContext["assignee"];\n            bool defaultSelf = !HasValue(assignee) || string.Equals(assignee.ToString(), "__CURRENT_OPERATOR__", StringComparison.Ordinal);\n            int numericAssignee;\n            if (!defaultSelf && int.TryParse(assignee.ToString(), out numericAssignee))\n                defaultSelf = numericAssignee == sessionContext.CurrentUserId;\n            if (!defaultSelf) return;\n\n            resolutionContext["actorUserId"] = sessionContext.CurrentUserId;\n            if (input != null) input["actorUserId"] = sessionContext.CurrentUserId;\n        }\n\n'''
text = replace_once(text, insert_anchor, insert_method + insert_anchor, "actor evidence method")
write(path, text)


# 8) Explicit semantic document category survives decomposition/composition as a binding constraint.
path = "Core/JarvisDocumentScopeValidator.cs"
text = read(path)
insert_anchor = '''        internal static string[] Validate(string documentScope, string datasetJson)\n        {'''
insert_method = '''        internal static string InferExplicitScope(string text)\n        {\n            string v = NormalizeText(text);\n            if (string.IsNullOrWhiteSpace(v)) return string.Empty;\n            var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);\n            if (v.Contains("τιμολογ") || v.Contains("invoice")) categories.Add("invoice");\n            if (v.Contains("παραγγελ") || v.Contains(" purchase order") || v.Contains(" sales order")) categories.Add("order");\n            if (v.Contains("προσφορ") || v.Contains("quotation") || v.Contains("quote")) categories.Add("quotation");\n            if (v.Contains("πιστω") || v.Contains("credit note") || v.Contains("credit memo")) categories.Add("credit");\n            if (v.Contains("δελτιο αποστο") || v.Contains("delivery note")) categories.Add("delivery");\n            return categories.Count == 1 ? categories.First() : string.Empty;\n        }\n\n'''
text = replace_once(text, insert_anchor, insert_method + insert_anchor, "document scope inference")
write(path, text)

path = "Core/JarvisIntentOrchestration.cs"
text = read(path)
text = replace_once(text,
'''            if (objectSet.Objects.Count == 0) errors.Add("Intent decomposer produced no usable intent objects.");\n            issues = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();''',
'''            if (objectSet.Objects.Count == 0) errors.Add("Intent decomposer produced no usable intent objects.");\n            ApplyBindingSemanticConstraints(objectSet);\n            issues = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();''',
"apply semantic constraints")
insert_anchor = '''        internal static void ApplyDynamicPass(JarvisIntentObject intentObject, IEnumerable<JarvisRoutingKnowledgeRecord> knowledge, int currentCompany)\n        {'''
insert_method = '''        private static void ApplyBindingSemanticConstraints(JarvisIntentObjectSet objectSet)\n        {\n            if (objectSet == null) return;\n            string originalScope = JarvisDocumentScopeValidator.InferExplicitScope(objectSet.OriginalPrompt);\n            foreach (JarvisIntentObject item in objectSet.Objects.Where(x => x != null))\n            {\n                bool isReportOrExport = item.CandidateScores.Any(x => x != null &&\n                    (string.Equals(x.TaskType, "ReportData", StringComparison.OrdinalIgnoreCase) ||\n                     string.Equals(x.TaskType, "ExportData", StringComparison.OrdinalIgnoreCase)));\n                if (!isReportOrExport || item.InputHints.ContainsKey("document_scope")) continue;\n\n                string scope = JarvisDocumentScopeValidator.InferExplicitScope(item.IntentFragment);\n                if (string.IsNullOrWhiteSpace(scope)) scope = originalScope;\n                if (!string.IsNullOrWhiteSpace(scope))\n                    item.InputHints["document_scope"] = new JValue(scope);\n            }\n        }\n\n'''
text = replace_once(text, insert_anchor, insert_method + insert_anchor, "binding semantic method")
write(path, text)


# Self-cleaning one-shot migration artifacts.
for rel in ["scripts/final_session_identity_and_scope_fix.py", ".github/workflows/final-session-identity-and-scope-once.yml"]:
    p = ROOT / rel
    if p.exists():
        p.unlink()

print("Final session identity and document-scope architecture fix applied.")
