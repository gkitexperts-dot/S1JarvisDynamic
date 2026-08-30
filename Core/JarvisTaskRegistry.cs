using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    internal enum JarvisTaskOperation
    {
        Read,
        Write,
        ExternalAction,
        Mixed
    }

    internal enum JarvisTaskExecutionPolicy
    {
        Sequential,
        ParallelSafe,
        DependsOnInputs
    }

    internal sealed class JarvisTaskDescriptor
    {
        public JarvisTaskDescriptor(
            string taskType,
            string capability,
            string ownerAgent,
            JarvisTaskOperation operation,
            bool requiresConfirmation,
            JarvisTaskExecutionPolicy executionPolicy,
            string description,
            string[] tools,
            string[] requiredInputs,
            string[] optionalInputs,
            string[] produces,
            string[] dependencyCapabilities,
            string[] intentHints)
        {
            TaskType = taskType ?? string.Empty;
            Capability = capability ?? string.Empty;
            OwnerAgent = ownerAgent ?? string.Empty;
            Operation = operation;
            RequiresConfirmation = requiresConfirmation;
            ExecutionPolicy = executionPolicy;
            Description = description ?? string.Empty;
            Tools = tools ?? new string[0];
            RequiredInputs = requiredInputs ?? new string[0];
            OptionalInputs = optionalInputs ?? new string[0];
            Produces = produces ?? new string[0];
            DependencyCapabilities = dependencyCapabilities ?? new string[0];
            IntentHints = intentHints ?? new string[0];
        }

        public string TaskType { get; private set; }
        public string Capability { get; private set; }
        public string OwnerAgent { get; private set; }
        public JarvisTaskOperation Operation { get; private set; }
        public bool RequiresConfirmation { get; private set; }
        public JarvisTaskExecutionPolicy ExecutionPolicy { get; private set; }
        public string Description { get; private set; }
        public string[] Tools { get; private set; }
        public string[] RequiredInputs { get; private set; }
        public string[] OptionalInputs { get; private set; }
        public string[] Produces { get; private set; }
        public string[] DependencyCapabilities { get; private set; }
        public string[] IntentHints { get; private set; }
    }

    /// <summary>
    /// Atomic business-task catalog used by Jarvis orchestration planning.
    ///
    /// Rules:
    /// - one task represents one user-visible business outcome;
    /// - helper reads may live inside a write task when they are prerequisites
    ///   of that single write outcome;
    /// - independent read/write intents must be separate tasks;
    /// - OwnerAgent is internal metadata only; Jarvis remains user-facing;
    /// - tools must exist in JarvisToolRegistry and confirmation policy remains
    ///   authoritative regardless of planner output.
    ///
    /// Task contracts are business-level contracts, not blind copies of a
    /// single tool schema. Tool-level prerequisites are documented in
    /// TOOLS_INVENTORY.md and may be resolved by helper tools inside the task.
    ///
    /// This registry still does not replace the mature Main Chat runtime router.
    /// </summary>
    internal static class JarvisTaskRegistry
    {
        private static readonly JarvisTaskDescriptor[] Tasks =
        {
            T(
                "ReportData", "Reporting", "Atlas", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.ParallelSafe,
                "Read, aggregate or analyze Soft1 business data and return a structured result.",
                A("query_data"),
                A("business_question"),
                A("filters", "date_range", "entity_reference", "entity_role", "document_scope", "operator_scope", "result_mode"),
                A("dataset", "summary", "query_sql"),
                A(),
                A("report", "analysis", "sales", "turnover", "balance", "movement", "list")),

            T(
                "ExportData", "Export", "Atlas", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Export business data to a file. It can autonomously plan a validated SELECT from export_request or reuse an explicitly bound upstream report result/query provenance.",
                A("export_query_to_file", "export_shown_table"),
                A("export_request"),
                A("source_result", "sql", "format", "filename", "columns", "visible_table", "entity_role", "document_scope", "operator_scope", "result_mode"),
                A("file_artifact", "path", "filename"),
                A("Reporting"),
                A("export", "excel", "xlsx", "csv", "pdf", "file")),

            T(
                "OpenDocument", "DocumentRead", "Atlas", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Resolve and open an existing Soft1 document/object. A human document reference may be resolved to SOSOURCE/FINDOC before navigation.",
                A("open_document"),
                A("document_reference"),
                A("sosource", "findoc", "series", "number", "mode"),
                A("opened_document", "findoc", "sosource", "objectName"),
                A(),
                A("open document", "show invoice", "open invoice", "παραστατικό")),

            T(
                "ResolveDocumentConversion", "DocumentConversion", "Atlas", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Find valid conversion targets for a resolved Soft1 document.",
                A("get_conversion_targets"),
                A("findoc"),
                A("sosource"),
                A("conversion_targets"),
                A("DocumentRead"),
                A("conversion", "transform", "μετασχηματισ")),

            T(
                "CreateItem", "ItemWrite", "Forge", JarvisTaskOperation.Write, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Create a Soft1 item using the approved item-template/write flow.",
                A("get_item_template", "create_item"),
                A("name", "mtrunit1", "vat", "mtracn", "mtrlotuse", "mtrsnuse"),
                A("code", "templateMtrl", "mtrunit3", "mtrunit4", "pricer", "pricew", "copiedFields"),
                A("item_reference", "mtrl", "code", "name"),
                A("ItemRead", "InternetResearch"),
                A("create item", "new item", "είδος", "δημιουργία είδους")),

            T(
                "FindTrader", "TraderLookup", "Compass", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.ParallelSafe,
                "Resolve an existing trader/customer/supplier by AFM or business identity.",
                A("find_trader_by_afm", "get_aade_data"),
                A("trader_identity"),
                A("afm", "name", "role", "sodType"),
                A("trader_data", "trdrId", "afm", "name", "sodType"),
                A(),
                A("customer", "supplier", "trader", "afm", "πελάτη", "προμηθευτή", "αφμ")),

            T(
                "CreateTrader", "TraderWrite", "Compass", JarvisTaskOperation.Write, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Create a Soft1 trader from resolved AADE/business data. AADE may provide name/address/code suggestions inside the task before confirmation.",
                A("find_trader_by_afm", "get_aade_data", "create_trader_from_aade"),
                A("afm", "role"),
                A("sodType", "name", "code", "address", "city", "doy", "zip", "jobType", "resolved_aade_data"),
                A("trader_reference", "trdrId", "sodType", "objectName", "code", "name"),
                A("TraderLookup"),
                A("create customer", "create supplier", "new trader", "νέος πελάτης", "νέος προμηθευτής")),

            T(
                "ReadInbox", "Email", "Echo", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.ParallelSafe,
                "Read/filter Outlook inbox messages and optionally download attachment artifacts from a resolved message.",
                A("filter_email_inbox", "read_email", "download_email_attachment"),
                A("email_request"),
                A("count", "searchText", "sender", "date_range", "read_state", "messageId", "attachmentName"),
                A("email_messages", "messageId", "email_artifacts", "attachment_paths"),
                A(),
                A("email", "inbox", "message", "attachment", "εισερχόμενα", "μήνυμα", "συνημμένο")),

            T(
                "ReadCalendar", "Calendar", "Echo", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.ParallelSafe,
                "Read or filter Outlook calendar entries without creating or changing events.",
                A("filter_calendar", "show_calendar_entries", "read_calendar"),
                A("calendar_request"),
                A("startDate", "endDate", "searchText", "date_range"),
                A("calendar_entries", "eventId"),
                A(),
                A("calendar", "what do I have", "schedule", "τι έχω", "ημερολόγιο", "ραντεβού")),

            T(
                "CreateCalendarEvent", "CalendarWrite", "Echo", JarvisTaskOperation.ExternalAction, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Create one Outlook calendar event/reminder; contact lookup may be used only to resolve attendee addresses.",
                A("search_outlook_contacts", "show_contact_results", "create_outlook_event"),
                A("subject", "start"),
                A("end", "location", "attendees", "body", "reminderMinutesBeforeStart", "isAllDay"),
                A("calendar_event", "eventId", "webLink"),
                A("Contacts"),
                A("create event", "calendar event", "outlook reminder", "κλείσε ραντεβού", "βάλε στο calendar", "υπενθύμιση outlook")),

            T(
                "CreateCrmTask", "CRM", "Echo", JarvisTaskOperation.Write, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Create one CRM task/action in Soft1. The assignee must resolve to actorUserId or actorUserIds before the write.",
                A("create_crm_task"),
                A("title", "description", "fromDate", "assignee"),
                A("actorUserId", "actorUserIds", "reminderDate", "trdr", "tsodType", "parentSoactionId", "inst", "prjc", "durationMinutes"),
                A("crm_task_reference", "soaction_ids"),
                A("TraderLookup", "Email", "Calendar", "Reporting"),
                A("crm task", "follow up", "εργασία crm", "ενέργεια crm", "ανάθεση εργασίας")),

            T(
                "SendEmail", "EmailWrite", "Echo", JarvisTaskOperation.ExternalAction, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Send one new Outlook email; a named recipient may be resolved to an address with contact lookup before confirmation.",
                A("search_outlook_contacts", "show_contact_results", "send_email"),
                A("to", "subject", "body"),
                A("recipient_name", "cc", "attachmentFilePath", "attachmentContent", "attachmentFilename", "artifact_reference"),
                A("email_send_result", "success", "hasAttachment"),
                A("Contacts", "Export", "OrderWrite", "DocumentRead"),
                A("send email", "στείλε email", "στείλε μήνυμα", "email this")),

            T(
                "ReplyEmail", "EmailWrite", "Echo", JarvisTaskOperation.ExternalAction, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Reply to one already resolved Outlook message using its Graph message id.",
                A("read_email", "reply_email"),
                A("messageId", "body"),
                A("cc"),
                A("email_reply_result", "success"),
                A("Email"),
                A("reply email", "reply to", "απάντησε", "απάντησε στο email", "απάντηση στο μήνυμα")),

            T(
                "CourierDocuments", "CourierRead", "Sprint", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.ParallelSafe,
                "Find and surface Soft1 documents eligible for courier handling. Query resolution precedes UI projection; voucher data may then be resolved for one FINDOC.",
                A("show_courier_documents", "get_courier_voucher_data"),
                A("courier_request"),
                A("entries", "findocId", "document_filters", "provider"),
                A("courier_documents", "findocId", "voucher_data", "providers"),
                A("Reporting"),
                A("courier", "voucher", "shipment", "αποστολή", "voucher data")),

            T(
                "CreateCourierVoucher", "CourierWrite", "Sprint", JarvisTaskOperation.ExternalAction, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Create one courier voucher for a resolved eligible FINDOC. get_courier_voucher_data resolves receiver/provider capability data before confirmation.",
                A("get_courier_voucher_data", "create_courier_voucher"),
                A("findocId", "providerCode"),
                A("documentNumber", "documentRef", "senderName", "senderAddress", "senderCity", "senderZipCode", "senderPhone", "receiverName", "receiverContactName", "receiverAddress", "receiverCity", "receiverZipCode", "receiverPhone", "pieces", "weight", "comments", "isCOD", "codAmount", "codPaymentType", "codChequeDate", "deliveryTimeRequested", "deliveryTimeFrom", "deliveryTimeTo", "deliveryDate", "saturdayDelivery"),
                A("voucher_reference", "shipmentNumber", "providerCode", "pdfLink", "courier_artifact"),
                A("CourierRead", "DocumentRead"),
                A("create voucher", "ship", "send courier", "έκδοση voucher")),

            T(
                "CancelCourierVoucher", "CourierWrite", "Sprint", JarvisTaskOperation.ExternalAction, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Cancel one existing resolved courier voucher. The persisted provider name and shipment number must be resolved before confirmation.",
                A("get_courier_voucher_data", "cancel_courier_voucher"),
                A("findocId", "providerName", "shipmentNumber"),
                A("jobId"),
                A("courier_cancel_result", "success", "findocId"),
                A("CourierRead"),
                A("cancel voucher", "cancel shipment", "ακύρωση voucher")),

            T(
                "InternetResearch", "InternetResearch", "Scout", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.ParallelSafe,
                "Research public Internet sources using browser navigation, page reading and table extraction.",
                A("open_url", "read_page_content", "extract_page_tables"),
                A("research_question"),
                A("url", "source_constraints"),
                A("research_result", "web_tables", "sources"),
                A(),
                A("internet", "web", "site", "research", "τεχνικά χαρακτηριστικά", "ιστοσελίδα")),

            T(
                "CreateOrder", "OrderWrite", "Scout", JarvisTaskOperation.Write, true,
                JarvisTaskExecutionPolicy.DependsOnInputs,
                "Create one supported Soft1 document/order. Trader, series and item-line IDs must be resolved before the write and confidence-gated.",
                A("create_order"),
                A("sosource", "series", "trdrId", "lines", "sourceInstruction", "confidence"),
                A("payment", "shipment", "confidenceNotes"),
                A("order_reference", "document_reference", "findocId", "sosource", "objectName", "linesWritten", "promptLogSoactionId"),
                A("TraderLookup", "ItemRead", "InternetResearch", "Reporting"),
                A("create order", "new order", "παραγγελία", "καταχώρηση παραγγελίας")),

            T(
                "HelpLookup", "Help", "Sage", JarvisTaskOperation.Read, false,
                JarvisTaskExecutionPolicy.ParallelSafe,
                "Answer Soft1 help/knowledge questions using shared read/reporting/document tools.",
                A("query_data", "open_document", "export_query_to_file"),
                A("help_question"),
                A("entity_reference", "report_request"),
                A("help_answer", "supporting_data"),
                A("Reporting", "DocumentRead"),
                A("help", "how do I", "soft1", "βοήθεια", "πως κάνω"))
        };

        internal static IReadOnlyList<JarvisTaskDescriptor> AllTasks
        {
            get { return Tasks; }
        }

        internal static JarvisTaskDescriptor Find(string taskType)
        {
            if (string.IsNullOrWhiteSpace(taskType)) return null;
            return Tasks.FirstOrDefault(x => string.Equals(
                x.TaskType, taskType.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        internal static IEnumerable<JarvisTaskDescriptor> ForCapability(string capability)
        {
            if (string.IsNullOrWhiteSpace(capability))
                return Enumerable.Empty<JarvisTaskDescriptor>();

            return Tasks.Where(x => string.Equals(
                x.Capability, capability.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        internal static IEnumerable<JarvisTaskDescriptor> ForAgent(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName))
                return Enumerable.Empty<JarvisTaskDescriptor>();

            return Tasks.Where(x => string.Equals(
                x.OwnerAgent, agentName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        internal static string[] ValidateAgainstToolRegistry()
        {
            var issues = new List<string>();

            foreach (IGrouping<string, JarvisTaskDescriptor> group in Tasks.GroupBy(
                x => x.TaskType, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() > 1)
                    issues.Add("Duplicate task registration: " + group.Key);
            }

            foreach (JarvisTaskDescriptor task in Tasks)
            {
                if (string.IsNullOrWhiteSpace(task.Capability))
                    issues.Add("Task without capability: " + task.TaskType);
                if (string.IsNullOrWhiteSpace(task.OwnerAgent))
                    issues.Add("Task without owner agent: " + task.TaskType);
                if (task.Tools.Length == 0)
                    issues.Add("Task without tools: " + task.TaskType);
                if ((task.Operation == JarvisTaskOperation.Write ||
                     task.Operation == JarvisTaskOperation.ExternalAction) &&
                    !task.RequiresConfirmation)
                    issues.Add("State-changing task without confirmation: " + task.TaskType);

                foreach (string toolName in task.Tools)
                {
                    if (JarvisToolRegistry.Find(toolName) == null)
                        issues.Add("Task references unregistered tool: " + task.TaskType + " -> " + toolName);
                }
            }

            return issues.ToArray();
        }

        private static JarvisTaskDescriptor T(
            string taskType,
            string capability,
            string ownerAgent,
            JarvisTaskOperation operation,
            bool requiresConfirmation,
            JarvisTaskExecutionPolicy executionPolicy,
            string description,
            string[] tools,
            string[] requiredInputs,
            string[] optionalInputs,
            string[] produces,
            string[] dependencyCapabilities,
            string[] intentHints)
        {
            return new JarvisTaskDescriptor(
                taskType,
                capability,
                ownerAgent,
                operation,
                requiresConfirmation,
                executionPolicy,
                description,
                tools,
                requiredInputs,
                optionalInputs,
                produces,
                dependencyCapabilities,
                intentHints);
        }

        private static string[] A(params string[] values)
        {
            return values ?? new string[0];
        }
    }
}
