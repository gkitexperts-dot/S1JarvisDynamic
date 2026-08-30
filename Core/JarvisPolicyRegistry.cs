using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace S1Jarvis.Core
{
    internal enum JarvisPolicyScope
    {
        Global,
        Agent,
        Task,
        Domain,
        Tool,
        Presentation,
        Routing,
        Orchestration,
        Execution,
        Validation
    }

    internal enum JarvisPolicyEnforcement
    {
        Training,
        Deterministic,
        Both
    }

    internal sealed class JarvisPolicyDescriptor
    {
        internal JarvisPolicyDescriptor(
            string policyId,
            JarvisPolicyScope scope,
            JarvisPolicyEnforcement enforcement,
            string rule,
            string[] agents = null,
            string[] tasks = null,
            string[] domains = null,
            string[] tools = null,
            int priority = 100)
        {
            PolicyId = policyId ?? string.Empty;
            Scope = scope;
            Enforcement = enforcement;
            Rule = rule ?? string.Empty;
            Agents = agents ?? new string[0];
            Tasks = tasks ?? new string[0];
            Domains = domains ?? new string[0];
            Tools = tools ?? new string[0];
            Priority = priority;
        }

        internal string PolicyId { get; private set; }
        internal JarvisPolicyScope Scope { get; private set; }
        internal JarvisPolicyEnforcement Enforcement { get; private set; }
        internal string Rule { get; private set; }
        internal string[] Agents { get; private set; }
        internal string[] Tasks { get; private set; }
        internal string[] Domains { get; private set; }
        internal string[] Tools { get; private set; }
        internal int Priority { get; private set; }
    }

    /// <summary>
    /// Central policy inventory for Jarvis and every logical agent.
    ///
    /// Structural contracts remain authoritative in JarvisTaskRegistry and
    /// JarvisToolRegistry. This registry owns behavioral policy: how an agent
    /// must reason/act/present, and which deterministic invariants must be
    /// enforced by the control plane. No execution path should carry its own
    /// copy of policy prose; it resolves the applicable policy set here.
    /// </summary>
    internal static class JarvisPolicyRegistry
    {
        private static readonly JarvisPolicyDescriptor[] Policies =
        {
            P("GLOBAL.JARVIS_OWNS_GRAPH", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Ο Jarvis είναι ο μοναδικός ιδιοκτήτης του execution graph. Agents δεν καλούν άλλους agents, δεν προωθούν αποτελέσματα μεταξύ τους και δεν αποφασίζουν ποιο task εκτελείται μετά.", priority: 1000),

            P("GLOBAL.REGISTRY_IS_AUTHORITY", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Both,
                "Task/tool registries και deterministic runtime contracts είναι authoritative για capabilities, prerequisites και outputs. Prose από model δεν μπορεί να αναιρέσει capability που έχει πράγματι δοθεί στο request.", priority: 990),

            P("GLOBAL.PROVIDER_NEUTRAL", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Deterministic,
                "Κανένα business/orchestration behavior δεν εξαρτάται από συγκεκριμένο AI provider ή model. Provider/model/credential προέρχονται μόνο από το session runtime registry.", priority: 980),

            P("GLOBAL.NO_INVENTED_FACTS", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Both,
                "Μην επινοείς ids, emails, dates, schema fields, entity identities, recipients, document state ή business facts. Αν η πληροφορία δεν είναι resolved από input, knowledge ή tool evidence, ζήτησε clarification ή επέστρεψε controlled failure.", priority: 970),

            P("GLOBAL.RESULTS_RETURN_TO_JARVIS", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Κάθε agent επιστρέφει αποτέλεσμα μόνο στον Jarvis. Ο Jarvis το επικυρώνει, το αποθηκεύει και υλοποιεί deterministic dependency bindings πριν επιτρέψει downstream task.", priority: 960),

            P("GLOBAL.INDEPENDENT_TASK_CONTINUATION", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Deterministic,
                "Failure ή ανάγκη clarification ενός node δεν σταματά ανεξάρτητα nodes που είναι ήδη dispatchable. Μπλοκάρονται μόνο τα πραγματικά downstream dependencies του προβληματικού node.", priority: 950),

            P("GLOBAL.NO_DUPLICATE_COMPLETED_TASK", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Deterministic,
                "Ένα task που έχει verified terminal success δεν επανεκτελείται στο ίδιο orchestration run εκτός αν το upstream state που το τροφοδότησε έχει invalidated και το contract επιτρέπει νέο execution.", priority: 940),

            P("GLOBAL.CLARIFY_TO_COMPLETENESS", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Όταν λείπουν πραγματικά απαραίτητα facts, ο Jarvis ζητά clarification στο αναγκαίο βάθος μέχρι να μην υπάρχουν unresolved required inputs. Δεν χρησιμοποιεί blind retry αντί για clarification.", priority: 930),

            P("GLOBAL.AUTHORITATIVE_ENTITY_KNOWLEDGE", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Business entity roles, discriminators, object mappings και identity fields προέρχονται μόνο από authoritative knowledge/schema companions. Agent δεν μαντεύει SODTYPE, object name ή άλλο discriminator.", priority: 920),

            P("GLOBAL.ADDRESSABLE_RESULT_LINK", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Both,
                "Όταν verified task παράγει addressable Soft1 object, document, file ή external object και υπάρχει authoritative reference/link, το user-visible response πρέπει να εμφανίζει clickable reference. Δεν κατασκευάζεται link από μη εγκεκριμένο mapping.", priority: 910),

            P("DECOMPOSER.ATOMIC_OUTCOME", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Σπάσε το request σε ανεξάρτητα atomic business outcomes, ένα outcome ανά intent object. Μην εκτελείς tools και μην δημιουργείς dependencies κατά το decomposition.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 900),

            P("DECOMPOSER.SELF_CONTAINED_OBJECT", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Κάθε atomic object είναι self-contained: κληρονομεί από το ίδιο user prompt μόνο τα shared facts που απαιτούνται για αυτόνομη εκτέλεση. Μην αφήνεις fragment τύπου 'και στο calendar' που χάνει πρόσωπο, ημερομηνία, ώρα, entity ή recipient.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 890),

            P("DECOMPOSER.REGISTERED_TASKS_ONLY", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Task candidates προέρχονται αποκλειστικά από το Task Registry. Μην επινοείς agents, tools, capabilities ή task types και μην μαντεύεις resolved ids/series/emails.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 880),

            P("ATLAS.REPORT_SINGLE_TARGET_QUERY", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Για ReportData πρότεινε ένα στοχευμένο read-only query που απαντά το business question. Lookup/master rows είναι prerequisite evidence και όχι τελικό business result όταν ζητούνται transactions/documents.", agents: A("Atlas"), tasks: A("ReportData"), domains: A("Reporting"), tools: A("query_data"), priority: 870),

            P("ATLAS.SELECT_ONLY", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Το query_data εκτελεί μόνο SELECT. Μη χρησιμοποιείς write/DDL/EXEC operations. Ο deterministic SELECT-only validator παραμένει authoritative.", agents: A("Atlas"), tools: A("query_data"), priority: 860),

            P("ATLAS.ENTITY_FIDELITY", JarvisPolicyScope.Domain, JarvisPolicyEnforcement.Both,
                "Όταν το request αναφέρει συγκεκριμένη business entity και role, διατήρησε την ίδια identity/role. Μην διευρύνεις αυθαίρετα με συνώνυμα, μεταφράσεις, φωνητικά ή παρόμοιες επωνυμίες. Σε ambiguity απαιτείται resolution/clarification.", agents: A("Atlas", "Compass", "Jarvis"), domains: A("Reporting", "Traders"), priority: 850),

            P("ATLAS.DETERMINISTIC_LATEST", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Singular latest/most-recent requests χρησιμοποιούν deterministic ordering και single-row result, με stable tie-breaker όταν απαιτείται από το domain.", agents: A("Atlas"), tasks: A("ReportData"), priority: 840),

            P("ECHO.ATOMIC_NATIVE_CALL", JarvisPolicyScope.Agent, JarvisPolicyEnforcement.Training,
                "Σε controlled write task ο Echo materializes ακριβώς μία native terminal tool call για το ήδη ανατεθειμένο atomic task. Δεν αποφασίζει capabilities, άλλα tasks ή agent handoffs και δεν κάνει uncontrolled retry loop.", agents: A("Echo"), priority: 830),

            P("ECHO.DATE_TIME_NORMALIZATION", JarvisPolicyScope.Domain, JarvisPolicyEnforcement.Training,
                "Μετέτρεψε ρητές φυσικές ημερομηνίες/ώρες σε ISO χρησιμοποιώντας την τρέχουσα τοπική ημερομηνία/ώρα που παρέχει ο Jarvis runtime context. Μην αλλάζεις ημερομηνία/ώρα που έδωσε ρητά ο χρήστης.", agents: A("Echo"), domains: A("CRM", "Calendar"), priority: 820),

            P("CALENDAR.DEFAULT_DURATION", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Για CreateCalendarEvent, όταν ο χρήστης δεν δίνει διάρκεια, χρησιμοποίησε 30 λεπτά.", agents: A("Echo"), tasks: A("CreateCalendarEvent"), priority: 810),

            P("CALENDAR.NO_IMPLICIT_ATTENDEE", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Αναφορά προσώπου στο subject/body δεν σημαίνει attendee. Attendee δημιουργείται μόνο όταν ο χρήστης ζητά ρητά πρόσκληση/συμμετοχή τρίτου.", agents: A("Echo", "Jarvis"), tasks: A("CreateCalendarEvent"), domains: A("Calendar"), priority: 800),

            P("EMAIL.RESOLVED_RECIPIENT_ONLY", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Send/reply email επιτρέπεται μόνο με resolved recipient/source message και validated content. Αν υπάρχουν πολλαπλές επαφές, παρουσίασε επιλογές αντί να επιλέξεις σιωπηρά.", agents: A("Echo", "Jarvis"), domains: A("Email"), tools: A("send_email", "reply_email", "search_outlook_contacts"), priority: 790),

            P("PRESENTATION.VALIDATED_FACTS_ONLY", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Both,
                "Presentation layer αλλάζει μόνο wording/μορφοποίηση πάνω σε validated context. Δεν κάνει query/action, δεν αλλάζει business facts και δεν συμπληρώνει ελλείποντα στοιχεία.", agents: A("Jarvis"), priority: 780),

            P("PRESENTATION.HUMAN_READABLE", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Training,
                "User-facing report/email πρέπει να είναι σύντομο, φυσικό και επαγγελματικό, όχι raw key=value dump. Σε email το subject είναι σχετικό και το body έτοιμο για πραγματική αποστολή.", agents: A("Jarvis"), priority: 770),

            P("TOOL.query_data.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Σε failure του query_data αποτυγχάνει μόνο το read task, χωρίς data mutation. Ποτέ success claim χωρίς πραγματικό dataset/tool success.", tools: A("query_data")),
            P("TOOL.export_query_to_file.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Σε export failure διατήρησε source query/result context και μην ισχυριστείς ότι δημιουργήθηκε αρχείο.", tools: A("export_query_to_file")),
            P("TOOL.export_shown_table.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Export μόνο της ήδη ορατής table state· ποτέ silent re-query.", tools: A("export_shown_table")),
            P("TOOL.open_document.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Αν document/object δεν resolve/open, ανέφερε failure χωρίς να επινοήσεις document state.", tools: A("open_document")),
            P("TOOL.get_conversion_targets.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Επέστρεφε μόνο πραγματικά conversion targets του active Soft1 context.", tools: A("get_conversion_targets")),
            P("TOOL.get_item_template.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Αν template metadata λείπει, σταμάτα πριν το create_item και επέστρεψε τα missing requirements.", tools: A("get_item_template")),
            P("TOOL.create_item.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Μη δηλώνεις item creation χωρίς successful Soft1 write και verified required inputs/confirmation.", tools: A("create_item")),
            P("TOOL.find_trader_by_afm.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Ambiguous trader matches οδηγούν σε clarification, ποτέ σε guessed entity.", tools: A("find_trader_by_afm")),
            P("TOOL.get_aade_data.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Μην κατασκευάζεις taxpayer/company fields όταν AADE data λείπουν ή είναι incomplete.", tools: A("get_aade_data")),
            P("TOOL.create_trader_from_aade.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Create trader μόνο με resolved target και successful Soft1 write.", tools: A("create_trader_from_aade")),
            P("TOOL.search_outlook_contacts.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Σε multiple contact matches παρουσίασε επιλογές, μη διαλέγεις recipient σιωπηρά.", tools: A("search_outlook_contacts")),
            P("TOOL.show_contact_results.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Πρόβαλε μόνο resolved contact results.", tools: A("show_contact_results")),
            P("TOOL.filter_email_inbox.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Empty inbox match είναι valid empty result· μην επιστρέφεις unrelated messages.", tools: A("filter_email_inbox")),
            P("TOOL.read_email.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Διάβασε μόνο το resolved message και μην επινοείς sender/body/attachments.", tools: A("read_email")),
            P("TOOL.download_email_attachment.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Expose file μόνο μετά από successful attachment download.", tools: A("download_email_attachment")),
            P("TOOL.filter_calendar.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Filter μόνο πραγματικά calendar entries· empty match είναι valid result.", tools: A("filter_calendar")),
            P("TOOL.show_calendar_entries.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Πρόβαλε μόνο returned calendar entries.", tools: A("show_calendar_entries")),
            P("TOOL.read_calendar.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Μην συμπληρώνεις missing meeting details πέρα από το retrieved event.", tools: A("read_calendar")),
            P("TOOL.create_outlook_event.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Calendar creation απαιτεί resolved required date/time/attendee data και successful Outlook result.", tools: A("create_outlook_event")),
            P("TOOL.create_crm_task.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Μη δηλώνεις CRM completion χωρίς successful Soft1 task creation.", tools: A("create_crm_task")),
            P("TOOL.send_email.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Recipient/content πρέπει να είναι resolved και send success να προέρχεται από το tool result.", tools: A("send_email")),
            P("TOOL.reply_email.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Reply μόνο σε resolved source message και μόνο με successful send result.", tools: A("reply_email")),
            P("TOOL.show_courier_documents.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Πρόβαλε μόνο eligible returned courier documents.", tools: A("show_courier_documents")),
            P("TOOL.get_courier_voucher_data.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Επέστρεφε μόνο persisted voucher/provider data για resolved document.", tools: A("get_courier_voucher_data")),
            P("TOOL.create_courier_voucher.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Voucher creation απαιτεί resolved document/provider και successful courier result.", tools: A("create_courier_voucher")),
            P("TOOL.cancel_courier_voucher.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Cancellation απαιτεί existing resolved voucher και successful provider cancellation.", tools: A("cancel_courier_voucher")),
            P("TOOL.open_url.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Navigation success δεν αποτελεί evidence για page facts.", tools: A("open_url")),
            P("TOOL.read_page_content.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Claims βασίζονται μόνο στο content που πράγματι επέστρεψε η loaded page.", tools: A("read_page_content")),
            P("TOOL.extract_page_tables.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Επέστρεφε μόνο tables που πράγματι extracted από την current page.", tools: A("extract_page_tables")),
            P("TOOL.create_order.FALLBACK", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Deterministic,
                "Order creation απαιτεί resolved business keys/lines και successful Soft1 write.", tools: A("create_order"))
        };

        internal static IReadOnlyList<JarvisPolicyDescriptor> AllPolicies
        {
            get { return Policies; }
        }

        internal static IEnumerable<JarvisPolicyDescriptor> Resolve(
            string agent = null,
            string task = null,
            IEnumerable<string> domains = null,
            IEnumerable<string> tools = null,
            JarvisPolicyEnforcement? enforcement = null)
        {
            var domainSet = Set(domains);
            var toolSet = Set(tools);
            string agentValue = (agent ?? string.Empty).Trim();
            string taskValue = (task ?? string.Empty).Trim();

            return Policies
                .Where(p => enforcement == null || p.Enforcement == enforcement.Value || p.Enforcement == JarvisPolicyEnforcement.Both)
                .Where(p => p.Agents.Length == 0 || p.Agents.Any(x => Eq(x, agentValue)))
                .Where(p => p.Tasks.Length == 0 || p.Tasks.Any(x => Eq(x, taskValue)))
                .Where(p => p.Domains.Length == 0 || p.Domains.Any(domainSet.Contains))
                .Where(p => p.Tools.Length == 0 || p.Tools.Any(toolSet.Contains))
                .OrderByDescending(p => p.Priority)
                .ThenBy(p => p.PolicyId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        internal static string BuildTrainingContext(
            string agent,
            string task,
            IEnumerable<string> domains,
            IEnumerable<string> tools)
        {
            JarvisPolicyDescriptor[] applicable = Resolve(agent, task, domains, tools)
                .Where(p => p.Enforcement == JarvisPolicyEnforcement.Training || p.Enforcement == JarvisPolicyEnforcement.Both)
                .ToArray();
            if (applicable.Length == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[JARVIS_POLICY_CONTEXT]");
            foreach (JarvisPolicyDescriptor policy in applicable)
                sb.Append("- ").Append(policy.PolicyId).Append(": ").AppendLine(policy.Rule);
            return sb.ToString().TrimEnd();
        }

        internal static string BuildDeterministicPolicyContext(
            string agent,
            string task,
            IEnumerable<string> domains,
            IEnumerable<string> tools)
        {
            JarvisPolicyDescriptor[] applicable = Resolve(agent, task, domains, tools)
                .Where(p => p.Enforcement == JarvisPolicyEnforcement.Deterministic || p.Enforcement == JarvisPolicyEnforcement.Both)
                .ToArray();
            return string.Join(" | ", applicable.Select(x => x.PolicyId).ToArray());
        }

        internal static string GetToolFallbackPolicy(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return string.Empty;
            JarvisPolicyDescriptor policy = Policies.FirstOrDefault(x =>
                x.PolicyId.Equals("TOOL." + toolName.Trim() + ".FALLBACK", StringComparison.OrdinalIgnoreCase));
            return policy == null ? string.Empty : policy.Rule;
        }

        internal static string[] ValidateInventory()
        {
            var issues = new List<string>();
            foreach (IGrouping<string, JarvisPolicyDescriptor> group in Policies.GroupBy(x => x.PolicyId, StringComparer.OrdinalIgnoreCase))
                if (group.Count() > 1) issues.Add("Duplicate policy registration: " + group.Key);

            foreach (JarvisPolicyDescriptor policy in Policies)
            {
                if (string.IsNullOrWhiteSpace(policy.PolicyId)) issues.Add("Policy without id.");
                if (string.IsNullOrWhiteSpace(policy.Rule)) issues.Add("Policy without rule: " + policy.PolicyId);
                foreach (string tool in policy.Tools)
                    if (JarvisToolRegistry.Find(tool) == null)
                        issues.Add("Policy references unknown tool: " + policy.PolicyId + " -> " + tool);
                foreach (string task in policy.Tasks)
                    if (!task.StartsWith("__", StringComparison.Ordinal) && JarvisTaskRegistry.Find(task) == null)
                        issues.Add("Policy references unknown task: " + policy.PolicyId + " -> " + task);
            }
            return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static JarvisPolicyDescriptor P(
            string id,
            JarvisPolicyScope scope,
            JarvisPolicyEnforcement enforcement,
            string rule,
            string[] agents = null,
            string[] tasks = null,
            string[] domains = null,
            string[] tools = null,
            int priority = 100)
        {
            return new JarvisPolicyDescriptor(id, scope, enforcement, rule, agents, tasks, domains, tools, priority);
        }

        private static string[] A(params string[] values) { return values ?? new string[0]; }

        private static HashSet<string> Set(IEnumerable<string> values)
        {
            return new HashSet<string>((values ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.OrdinalIgnoreCase);
        }

        private static bool Eq(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
