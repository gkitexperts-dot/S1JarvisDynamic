using System;
using System.Collections.Generic;
using System.Linq;

namespace S1Jarvis.Core
{
    internal enum JarvisToolOperation
    {
        Read,
        Write,
        Mixed
    }

    internal enum JarvisToolUiEffect
    {
        None,
        ChatText,
        Table,
        File,
        Soft1Object,
        EmailList,
        CalendarList,
        ContactList,
        CourierList,
        Browser,
        ExternalAction
    }

    internal sealed class JarvisToolDescriptor
    {
        public JarvisToolDescriptor(
            string name,
            string domain,
            string ownerAgent,
            JarvisToolOperation operation,
            bool requiresConfirmation,
            JarvisToolUiEffect uiEffect,
            string[] allowedAgents,
            string[] capabilities,
            string[] compactModes,
            bool durableResult,
            string fallbackPolicy)
        {
            Name = name;
            Domain = domain;
            OwnerAgent = ownerAgent;
            Operation = operation;
            RequiresConfirmation = requiresConfirmation;
            UiEffect = uiEffect;
            AllowedAgents = allowedAgents ?? new string[0];
            Capabilities = capabilities ?? new string[0];
            CompactModes = compactModes ?? new string[0];
            DurableResult = durableResult;
            FallbackPolicy = fallbackPolicy ?? string.Empty;
        }

        public string Name { get; private set; }
        public string Domain { get; private set; }
        public string OwnerAgent { get; private set; }
        public JarvisToolOperation Operation { get; private set; }
        public bool RequiresConfirmation { get; private set; }
        public JarvisToolUiEffect UiEffect { get; private set; }
        public string[] AllowedAgents { get; private set; }
        public string[] Capabilities { get; private set; }
        public string[] CompactModes { get; private set; }
        public bool DurableResult { get; private set; }
        public string FallbackPolicy { get; private set; }
    }

    internal sealed class JarvisRoutingDescriptor
    {
        public JarvisRoutingDescriptor(string capability, string agent, string notes)
        {
            Capability = capability;
            Agent = agent;
            Notes = notes ?? string.Empty;
        }

        public string Capability { get; private set; }
        public string Agent { get; private set; }
        public string Notes { get; private set; }
    }

