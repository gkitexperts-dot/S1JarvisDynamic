from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8")
    print("updated", path)


def replace_method(text, signature, replacement):
    start = text.find(signature)
    if start < 0:
        raise RuntimeError("method signature not found: " + signature)
    brace = text.find("{", start)
    if brace < 0:
        raise RuntimeError("opening brace not found: " + signature)
    depth = 0
    in_string = False
    verbatim = False
    escape = False
    i = brace
    while i < len(text):
        ch = text[i]
        if in_string:
            if verbatim:
                if ch == '"':
                    if i + 1 < len(text) and text[i + 1] == '"':
                        i += 2
                        continue
                    in_string = False
                    verbatim = False
            else:
                if escape:
                    escape = False
                elif ch == '\\':
                    escape = True
                elif ch == '"':
                    in_string = False
        else:
            if ch == '"':
                in_string = True
                verbatim = i > 0 and text[i - 1] == '@'
            elif ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    return text[:start] + replacement.rstrip() + text[i + 1:]
        i += 1
    raise RuntimeError("closing brace not found: " + signature)


def simple_prompt(signature, agent, mode, extra=""):
    return f'''        {signature}
        {{
            StringBuilder sb = PromptBase("{agent}", "{mode}", contextLine, durableContext);
{extra}            return sb.ToString().Trim();
        }}'''


