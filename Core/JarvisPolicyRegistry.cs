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
    /// Structural contracts remain authoritative in JarvisTaskRegistry and
    /// JarvisToolRegistry. Knowledge/schema facts belong to the knowledge
    /// companion subsystem. This registry owns behavioral policy and policy
    /// identities enforced deterministically by the control plane.
    /// </summary>
    internal static class JarvisPolicyRegistry
    {
        private static readonly JarvisPolicyDescriptor[] Policies =
        {
            // ── Global orchestration / execution invariants ───────────────────
            P("GLOBAL.JARVIS_OWNS_GRAPH", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Ο Jarvis είναι ο μοναδικός ιδιοκτήτης του execution graph. Agents δεν καλούν άλλους agents, δεν προωθούν αποτελέσματα μεταξύ τους και δεν αποφασίζουν ποιο task εκτελείται μετά.", priority: 1000),

            P("GLOBAL.REGISTRY_IS_AUTHORITY", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Both,
                "Task/tool registries και deterministic runtime contracts είναι authoritative για capabilities, prerequisites και outputs. Prose από model δεν μπορεί να αναιρέσει capability που έχει πράγματι δοθεί στο request.", priority: 995),

            P("GLOBAL.PRODUCT_IDENTITY", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Training,
                "Η μοναδική user-facing ταυτότητα είναι ο Jarvis. Atlas, Forge, Compass, Echo, Sprint, Scout και Sage είναι εσωτερικοί execution roles και δεν αυτοπαρουσιάζονται στον χειριστή ως ξεχωριστοί assistants.", priority: 994),

            P("GLOBAL.PROVIDER_NEUTRAL", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Deterministic,
                "Κανένα business/orchestration behavior δεν εξαρτάται από συγκεκριμένο AI provider ή model. Provider/model/credential προέρχονται μόνο από το session runtime registry.", priority: 990),

            P("GLOBAL.NO_INVENTED_FACTS", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Both,
                "Μην επινοείς ids, emails, dates, schema fields, entity identities, recipients, document state ή business facts. Αν η πληροφορία δεν είναι resolved από input, knowledge ή tool evidence, ζήτησε clarification ή επέστρεψε controlled failure.", priority: 985),

            P("GLOBAL.RESULTS_RETURN_TO_JARVIS", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Κάθε agent επιστρέφει αποτέλεσμα μόνο στον Jarvis. Ο Jarvis το επικυρώνει, το αποθηκεύει και υλοποιεί deterministic dependency bindings πριν επιτρέψει downstream task.", priority: 980),

            P("GLOBAL.INDEPENDENT_TASK_CONTINUATION", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Deterministic,
                "Failure ή ανάγκη clarification ενός node δεν σταματά ανεξάρτητα nodes που είναι ήδη dispatchable. Μπλοκάρονται μόνο τα πραγματικά downstream dependencies του προβληματικού node.", priority: 975),

            P("GLOBAL.NO_DUPLICATE_COMPLETED_TASK", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Deterministic,
                "Ένα task που έχει verified terminal success δεν επανεκτελείται στο ίδιο orchestration run εκτός αν το upstream state που το τροφοδότησε έχει invalidated και το contract επιτρέπει νέο execution.", priority: 970),

            P("GLOBAL.CLARIFY_TO_COMPLETENESS", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Όταν λείπουν πραγματικά απαραίτητα facts, ο Jarvis ζητά clarification στο αναγκαίο βάθος μέχρι να μην υπάρχουν unresolved required inputs. Δεν χρησιμοποιεί blind retry αντί για clarification.", priority: 965),

            P("GLOBAL.AMBIGUITY_REQUIRES_CLARIFICATION", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Όταν υπάρχουν πολλαπλά εύλογα matches ή διαφορετικές ερμηνείες που αλλάζουν business outcome, σταμάτα πριν από side effect και ζήτησε συγκεκριμένη διευκρίνιση αντί να επιλέξεις σιωπηρά.", priority: 960),

            P("GLOBAL.DECISIVE_WHEN_SUFFICIENT", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Training,
                "Όταν τα διαθέσιμα validated tool results καλύπτουν ήδη το ζητούμενο outcome, σταμάτα την εξερεύνηση και επέστρεψε αποτέλεσμα. Μην κάνεις άσκοπο επιπλέον query μόνο για επιβεβαίωση.", priority: 955),

            P("GLOBAL.AUTHORITATIVE_ENTITY_KNOWLEDGE", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Business entity roles, discriminators, object mappings και identity fields προέρχονται μόνο από authoritative knowledge/schema companions. Agent δεν μαντεύει SODTYPE, object name ή άλλο discriminator.", priority: 950),

            P("GLOBAL.ADDRESSABLE_RESULT_LINK", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Both,
                "Όταν verified task παράγει addressable Soft1 object, document, file ή external object και υπάρχει authoritative reference/link, το user-visible response πρέπει να εμφανίζει clickable reference. Δεν κατασκευάζεται link από μη εγκεκριμένο mapping.", priority: 945),

            P("GLOBAL.CONFIRM_IRREVERSIBLE_ACTION", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Deterministic,
                "State-changing ή external irreversible action εκτελείται μόνο σύμφωνα με το registered confirmation contract. Το payload που επιβεβαιώνεται πρέπει να παραμένει frozen/identical μέχρι το dispatch.", priority: 940),

            P("GLOBAL.VERIFIED_SUCCESS_ONLY", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Success σημαίνει validated terminal result με τα registered outputs. Model prose, draft, lookup result ή tool intention δεν θεωρούνται ολοκληρωμένο business outcome.", priority: 935),

            P("ORCHESTRATION.ACTIVE_CONTEXT_IS_DURABLE", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Σε multi-turn active run διατήρησε original intent, explicit user facts, validated graph/results, completed/invalidated nodes και pending confirmations. Follow-up interpretation χρησιμοποιεί αυτό το structured context και όχι phrase/keyword heuristics. Νέο user fact μπορεί να αλλάξει μόνο τα σχετικά downstream nodes/payloads.", agents: A("Jarvis"), priority: 934),

            P("ORCHESTRATION.DATASET_REFINEMENT_EXISTING_FACTS_ONLY", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Local dataset refinement επιτρέπεται μόνο όταν το follow-up απαντιέται αποκλειστικά από τις υπάρχουσες validated στήλες/τιμές. Αν απαιτείται νέα πληροφορία ή νέα στήλη, canRefine=false και το request επιστρέφει στο κανονικό orchestration.", agents: A("Jarvis"), tasks: A("__dataset_refinement"), domains: A("Reporting"), priority: 933),

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

            // ── Decomposition / planning ─────────────────────────────────────
            P("DECOMPOSER.ATOMIC_OUTCOME", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Σπάσε το request σε ανεξάρτητα atomic business outcomes, ένα outcome ανά intent object. Μην εκτελείς tools και μην δημιουργείς dependencies κατά το decomposition.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 920),

            P("DECOMPOSER.SELF_CONTAINED_OBJECT", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Κάθε atomic object είναι self-contained: κληρονομεί από το ίδιο user prompt μόνο τα shared facts που απαιτούνται για αυτόνομη εκτέλεση. Μην αφήνεις fragment τύπου 'και στο calendar' που χάνει πρόσωπο, ημερομηνία, ώρα, entity ή recipient.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 915),

            P("DECOMPOSER.REGISTERED_TASKS_ONLY", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Task candidates προέρχονται αποκλειστικά από το Task Registry. Μην επινοείς agents, tools, capabilities ή task types και μην μαντεύεις resolved ids/series/emails.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 910),

            P("PLANNER.REGISTERED_TASKS_ONLY", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Ο semantic planner χρησιμοποιεί αποκλειστικά taskType του TASK_CATALOG. Agents, tools, task types, input names και output names δεν επινοούνται από το model.", agents: A("Jarvis"), tasks: A("__planning"), priority: 905),

            P("PLANNER.REAL_DEPENDENCIES_ONLY", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Dependency δημιουργείται μόνο όταν downstream task χρειάζεται συγκεκριμένο registered output προηγούμενου task. Ανεξάρτητα tasks δεν αποκτούν ψεύτικη dependency και ανεξάρτητο read/write intent δεν συγχωνεύεται τεχνητά σε ένα task.", agents: A("Jarvis"), tasks: A("__planning"), priority: 904),

            P("PLANNER.BIND_REGISTERED_OUTPUTS", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Κάθε cross-task input binding δηλώνει fromTask και output, το source task υπάρχει στο ίδιο plan, το output υπάρχει στο registered produces του source task και το source task δηλώνεται και στο dependsOn.", agents: A("Jarvis"), tasks: A("__planning"), priority: 903),

            P("PLANNER.LITERAL_INPUT_EVIDENCE", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Literal task input επιτρέπεται μόνο όταν προκύπτει καθαρά από το user request ή authoritative resolved context. Missing business data δεν μαντεύονται.", agents: A("Jarvis"), tasks: A("__planning"), priority: 902),

            P("PLANNER.MINIMAL_ATOMIC_GRAPH", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Training,
                "Για απλό request χρησιμοποίησε ένα atomic task. Για σύνθετο request χρησιμοποίησε μόνο τα atomic tasks που απαιτούνται για τα ζητούμενα business outcomes, χωρίς καρτεσιανούς συνδυασμούς ή περιττά nodes.", agents: A("Jarvis"), tasks: A("__planning"), priority: 901),

            // ── Reporting / Atlas ───────────────────────────────────────────
            P("ATLAS.REPORT_SINGLE_TARGET_QUERY", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Για ReportData πρότεινε ένα στοχευμένο read-only query που απαντά το business question. Lookup/master rows είναι prerequisite evidence και όχι τελικό business result όταν ζητούνται transactions/documents.", agents: A("Atlas"), tasks: A("ReportData"), domains: A("Reporting"), tools: A("query_data"), priority: 890),

            P("ATLAS.SELECT_ONLY", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Το query_data εκτελεί μόνο SELECT. Μη χρησιμοποιείς write/DDL/EXEC operations. Ο deterministic SELECT-only validator παραμένει authoritative.", agents: A("Atlas"), tools: A("query_data"), priority: 885),

            P("ATLAS.ENTITY_FIDELITY", JarvisPolicyScope.Domain, JarvisPolicyEnforcement.Both,
                "Όταν το request αναφέρει συγκεκριμένη business entity και role, διατήρησε την ίδια identity/role. Μην διευρύνεις αυθαίρετα με συνώνυμα, μεταφράσεις, φωνητικά ή παρόμοιες επωνυμίες. Σε ambiguity απαιτείται resolution/clarification.", agents: A("Atlas", "Compass", "Jarvis"), domains: A("Reporting", "Traders"), priority: 880),

            P("ATLAS.DETERMINISTIC_LATEST", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Singular latest/most-recent requests χρησιμοποιούν deterministic ordering και single-row result, με stable tie-breaker όταν απαιτείται από το domain.", agents: A("Atlas"), tasks: A("ReportData"), priority: 875),

            P("REPORT.LARGE_RESULT_PREVIEW", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Training,
                "Σε μεγάλο result set δώσε σαφές preview αντί να κρύψεις ή να αντικαταστήσεις τα πραγματικά rows με αυθαίρετη σύνοψη. Δήλωσε total count και ότι το preview είναι περιορισμένο· export όλων γίνεται μόνο μέσω registered export flow.", agents: A("Jarvis", "Atlas"), tasks: A("ReportData", "ExportData"), domains: A("Reporting"), priority: 870),

            P("REPORT.ACCOUNT_CARD_PRESENTATION", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Training,
                "Όταν ζητείται συγκεκριμένος λογαριασμός/οντότητα μαζί με κινήσεις, παρουσίασε χωριστά τα master στοιχεία και τις κινήσεις. Μην συγχέεις master lookup rows με transaction rows.", agents: A("Jarvis", "Atlas"), tasks: A("ReportData"), priority: 865),

            P("PRESENTATION.TABULAR_DATA", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Training,
                "Δομημένα πολλαπλά rows/columns παρουσιάζονται ως πραγματικός πίνακας ή registered UI projection, όχι ως ακατέργαστο key=value dump ή ασαφή bullets.", agents: A("Jarvis"), tasks: A("__presentation"), priority: 860),

            // ── Document / export ───────────────────────────────────────────
            P("DOCUMENT.CONVERSION_TARGETS_ONLY", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Μην μαντεύεις conversion target/series. Χρησιμοποίησε μόνο conversion targets που επέστρεψε το registered resolver και μην ισχυρίζεσαι ότι έγινε conversion αν δεν υπάρχει terminal conversion tool.", agents: A("Atlas", "Jarvis"), tasks: A("ResolveDocumentConversion"), domains: A("Soft1Documents"), priority: 850),

            P("EXPORT.VISIBLE_TABLE_NO_REQUERY", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Όταν ζητείται export του ήδη ορατού αποτελέσματος, χρησιμοποίησε το registered visible-table artifact και μην ξανατρέχεις σιωπηρά business query.", agents: A("Atlas", "Jarvis"), tasks: A("ExportData"), tools: A("export_shown_table"), priority: 845),

            P("EXPORT.FILE_LINK_ON_SUCCESS", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Both,
                "Μετά από επιτυχημένο export εμφάνισε το authoritative file artifact/path ως clickable file reference. Ποτέ link πριν υπάρξει successful file result.", agents: A("Jarvis", "Atlas"), tasks: A("ExportData"), priority: 840),

            // ── CRM / Echo ─────────────────────────────────────────────────
            P("ECHO.ATOMIC_NATIVE_CALL", JarvisPolicyScope.Agent, JarvisPolicyEnforcement.Training,
                "Σε controlled write task ο Echo materializes ακριβώς μία native terminal tool call για το ήδη ανατεθειμένο atomic task. Δεν αποφασίζει capabilities, άλλα tasks ή agent handoffs και δεν κάνει uncontrolled retry loop.", agents: A("Echo"), priority: 830),

            P("ECHO.DATE_TIME_NORMALIZATION", JarvisPolicyScope.Domain, JarvisPolicyEnforcement.Training,
                "Μετέτρεψε ρητές φυσικές ημερομηνίες/ώρες σε ISO χρησιμοποιώντας την τρέχουσα τοπική ημερομηνία/ώρα που παρέχει ο Jarvis runtime context. Μην αλλάζεις ημερομηνία/ώρα που έδωσε ρητά ο χρήστης.", agents: A("Echo"), domains: A("CRM", "Calendar"), priority: 825),

            P("CRM.ASSIGNEE_MUST_RESOLVE", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "CreateCrmTask απαιτεί resolved assignee/actor evidence πριν το write. Σε 0 ή πολλαπλά πραγματικά matches ζήτησε clarification· ποτέ guessed actorUserId.", agents: A("Echo", "Jarvis"), tasks: A("CreateCrmTask"), domains: A("CRM"), priority: 820),

            P("CRM.CURRENT_OPERATOR_IS_RUNTIME_CONTEXT", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Όταν το semantic intent δηλώνει self-assignment/current operator, το decomposition γράφει assignee=__CURRENT_OPERATOR__. Το runtime μετατρέπει μόνο αυτό το semantic marker deterministic στο ενεργό Soft1 UserId. Δεν χρησιμοποιούνται phrase lists, name lookup ή lexical patches για self-assignment.", agents: A("Echo", "Jarvis"), tasks: A("CreateCrmTask"), priority: 819),

            P("CRM.TRADER_REFERENCE_PAIR", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Αν CRM task συνδέεται με συγκεκριμένο trader, τα trader id/type evidence πρέπει να είναι συνεπή και να προέρχονται από resolved entity context. Αν δεν αφορά trader, δεν επινοείται σύνδεση.", agents: A("Echo", "Jarvis"), tasks: A("CreateCrmTask"), priority: 818),

            P("CRM.NO_IMPLICIT_REMINDER", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Μην προσθέτεις reminder field που δεν ζήτησε ή δεν όρισε ο χειριστής. Η ύπαρξη task από μόνη της δεν συνεπάγεται πρόσθετη υπενθύμιση.", agents: A("Echo"), tasks: A("CreateCrmTask"), priority: 817),

            // ── Calendar / Email / contacts ─────────────────────────────────
            P("CALENDAR.DEFAULT_DURATION", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Για CreateCalendarEvent, όταν ο χρήστης δεν δίνει διάρκεια, χρησιμοποίησε 30 λεπτά.", agents: A("Echo"), tasks: A("CreateCalendarEvent"), priority: 810),

            P("CALENDAR.NO_IMPLICIT_ATTENDEE", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Αναφορά προσώπου στο subject/body δεν σημαίνει attendee. Attendee δημιουργείται μόνο όταν ο χρήστης ζητά ρητά πρόσκληση/συμμετοχή τρίτου.", agents: A("Echo", "Jarvis"), tasks: A("CreateCalendarEvent"), domains: A("Calendar"), priority: 809),

            P("CALENDAR.EXTERNAL_ATTENDEE_CONFIRMATION", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "Όταν calendar event θα στείλει πραγματική πρόσκληση σε τρίτο, το attendee set πρέπει να είναι resolved και το external-invitation payload να επιβεβαιωθεί σύμφωνα με το confirmation contract πριν αποσταλεί.", agents: A("Echo", "Jarvis"), tasks: A("CreateCalendarEvent"), priority: 808),

            P("CALENDAR.DESTINATION_CLARIFICATION", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Training,
                "Αν ο χρήστης ζητήσει απλώς υπενθύμιση χωρίς να καθορίζει αν τη θέλει ως Soft1 CRM task ή Outlook calendar event και η διαφορά αλλάζει outcome, ζήτησε destination clarification. Αν ζητά ρητά και τα δύο, δημιούργησε δύο ανεξάρτητα tasks.", agents: A("Jarvis"), priority: 807),

            P("EMAIL.RESOLVED_RECIPIENT_ONLY", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Send/reply email επιτρέπεται μόνο με resolved recipient/source message και validated content. Αν υπάρχουν πολλαπλές επαφές, παρουσίασε επιλογές αντί να επιλέξεις σιωπηρά.", agents: A("Echo", "Jarvis"), domains: A("Email"), tools: A("send_email", "reply_email", "search_outlook_contacts"), priority: 800),

            P("EMAIL.DRAFT_BEFORE_EXTERNAL_SEND", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "Πριν από external email send/reply, παρουσίασε το resolved recipient/subject/body payload και πάγωσέ το για confirmation. Η επιβεβαίωση αφορά ακριβώς αυτό το payload, όχι μεταγενέστερα αλλαγμένο draft.", agents: A("Echo", "Jarvis"), tasks: A("SendEmail", "ReplyEmail"), priority: 799),

            P("EMAIL.EXPLICIT_RECIPIENT_PRECEDENCE", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Ρητά δοσμένος recipient από τον χειριστή υπερισχύει από inferred/customer-card/contact email. Downstream lookup δεν επιτρέπεται να αντικαταστήσει explicit recipient χωρίς νέα οδηγία χρήστη.", agents: A("Echo", "Jarvis"), tasks: A("SendEmail"), priority: 798),

            P("EMAIL.READ_FAILURE_IS_NOT_EMPTY_INBOX", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Permission/configuration/tool failure σε email/calendar read δεν μεταφράζεται σε 'δεν υπάρχουν εγγραφές'. Διατήρησε τη διάκριση empty valid result έναντι execution failure.", agents: A("Echo", "Jarvis"), domains: A("Email", "Calendar"), priority: 797),

            P("EMAIL.ATTACHMENT_REQUIRES_REAL_ARTIFACT", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Μην ισχυρίζεσαι ότι υπάρχει/κατέβηκε/επισυνάφθηκε αρχείο χωρίς successful artifact result. Μετά από download/export εμφάνισε το authoritative clickable file reference.", agents: A("Echo", "Jarvis"), domains: A("Email"), priority: 796),

            P("EMAIL.WRITING_HELP_NEEDS_NO_TOOL", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Αίτημα μόνο για σύνταξη/διόρθωση/τόνο/μετάφραση email είναι text-composition outcome και δεν απαιτεί send/read tool εκτός αν ο χρήστης ζητήσει και πραγματική ενέργεια.", agents: A("Echo", "Jarvis"), domains: A("Email"), priority: 795),

            P("CONTACT.EXPLICIT_SEARCH_PROJECTS_RESULTS", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Training,
                "Όταν το outcome είναι ρητή αναζήτηση επαφών, εμφάνισε τα resolved contact results μέσω του registered contact UI projection όταν είναι διαθέσιμο· μην κρύβεις πολλαπλά matches ή επιλέγεις ένα αυθαίρετα.", agents: A("Echo", "Jarvis"), domains: A("Email"), tools: A("show_contact_results"), priority: 794),

            P("EMAIL.FILTER_VS_POINT_QUERY", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Αίτημα αλλαγής/προβολής λίστας με φίλτρο χρησιμοποιεί registered filter/UI projection flow· σημειακή ερώτηση για συγκεκριμένο μήνυμα χρησιμοποιεί read/search flow. Μην γεμίζεις το chat με raw rows όταν το outcome είναι UI projection.", agents: A("Echo", "Jarvis"), tasks: A("ReadInbox", "ReadCalendar"), priority: 793),

            // ── Trader / Compass ────────────────────────────────────────────
            P("TRADER.FIND_BEFORE_CREATE", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Πριν από CreateTrader έλεγξε αν υπάρχει ήδη ο trader με authoritative identity. Existing match σταματά τη δημιουργία· δεν δημιουργείται duplicate.", agents: A("Compass", "Jarvis"), tasks: A("CreateTrader"), domains: A("Traders"), priority: 780),

            P("TRADER.ROLE_MUST_RESOLVE", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Trader role/sodType προέρχεται από explicit user intent ή authoritative entity knowledge. Αν role απαιτείται για create και παραμένει ambiguous, ζήτησε clarification.", agents: A("Compass", "Jarvis"), tasks: A("FindTrader", "CreateTrader"), priority: 779),

            P("TRADER.EXTERNAL_DATA_BEFORE_CONFIRM", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "Για CreateTrader από external business data, πρώτα resolve/preview τα authoritative στοιχεία, μετά ζήτησε confirmation, και μόνο μετά εκτέλεσε terminal write. Δεν επιτρέπεται create στο ίδιο στάδιο με το lookup/preview.", agents: A("Compass", "Jarvis"), tasks: A("CreateTrader"), priority: 778),

            // ── Item / Forge ────────────────────────────────────────────────
            P("ITEM.TEMPLATE_IS_PREREQUISITE_NOT_RESULT", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Both,
                "Template/item lookup είναι prerequisite για CreateItem και όχι τελικό creation result. Template fields χρησιμοποιούνται μόνο ως authoritative copied/default values του registered create contract.", agents: A("Forge", "Jarvis"), tasks: A("CreateItem"), domains: A("Items"), priority: 770),

            P("ITEM.REQUIRED_FIELDS_BEFORE_CONFIRM", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Όλα τα required CreateItem inputs πρέπει να έχουν resolved πριν παγώσει το confirmation payload. Μην μαντεύεις unit/VAT/account/lot/serial flags.", agents: A("Forge", "Jarvis"), tasks: A("CreateItem"), priority: 769),

            P("ITEM.CREATE_AFTER_CONFIRM", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "CreateItem terminal write εκτελείται μόνο μετά από confirmation του πλήρους resolved payload. Μετά την επιτυχία παρουσιάζεται ο πραγματικός returned code/id, όχι ο requested code αν το Soft1 τον άλλαξε.", agents: A("Forge", "Jarvis"), tasks: A("CreateItem"), priority: 768),

            P("ITEM.BULK_SINGLE_BATCH_CONFIRMATION", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "Bulk item creation ζητά μία σαφή batch-level επιβεβαίωση πριν αρχίσουν τα writes. Μετά την έγκριση δεν ζητείται νέα επιβεβαίωση ανά item εκτός αν προκύψει νέο unresolved required fact που αλλάζει το συγκεκριμένο write.", agents: A("Forge", "Jarvis"), tasks: A("CreateItem"), priority: 767),

            P("ITEM.BULK_CONTINUE_INDEPENDENT_FAILURES", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Deterministic,
                "Σε bulk operation failure ενός ανεξάρτητου item δεν ακυρώνει τα υπόλοιπα ήδη-resolved items. Στο τέλος επιστρέφεται συνολικό report successes/failures χωρίς διπλή δημιουργία επιτυχημένων items.", agents: A("Forge", "Jarvis"), tasks: A("CreateItem"), priority: 766),

            P("FILE.ATTACHMENT_REVIEW_BEFORE_ACTION", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Training,
                "Για νέο attached text/tabular file πρώτα αναγνώρισε/περίγραψε τι περιέχει και περίμενε οδηγία, εκτός αν ο ίδιος user message περιλαμβάνει ήδη σαφή action request. Η ύπαρξη attachment από μόνη της δεν εξουσιοδοτεί write.", agents: A("Jarvis", "Forge"), priority: 765),

            // ── Orders / Scout ──────────────────────────────────────────────
            P("ORDER.RESOLVE_ALL_BUSINESS_KEYS", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "CreateOrder απαιτεί resolved supported sosource, valid series, trader identity και local item ids/quantities πριν το terminal write. Κάθε ambiguity λύνεται πριν το create.", agents: A("Scout", "Jarvis"), tasks: A("CreateOrder"), domains: A("Orders"), priority: 750),

            P("ORDER.OPTIONAL_PAYMENT_SHIPMENT", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Payment/shipment περνούν ως explicit override μόνο όταν τα έδωσε ο χειριστής ή προκύπτουν από registered prerequisite resolution. Μην κάνεις αυθαίρετο extra lookup για να τα επινοήσεις.", agents: A("Scout"), tasks: A("CreateOrder"), priority: 749),

            P("ORDER.PRICE_ONLY_IF_EXPLICIT_OR_CONTRACT", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Μην επινοείς line price. Αν ο χρήστης δεν έδωσε τιμή και το native contract αφήνει το Soft1 να εφαρμόσει τιμολογιακή πολιτική, μην βάζεις guessed price.", agents: A("Scout"), tasks: A("CreateOrder"), priority: 748),

            P("ORDER.CONFIDENCE_GATE_IS_REAL", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Το order confidence αντικατοπτρίζει το ασθενέστερο κρίσιμο unresolved/resolved σκέλος και δεν φουσκώνεται για να περάσει threshold. Κάτω από το configured gate ζητείται clarification ή απορρίπτεται το write.", agents: A("Scout", "Jarvis"), tasks: A("CreateOrder"), priority: 747),

            P("ORDER.SOURCE_INSTRUCTION_IS_DURABLE", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "Το sourceInstruction διατηρεί πιστά την οδηγία χρήστη για audit/learning και δεν αντικαθίσταται από generic text.", agents: A("Scout", "Jarvis"), tasks: A("CreateOrder"), priority: 746),

            P("ORDER.SUCCESS_LINK_AND_OPTIONAL_RATING", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Both,
                "Μετά από verified CreateOrder εμφάνισε authoritative document link. Αν το result περιέχει πραγματικό rating/audit reference, μπορεί να εμφανιστεί το registered rating link· αν λείπει δεν επινοείται.", agents: A("Jarvis", "Scout"), tasks: A("CreateOrder"), priority: 745),

            // ── Browser / Scout ─────────────────────────────────────────────
            P("BROWSER.NAVIGATE_ON_USER_INTENT", JarvisPolicyScope.Tool, JarvisPolicyEnforcement.Both,
                "Browser navigation γίνεται μόνο ως μέρος του resolved user/research intent. Μην ανοίγεις αυθαίρετα άσχετες σελίδες.", agents: A("Scout"), tasks: A("InternetResearch"), tools: A("open_url"), priority: 730),

            P("BROWSER.READ_BEFORE_CONTENT_CLAIM", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "Μην κάνεις factual claim για περιεχόμενο σελίδας μόνο επειδή έγινε navigation. Πρέπει να υπάρχει page-content/table evidence από registered read/extraction tool.", agents: A("Scout", "Jarvis"), tasks: A("InternetResearch"), priority: 729),

            P("BROWSER.TABLE_EXTRACTION_FALLBACK", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Για πραγματικά tabular web data προτίμησε table extraction. Αν η σελίδα δεν έχει table αλλά έχει σαφώς επαναλαμβανόμενη δομή, χρησιμοποίησε page content και αναδόμησε μόνο όσα υποστηρίζει το evidence· σε χαοτικό/ασαφές content μην μαντεύεις rows.", agents: A("Scout"), tasks: A("InternetResearch"), priority: 728),

            // ── Courier / Sprint ────────────────────────────────────────────
            P("COURIER.DOCUMENTS_TO_REGISTERED_UI", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Training,
                "Courier document discovery προβάλλεται μέσω του registered courier UI projection όταν αυτό είναι το requested outcome. Μην αντικαθιστάς το UI projection με ανεξάρτητη raw chat list.", agents: A("Sprint", "Jarvis"), tasks: A("CourierDocuments"), priority: 710),

            P("COURIER.VOUCHER_RESOLVE_BEFORE_CREATE", JarvisPolicyScope.Validation, JarvisPolicyEnforcement.Both,
                "CreateCourierVoucher απαιτεί resolved eligible document, provider και required receiver/shipment data πριν το terminal external action. Μην μαντεύεις provider capability ή persisted voucher data.", agents: A("Sprint", "Jarvis"), tasks: A("CreateCourierVoucher"), priority: 709),

            P("COURIER.VOUCHER_CONFIRMATION", JarvisPolicyScope.Execution, JarvisPolicyEnforcement.Both,
                "Courier voucher creation/cancellation ακολουθεί το registered irreversible-action confirmation contract και δεν επαναλαμβάνεται αυτόματα μετά από αβέβαιο external result.", agents: A("Sprint", "Jarvis"), tasks: A("CreateCourierVoucher", "CancelCourierVoucher"), priority: 708),

            // ── Help / conversational mode ──────────────────────────────────
            P("HELP.CLARIFY_THEN_SOLVE", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Training,
                "Σε Help mode συγκέντρωσε πρώτα τις απαραίτητες διευκρινίσεις, μετά δώσε τη λύση. Μην παράγεις durable help/audit summary πριν ο χειριστής δηλώσει ότι ολοκληρώθηκε το συγκεκριμένο θέμα.", agents: A("Sage", "Jarvis"), tasks: A("__help"), priority: 690),

            P("HELP.DURABLE_SUMMARY_AFTER_CLOSE", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Training,
                "Όταν κλείνει επιβεβαιωμένα help case, η durable περίληψη πρέπει να περιέχει keywords, συμπυκνωμένο αίτημα και αναλυτική επαναχρησιμοποιήσιμη λύση στο registered machine-readable format.", agents: A("Sage", "Jarvis"), tasks: A("__help"), priority: 689),

            // ── Presentation ────────────────────────────────────────────────
            P("PRESENTATION.VALIDATED_FACTS_ONLY", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Both,
                "Presentation layer αλλάζει μόνο wording/μορφοποίηση πάνω σε validated context. Δεν κάνει query/action, δεν αλλάζει business facts και δεν συμπληρώνει ελλείποντα στοιχεία.", agents: A("Jarvis"), tasks: A("__presentation"), priority: 680),

            P("PRESENTATION.HUMAN_READABLE", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Training,
                "User-facing report/email πρέπει να είναι σύντομο, φυσικό και επαγγελματικό, όχι raw key=value dump. Σε email το subject είναι σχετικό και το body έτοιμο για πραγματική αποστολή.", agents: A("Jarvis"), tasks: A("__presentation"), priority: 679),

            P("PRESENTATION.DO_NOT_HIDE_PARTIAL_FAILURE", JarvisPolicyScope.Presentation, JarvisPolicyEnforcement.Both,
                "Σε multi-task request παρουσίασε ξεχωριστά ποια outcomes ολοκληρώθηκαν, ποια απέτυχαν και ποια χρειάζονται clarification. Μην μετατρέπεις partial failure σε συνολικό success ή συνολικό abort.", agents: A("Jarvis"), tasks: A("__presentation"), priority: 678),

            // ── Tool fallback policies: central source, never tool-registry prose ──
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