    /// <summary>
    /// Canonical inventory metadata for Jarvis AI tools and internal routing.
    ///
    /// Phase 1 rule: this registry documents the current runtime architecture
    /// but does not yet drive routing or optimizer behavior. That migration is
    /// intentionally a separate step after the inventory has been reviewed.
    ///
    /// User-visible product identity remains Jarvis. Agent names below are
    /// internal implementation roles only.
    /// </summary>
    internal static class JarvisToolRegistry
    {
        private static readonly JarvisToolDescriptor[] Tools =
        {
            T("query_data", "Reporting", "Atlas", JarvisToolOperation.Read, false, JarvisToolUiEffect.Table,
                A("Atlas", "Forge", "Compass", "Echo", "Sprint", "Scout", "Sage", "Jarvis"),
                A("Reporting", "SqlRead", "EntityLookup"),
                A("atlas-read", "direct-export", "latest-user-document", "forge", "compass", "echo-*", "sprint", "scout", "sage"),
                true, "Fail closed to no data change; SELECT-only validation remains authoritative in JarvisTools."),

            T("export_query_to_file", "Reporting", "Atlas", JarvisToolOperation.Read, false, JarvisToolUiEffect.File,
                A("Atlas", "Forge", "Compass", "Echo", "Scout", "Sage", "Jarvis"),
                A("Reporting", "Export"), A("direct-export", "atlas-read", "echo-export", "scout", "sage"),
                true, "Return a safe error and preserve the source query/result context; never claim export success without tool success."),

            T("export_shown_table", "Reporting", "Atlas", JarvisToolOperation.Read, false, JarvisToolUiEffect.File,
                A("Atlas", "Forge", "Echo", "Jarvis"), A("Reporting", "Export", "VisibleTable"),
                A("direct-export", "atlas-read", "forge", "echo-export"), true,
                "Export only the already visible table; do not re-query silently."),

            T("open_document", "Soft1Documents", "Atlas", JarvisToolOperation.Read, false, JarvisToolUiEffect.Soft1Object,
                A("Atlas", "Forge", "Compass", "Echo", "Sprint", "Scout", "Sage", "Jarvis"),
                A("DocumentRead", "Soft1Navigation"), A("atlas-read", "forge", "compass", "echo-export", "sprint", "scout", "sage"),
                false, "If the object cannot be resolved/opened, report failure without inventing document state."),

            T("get_conversion_targets", "Soft1Documents", "Atlas", JarvisToolOperation.Read, false, JarvisToolUiEffect.None,
                A("Atlas", "Scout", "Jarvis"), A("DocumentRead", "DocumentConversion"), A("atlas-read", "scout"), false,
                "Return only real conversion targets available in the active Soft1 context."),

            T("get_item_template", "Items", "Forge", JarvisToolOperation.Read, false, JarvisToolUiEffect.None,
                A("Forge", "Scout"), A("ItemRead", "ItemWrite"), A("forge", "scout"), false,
                "If template metadata is unavailable, stop before create_item and request/return the missing information."),

            T("create_item", "Items", "Forge", JarvisToolOperation.Write, true, JarvisToolUiEffect.Soft1Object,
                A("Forge", "Scout"), A("ItemWrite"), A("forge", "scout"), true,
                "No success claim without a successful Soft1 result; confirmation/required-field policy must remain enforced."),

            T("find_trader_by_afm", "Traders", "Compass", JarvisToolOperation.Read, false, JarvisToolUiEffect.None,
                A("Compass"), A("TraderLookup", "TraderWrite"), A("compass"), false,
                "Return ambiguous matches for clarification instead of guessing an entity."),

            T("get_aade_data", "Traders", "Compass", JarvisToolOperation.Read, false, JarvisToolUiEffect.None,
                A("Compass"), A("TraderLookup", "ExternalBusinessData"), A("compass"), false,
                "If AADE data is unavailable or incomplete, do not fabricate taxpayer/company fields."),

            T("create_trader_from_aade", "Traders", "Compass", JarvisToolOperation.Write, true, JarvisToolUiEffect.Soft1Object,
                A("Compass"), A("TraderWrite"), A("compass"), true,
                "Require a resolved target and successful Soft1 write before reporting creation."),

            T("search_outlook_contacts", "Email", "Echo", JarvisToolOperation.Read, false, JarvisToolUiEffect.ContactList,
                A("Echo", "Scout", "Jarvis"), A("Email", "Contacts"), A("echo-contact", "echo-draft", "echo-send", "scout"), false,
                "If multiple contacts match, surface choices; do not silently select a recipient."),

            T("show_contact_results", "Email", "Echo", JarvisToolOperation.Read, false, JarvisToolUiEffect.ContactList,
                A("Echo", "Scout", "Jarvis"), A("Email", "Contacts", "UiProjection"), A("echo-contact", "echo-draft", "scout"), false,
                "Project only resolved contact results into the UI."),

            T("filter_email_inbox", "Email", "Echo", JarvisToolOperation.Read, false, JarvisToolUiEffect.EmailList,
                A("Echo", "Jarvis"), A("Email", "Inbox", "UiProjection"), A("echo-inbox"), false,
                "Filter the visible inbox list; if nothing matches, return an empty result rather than unrelated messages."),

            T("read_email", "Email", "Echo", JarvisToolOperation.Read, false, JarvisToolUiEffect.ChatText,
                A("Echo", "Scout", "Jarvis"), A("Email", "Inbox"), A("echo-inbox", "scout"), true,
                "Read only the resolved message; never invent body/sender/attachment data."),

            T("download_email_attachment", "Email", "Echo", JarvisToolOperation.Read, false, JarvisToolUiEffect.File,
                A("Echo", "Scout", "Jarvis"), A("Email", "Inbox", "Attachment"), A("echo-inbox", "scout"), true,
                "Only expose the file after successful attachment download."),

            T("filter_calendar", "Calendar", "Echo", JarvisToolOperation.Read, false, JarvisToolUiEffect.CalendarList,
                A("Echo", "Jarvis"), A("Calendar", "UiProjection"), A("echo-calendar"), false,
                "Filter only actual calendar entries; empty match is a valid result."),

            T("show_calendar_entries", "Calendar", "Echo", JarvisToolOperation.Read, false, JarvisToolUiEffect.CalendarList,
                A("Echo", "Jarvis"), A("Calendar", "UiProjection"), A("echo-calendar"), false,
                "Project only returned calendar entries into the UI."),

            T("read_calendar", "Calendar", "Echo", JarvisToolOperation.Read, false, JarvisToolUiEffect.ChatText,
                A("Echo", "Jarvis"), A("Calendar"), A("echo-calendar"), true,
                "Do not infer missing meeting details beyond the retrieved event."),

            T("create_outlook_event", "Calendar", "Echo", JarvisToolOperation.Write, true, JarvisToolUiEffect.ExternalAction,
                A("Echo", "Scout", "Jarvis"), A("Calendar", "CalendarWrite"), A("echo-calendar", "scout"), true,
                "Require resolved date/time/attendees as applicable and successful Outlook result."),

            T("create_crm_task", "CRM", "Echo", JarvisToolOperation.Write, true, JarvisToolUiEffect.Soft1Object,
                A("Echo", "Scout", "Jarvis"), A("CRM", "TaskWrite"), A("echo-calendar", "scout"), true,
                "No completion claim without successful CRM task creation."),

            T("send_email", "Email", "Echo", JarvisToolOperation.Write, true, JarvisToolUiEffect.ExternalAction,
                A("Echo", "Scout", "Jarvis"), A("Email", "EmailWrite"), A("echo-send", "echo-export", "scout"), true,
                "Recipient/content must be resolved and send success must come from the tool result."),

            T("reply_email", "Email", "Echo", JarvisToolOperation.Write, true, JarvisToolUiEffect.ExternalAction,
                A("Echo", "Scout", "Jarvis"), A("Email", "EmailWrite"), A("echo-send", "scout"), true,
                "Reply only to a resolved source message and require successful send result."),

            T("show_courier_documents", "Courier", "Sprint", JarvisToolOperation.Read, false, JarvisToolUiEffect.CourierList,
                A("Sprint", "Jarvis"), A("Courier", "CourierRead", "UiProjection"), A("sprint"), false,
                "Project only eligible returned documents into the Courier list."),

            T("get_courier_voucher_data", "Courier", "Sprint", JarvisToolOperation.Read, false, JarvisToolUiEffect.ChatText,
                A("Sprint", "Jarvis"), A("Courier", "CourierRead"), A("sprint"), true,
                "Return only persisted voucher/provider data for the resolved document."),

            T("create_courier_voucher", "Courier", "Sprint", JarvisToolOperation.Write, true, JarvisToolUiEffect.ExternalAction,
                A("Sprint", "Jarvis"), A("Courier", "CourierWrite"), A("sprint"), true,
                "Require a resolved document/provider and successful courier result before reporting voucher creation."),

            T("cancel_courier_voucher", "Courier", "Sprint", JarvisToolOperation.Write, true, JarvisToolUiEffect.ExternalAction,
                A("Sprint", "Jarvis"), A("Courier", "CourierWrite"), A("sprint"), true,
                "Require an existing resolved voucher and successful provider cancellation result."),

            T("open_url", "Browser", "Scout", JarvisToolOperation.Read, false, JarvisToolUiEffect.Browser,
                A("Scout"), A("Browser", "InternetResearch"), A("scout"), false,
                "Navigate only to the requested/research target; navigation success is not evidence of page facts."),

            T("read_page_content", "Browser", "Scout", JarvisToolOperation.Read, false, JarvisToolUiEffect.ChatText,
                A("Scout"), A("Browser", "InternetResearch"), A("scout"), true,
                "Base claims only on content actually returned from the loaded page."),

            T("extract_page_tables", "Browser", "Scout", JarvisToolOperation.Read, false, JarvisToolUiEffect.Table,
                A("Scout"), A("Browser", "InternetResearch", "WebTable"), A("scout"), true,
                "Return only tables actually extracted from the current page."),

            T("create_order", "Orders", "Scout", JarvisToolOperation.Write, true, JarvisToolUiEffect.Soft1Object,
                A("Scout"), A("OrderWrite", "BrowserAssistedAction"), A("scout"), true,
                "Require all business keys/lines to be resolved and a successful Soft1 write before reporting creation.")
        };

