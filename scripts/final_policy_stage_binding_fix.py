from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(p): return (ROOT/p).read_text(encoding='utf-8-sig')
def write(p,s): (ROOT/p).write_text(s,encoding='utf-8')
def once(s,a,b,label):
    n=s.count(a)
    if n!=1: raise RuntimeError(f'{label}: expected 1 match, found {n}')
    return s.replace(a,b,1)

# These semantic policies must be visible to the Jarvis decomposition stage.
p='Core/JarvisPolicyRegistry.cs'
s=read(p)
s=once(s,
'''            P("CRM.DEFAULT_ASSIGNEE_CURRENT_OPERATOR", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "CreateCrmTask χωρίς ρητά διαφορετικό assignee ανατίθεται στον authenticated current operator της session. Το orchestration materializes __CURRENT_OPERATOR__ και deterministic actorUserId evidence από τη session identity πριν από tool validation. Αν ο χρήστης ορίσει ρητά άλλον assignee, απαιτείται κανονικό identity resolution και δεν γίνεται silent fallback στον current operator.", agents: A("Jarvis", "Echo"), tasks: A("CreateCrmTask"), domains: A("CRM"), tools: A("create_crm_task"), priority: 981),''',
'''            P("CRM.DEFAULT_ASSIGNEE_CURRENT_OPERATOR", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "CreateCrmTask χωρίς ρητά διαφορετικό assignee ανατίθεται στον authenticated current operator της session. Το semantic decomposition δεν επιτρέπεται να μαντέψει ή να ζητήσει ξανά τον ίδιο operator: materializes __CURRENT_OPERATOR__, και το deterministic control plane το μετατρέπει σε actorUserId evidence από τη session identity. Αν ο χρήστης ορίσει ρητά άλλον assignee, απαιτείται κανονικό identity resolution και δεν γίνεται silent fallback στον current operator.", agents: A("Jarvis"), priority: 981),''',
'crm decomposition policy')
s=once(s,
'''            P("DOCUMENT.EXPLICIT_SCOPE_IS_BINDING", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Όταν το user request δηλώνει ρητά μία semantic document category (π.χ. invoice/order/quotation/credit/delivery), το canonical document_scope είναι binding constraint για κάθε σχετικό ReportData/ExportData node. Η κατηγορία δεν επιτρέπεται να χαθεί κατά decomposition/composition και το returned dataset πρέπει να απορρίπτεται fail-closed αν περιέχει άλλη document category.", agents: A("Jarvis", "Atlas"), tasks: A("ReportData", "ExportData"), domains: A("Reporting", "Soft1Documents"), priority: 980),''',
'''            P("DOCUMENT.EXPLICIT_SCOPE_IS_BINDING", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Όταν το user request δηλώνει ρητά μία semantic document category (π.χ. invoice/order/quotation/credit/delivery), το semantic decomposition πρέπει να διατηρεί το canonical document_scope ως binding constraint σε κάθε σχετικό ReportData/ExportData node. Η κατηγορία δεν επιτρέπεται να χαθεί κατά decomposition/composition· downstream validation παραμένει fail-closed αν το dataset περιέχει άλλη document category.", agents: A("Jarvis"), priority: 980),''',
'document decomposition policy')
write(p,s)

# Recognize the startup-recorded operator even if the semantic layer emits its
# authenticated display name/id instead of the canonical marker.
p='Core/JarvisExecutionShadowHarness.cs'
s=read(p)
old='''                JarvisPrerequisiteResolutionItem assignee = node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, "assignee", StringComparison.OrdinalIgnoreCase));
                if (assignee == null || assignee.Value == null ||
                    !string.Equals(assignee.Value.ToString(), "__CURRENT_OPERATOR__", StringComparison.Ordinal))
                    continue;
'''
new='''                JarvisPrerequisiteResolutionItem assignee = node.Prerequisites.FirstOrDefault(x => x != null && string.Equals(x.InputName, "assignee", StringComparison.OrdinalIgnoreCase));
                if (assignee == null || assignee.Value == null) continue;
                string assigneeText = assignee.Value.ToString().Trim();
                int assigneeUserId;
                bool isCurrentOperator = string.Equals(assigneeText, "__CURRENT_OPERATOR__", StringComparison.Ordinal) ||
                    (int.TryParse(assigneeText, out assigneeUserId) && assigneeUserId == currentUserId) ||
                    (!string.IsNullOrWhiteSpace(runtimeContext.CurrentUserDisplayName) &&
                     string.Equals(assigneeText, runtimeContext.CurrentUserDisplayName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (!isCurrentOperator) continue;
'''
s=once(s,old,new,'runtime operator normalization')
write(p,s)

p='Core/JarvisControlledEchoTaskExecutor.cs'
s=read(p)
old='''            bool defaultSelf = !HasValue(assignee) || string.Equals(assignee.ToString(), "__CURRENT_OPERATOR__", StringComparison.Ordinal);
            int numericAssignee;
            if (!defaultSelf && int.TryParse(assignee.ToString(), out numericAssignee))
                defaultSelf = numericAssignee == sessionContext.CurrentUserId;
            if (!defaultSelf) return;
'''
new='''            string assigneeText = HasValue(assignee) ? assignee.ToString().Trim() : string.Empty;
            bool defaultSelf = string.IsNullOrWhiteSpace(assigneeText) ||
                string.Equals(assigneeText, "__CURRENT_OPERATOR__", StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(sessionContext.CurrentUserDisplayName) &&
                 string.Equals(assigneeText, sessionContext.CurrentUserDisplayName.Trim(), StringComparison.OrdinalIgnoreCase));
            int numericAssignee;
            if (!defaultSelf && int.TryParse(assigneeText, out numericAssignee))
                defaultSelf = numericAssignee == sessionContext.CurrentUserId;
            if (!defaultSelf) return;
'''
s=once(s,old,new,'echo operator normalization')
write(p,s)

for rel in ['scripts/final_policy_stage_binding_fix.py','.github/workflows/final-policy-stage-binding-once.yml']:
    f=ROOT/rel
    if f.exists(): f.unlink()
print('Policy stage binding fix applied.')