def update_optimizer():
    path = "Access/Verilic/VerilicProviderRequestOptimizer.cs"
    text = read(path)

    text = replace_method(text,
        "        private static string BuildDurableContext(List<string> paths, List<string> successfulTools)",
        '''        private static string BuildDurableContext(List<string> paths, List<string> successfulTools)
        {
            bool hasPaths = paths != null && paths.Count > 0;
            bool hasSuccessfulTools = successfulTools != null && successfulTools.Count > 0;
            if (!hasPaths && !hasSuccessfulTools) return string.Empty;

            return "[VERIFIED_DURABLE_CONTEXT] " + new JObject
            {
                ["successfulTools"] = new JArray((successfulTools ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase)),
                ["filePaths"] = new JArray((paths ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            }.ToString(Formatting.None);
        }''')

    text = replace_method(text,
        "        private static void HardenDirectExportTools(JArray tools)",
        '''        private static void HardenDirectExportTools(JArray tools)
        {
            if (tools == null) return;
            foreach (JObject tool in tools.OfType<JObject>())
            {
                string name = (string)tool["name"];
                if (string.Equals(name, "query_data", StringComparison.OrdinalIgnoreCase))
                    tool["description"] = "Read-only SQL SELECT for a narrow lookup, count or schema check needed by the direct-export protocol.";
                else if (string.Equals(name, "export_query_to_file", StringComparison.OrdinalIgnoreCase))
                    tool["description"] = "Execute the final SELECT directly to an export file and return path, rowsWritten and totalFound.";
                else if (string.Equals(name, "export_shown_table", StringComparison.OrdinalIgnoreCase))
                    tool["description"] = "Export the already-visible table through the registered visible-table artifact flow.";
            }
        }''')

    text = replace_method(text,
        "        private static StringBuilder PromptBase(string agent, string role, string contextLine, string durableContext)",
        '''        private static StringBuilder PromptBase(string agent, string role, string contextLine, string durableContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[JARVIS_OPTIMIZER_PROTOCOL] logicalAgent=" + (agent ?? string.Empty) +
                "; mode=" + (role ?? string.Empty) +
                "; localDate=" + DateTime.Now.ToString("yyyy-MM-dd") + ".");
            sb.AppendLine("Behavioral rules are supplied exclusively by JARVIS_POLICY_CONTEXT; business/schema facts by JARVIS_KNOWLEDGE_CONTEXT.");
            if (!string.IsNullOrWhiteSpace(contextLine)) sb.AppendLine(contextLine);
            if (!string.IsNullOrWhiteSpace(durableContext)) sb.AppendLine(durableContext);
            return sb;
        }''')

    text = replace_method(text,
        "        private static string BuildConversationalPrompt(string agentName, string contextLine, string durableContext)",
        '''        private static string BuildConversationalPrompt(string agentName, string contextLine, string durableContext)
        {
            string name = string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim();
            return PromptBase(name, "conversation_no_tools", contextLine, durableContext).ToString().Trim();
        }''')

    text = replace_method(text,
        "        private static string BuildQueryProvenancePrompt(string agentName, string contextLine, string sql)",
        '''        private static string BuildQueryProvenancePrompt(string agentName, string contextLine, string sql)
        {
            string name = string.IsNullOrWhiteSpace(agentName) ? "Jarvis" : agentName.Trim();
            StringBuilder sb = PromptBase(name, "query_provenance", contextLine, string.Empty);
            sb.AppendLine("actualPreviousQuery=" + (sql ?? string.Empty));
            return sb.ToString().Trim();
        }''')

    text = replace_method(text,
        "        private static string BuildLatestDocumentPrompt(string agent, string contextLine, string durableContext)",
        '''        private static string BuildLatestDocumentPrompt(string agent, string contextLine, string durableContext)
        {
            return PromptBase(agent, "latest_document_by_current_operator", contextLine, durableContext).ToString().Trim();
        }''')

    text = replace_method(text,
        "        private static string BuildDirectExportPrompt(\n            string agent, string contextLine, string durableContext, string inheritedRequest)",
        '''        private static string BuildDirectExportPrompt(
            string agent, string contextLine, string durableContext, string inheritedRequest)
        {
            StringBuilder sb = PromptBase(agent, "direct_export", contextLine, durableContext);
            if (!string.IsNullOrWhiteSpace(inheritedRequest))
                sb.AppendLine("inheritedExportRequest=" + inheritedRequest.Trim());
            return sb.ToString().Trim();
        }''')

    text = replace_method(text,
        "        private static string BuildReadPrompt(string agent, string contextLine, string durableContext)",
        '''        private static string BuildReadPrompt(string agent, string contextLine, string durableContext)
        {
            return PromptBase(agent, "read_reporting", contextLine, durableContext).ToString().Trim();
        }''')

    replacements = [
        ("BuildEchoContactPrompt", "Echo", "contact_lookup"),
        ("BuildEchoInboxPrompt", "Echo", "inbox_read"),
        ("BuildEchoCalendarPrompt", "Echo", "calendar"),
        ("BuildEchoDraftPrompt", "Echo", "email_draft"),
        ("BuildEchoSendPrompt", "Echo", "email_send"),
        ("BuildEchoGeneralPrompt", "Echo", "email_calendar_contacts"),
        ("BuildForgePrompt", "Forge", "items"),
        ("BuildCompassPrompt", "Compass", "traders"),
        ("BuildSprintPrompt", "Sprint", "courier"),
        ("BuildScoutPrompt", "Scout", "browser_research"),
        ("BuildSagePrompt", "Sage", "help_support"),
    ]
    for method, agent, mode in replacements:
        sig = f"        private static string {method}(string contextLine, string durableContext)"
        repl = f'''        private static string {method}(string contextLine, string durableContext)
        {{
            return PromptBase("{agent}", "{mode}", contextLine, durableContext).ToString().Trim();
        }}'''
        text = replace_method(text, sig, repl)

    text = replace_method(text,
        "        private static string BuildEchoExportPrompt(string contextLine, string durableContext, bool emailMentioned)",
        '''        private static string BuildEchoExportPrompt(string contextLine, string durableContext, bool emailMentioned)
        {
            StringBuilder sb = PromptBase("Echo", "report_export", contextLine, durableContext);
            sb.AppendLine("emailMentioned=" + emailMentioned.ToString().ToLowerInvariant());
            return sb.ToString().Trim();
        }''')

    write(path, text)