        private static readonly JarvisRoutingDescriptor[] Routing =
        {
            R("Reporting", "Atlas", "Read/reporting and generic Soft1 data analysis."),
            R("SqlRead", "Atlas", "Generic SELECT/query capability."),
            R("DocumentRead", "Atlas", "Generic Soft1 document lookup/opening unless a dedicated domain owns the turn."),
            R("ItemRead", "Forge", "Item-specific lookup/template work."),
            R("ItemWrite", "Forge", "Item creation and item-domain write actions."),
            R("TraderLookup", "Compass", "Trader/AFM/AADE resolution."),
            R("TraderWrite", "Compass", "Trader creation/update flow."),
            R("Email", "Echo", "Inbox, contacts and mail actions."),
            R("Calendar", "Echo", "Calendar read/write actions."),
            R("CRM", "Echo", "CRM task creation in the current architecture."),
            R("Courier", "Sprint", "Courier document/voucher workflows."),
            R("Browser", "Scout", "Browser curtain and Internet research."),
            R("InternetResearch", "Scout", "Web navigation/read/extraction research flow."),
            R("OrderWrite", "Scout", "Current architecture exposes create_order through Scout."),
            R("Help", "Sage", "Soft1 help/knowledge mode; primarily read/query/open/export capabilities.")
        };

        internal static IReadOnlyList<JarvisToolDescriptor> AllTools
        {
            get { return Tools; }
        }

        internal static IReadOnlyList<JarvisRoutingDescriptor> AllRoutes
        {
            get { return Routing; }
        }

        internal static JarvisToolDescriptor Find(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return null;
            return Tools.FirstOrDefault(x => string.Equals(x.Name, toolName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        internal static IEnumerable<JarvisToolDescriptor> ForAgent(string agentName)
        {
            if (string.IsNullOrWhiteSpace(agentName)) return Enumerable.Empty<JarvisToolDescriptor>();
            return Tools.Where(x => x.AllowedAgents.Any(a => string.Equals(a, agentName.Trim(), StringComparison.OrdinalIgnoreCase)));
        }

        internal static IEnumerable<JarvisToolDescriptor> ForCapability(string capability)
        {
            if (string.IsNullOrWhiteSpace(capability)) return Enumerable.Empty<JarvisToolDescriptor>();
            return Tools.Where(x => x.Capabilities.Any(c => string.Equals(c, capability.Trim(), StringComparison.OrdinalIgnoreCase)));
        }

        internal static string ResolveOwnerForCapability(string capability)
        {
            if (string.IsNullOrWhiteSpace(capability)) return null;
            JarvisRoutingDescriptor route = Routing.FirstOrDefault(x =>
                string.Equals(x.Capability, capability.Trim(), StringComparison.OrdinalIgnoreCase));
            return route == null ? null : route.Agent;
        }

        internal static string[] ValidateInventory()
        {
            var issues = new List<string>();

            foreach (IGrouping<string, JarvisToolDescriptor> group in Tools.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                if (group.Count() > 1)
                    issues.Add("Duplicate tool registration: " + group.Key);

            foreach (JarvisToolDescriptor tool in Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.OwnerAgent))
                    issues.Add("Tool without owner: " + tool.Name);
                if (tool.AllowedAgents.Length == 0)
                    issues.Add("Tool without allowed agents: " + tool.Name);
                if (tool.Capabilities.Length == 0)
                    issues.Add("Tool without capability: " + tool.Name);
                if (tool.Operation == JarvisToolOperation.Write && !tool.RequiresConfirmation)
                    issues.Add("Write tool without confirmation policy: " + tool.Name);
            }

            foreach (JarvisRoutingDescriptor route in Routing)
                if (!Tools.Any(t => t.Capabilities.Any(c => string.Equals(c, route.Capability, StringComparison.OrdinalIgnoreCase))))
                    issues.Add("Route capability without registered tool: " + route.Capability);

            return issues.ToArray();
        }

        private static JarvisToolDescriptor T(
            string name,
            string domain,
            string owner,
            JarvisToolOperation operation,
            bool confirmation,
            JarvisToolUiEffect uiEffect,
            string[] agents,
            string[] capabilities,
            string[] modes,
            bool durable,
            string fallback)
        {
            return new JarvisToolDescriptor(name, domain, owner, operation, confirmation, uiEffect,
                agents, capabilities, modes, durable, fallback);
        }

        private static JarvisRoutingDescriptor R(string capability, string agent, string notes)
        {
            return new JarvisRoutingDescriptor(capability, agent, notes);
        }

        private static string[] A(params string[] values)
        {
            return values ?? new string[0];
        }
    }
}