def update_registry():
    path = "Core/JarvisPolicyRegistry.cs"
    text = read(path)
    if 'GLOBAL.DURABLE_RESULTS_ARE_FACTS' in text:
        write(path, text)
        return

    anchor = '''            P("ORCHESTRATION.DATASET_REFINEMENT_EXISTING_FACTS_ONLY", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Local dataset refinement επιτρέπεται μόνο όταν το follow-up απαντιέται αποκλειστικά από τις υπάρχουσες validated στήλες/τιμές. Αν απαιτείται νέα πληροφορία ή νέα στήλη, canRefine=false και το request επιστρέφει στο κανονικό orchestration.", agents: A("Jarvis"), tasks: A("__dataset_refinement"), domains: A("Reporting"), priority: 933),
'''
    if anchor not in text:
        raise RuntimeError("policy insertion anchor not found")
    additions = anchor + '''
            P("GLOBAL.DURABLE_RESULTS_ARE_FACTS", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Verified successful tool results παραμένουν durable facts όταν συμπτύσσεται το raw history. Μην ισχυρίζεσαι νέα εκτέλεση χωρίς current tool_result και μην απορρίπτεις προηγούμενο verified success μόνο επειδή αφαιρέθηκε το raw trace.", priority: 932),

            P("FILE.REUSE_VERIFIED_ARTIFACT", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "Όταν υπάρχει verified durable file path για το ζητούμενο artifact, επαναχρησιμοποίησε ακριβώς αυτό το artifact/path αντί να ξανακάνεις export χωρίς invalidation ή νέο αίτημα που απαιτεί διαφορετικό περιεχόμενο.", agents: A("Jarvis", "Atlas", "Echo"), priority: 931),

            P("REPORT.QUERY_PROVENANCE_ACTUAL_TRACE", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Both,
                "Όταν ο χειριστής ζητά ποιο SQL/query χρησιμοποιήθηκε, παρουσίασε μόνο το πραγματικό query από το verified tool trace. Μην το ανακατασκευάζεις, διορθώνεις ή αντικαθιστάς εκ των υστέρων.", agents: A("Jarvis", "Atlas"), domains: A("Reporting"), priority: 930),

            P("REPORT.LATEST_CURRENT_OPERATOR_DOCUMENT", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Αίτημα για το τελευταίο παραστατικό που καταχώρησε ο τρέχων χειριστής σημαίνει FINDOC.COMPANY=currentCompany και FINDOC.INSUSER=currentUserId, με deterministic ORDER BY FINDOC.INSDATE DESC, FINDOC.FINDOC DESC. TRNDATE δεν είναι χρόνος καταχώρησης.", agents: A("Jarvis", "Atlas"), tasks: A("ReportData"), domains: A("Reporting"), priority: 929),

            P("DOCUMENT.SEMANTIC_CATEGORY_FROM_METADATA", JarvisPolicyScope.Domain, JarvisPolicyEnforcement.Both,
                "Κατηγορίες όπως τιμολόγια, παραγγελίες, προσφορές, συμψηφισμοί και πιστωτικά ερμηνεύονται από authoritative document type metadata/description και όχι από hardcoded SERIES ids που διαφέρουν ανά εταιρία. Αν η κατηγορία παραμένει αμφίσημη και αλλάζει το αποτέλεσμα, ζήτησε clarification.", agents: A("Jarvis", "Atlas", "Echo"), domains: A("Reporting", "Soft1Documents"), priority: 928),

            P("EXPORT.DIRECT_ROWS_BYPASS_LLM", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "Σε explicit direct export, το τελικό dataset ταξιδεύει από το validated SELECT απευθείας στο registered export tool/file και όχι ως μεγάλο preview μέσω LLM context. Narrow identity/schema/count lookups επιτρέπονται μόνο ως prerequisites.", agents: A("Jarvis", "Atlas", "Echo"), tasks: A("ExportData"), domains: A("Reporting"), priority: 927),

            P("EXPORT.RESOLVE_IDENTITY_BEFORE_EXPORT", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Πριν από direct export, κάθε business entity/filter που αλλάζει το dataset πρέπει να είναι μονοσήμαντα resolved. Πολλαπλά λογικά matches απαιτούν clarification πριν από το export· δεν συγχωνεύονται σιωπηρά.", agents: A("Jarvis", "Atlas", "Echo"), tasks: A("ExportData"), domains: A("Reporting"), priority: 926),

            P("CONTACT.SEARCH_CRITERION_FIDELITY", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Σε contact/email lookup διατήρησε το explicit email ή πλήρες name criterion του χειριστή. Μην μετατρέπεις exact email σε inferred name και μην χαλαρώνεις αυθαίρετα επώνυμο σε μικρά substrings που μπορούν να φέρουν άσχετα matches.", agents: A("Jarvis", "Echo"), domains: A("Email"), priority: 925),

            P("BROWSER.READ_BEFORE_CLAIM", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Μην ισχυρίζεσαι ότι διάβασες web page ή extracted table χωρίς successful registered read_page_content/extract_page_tables result για το σχετικό content.", agents: A("Scout", "Jarvis"), domains: A("Browser"), priority: 924),
'''
    text = text.replace(anchor, additions, 1)
    write(path, text)


def main():
    update_optimizer()
    update_registry()


if __name__ == "__main__":
    main()
