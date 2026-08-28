# Jarvis Tools Inventory

Status: Phase 1 inventory baseline — 28/08/2026

`Core/JarvisToolRegistry.cs` is the machine-readable inventory source of truth. This document is the human-readable architecture map. During Phase 1 the registry is descriptive only: existing routing/optimizer behavior is not changed yet.

## Architecture contract

The user-facing product identity is always **Jarvis**. Names such as Atlas, Forge, Compass, Echo, Sprint, Scout and Sage are internal implementation roles and must not be exposed as separate assistants in Main Chat.

The intended architecture chain is:

`User intent -> Capability -> internal subAgent -> allowed tools -> result/UI effect`

Three concepts are deliberately kept separate:

- **OwnerAgent**: primary internal domain owner of the tool.
- **AllowedAgents**: roles that may currently receive/use the tool because some capabilities are shared.
- **Routing capability**: semantic capability that selects an internal role. Routing should eventually depend on capabilities rather than duplicated tool-name `if`/HashSet logic.

## Current routing map

| Capability | Internal owner | Current meaning |
|---|---|---|
| Reporting / SqlRead | Atlas | Generic Soft1 SELECT, reporting, analysis |
| DocumentRead | Atlas | Generic document lookup/opening |
| ItemRead / ItemWrite | Forge | Item template/read/create flows |
| TraderLookup / TraderWrite | Compass | AFM, AADE and trader flows |
| Email | Echo | Inbox, contacts, send/reply |
| Calendar | Echo | Calendar read/write |
| CRM | Echo | CRM task creation in current architecture |
| Courier | Sprint | Courier documents and vouchers |
| Browser / InternetResearch | Scout | Web navigation, reading and extraction |
| OrderWrite | Scout | `create_order` is currently exposed through Scout |
| Help | Sage | Soft1 help/knowledge mode using shared read/export tools |

> Note: `Help` is currently a routing-only capability; Sage uses shared Reporting/DocumentRead tools rather than Help-specific tool definitions. `ValidateInventory()` intentionally makes architectural gaps visible for the next phase rather than hiding them.

## Tool inventory

| Tool | Domain | Owner | Operation | Confirmation | UI effect | Main capabilities |
|---|---|---|---|---|---|---|
| `query_data` | Reporting | Atlas | Read | No | Table | Reporting, SqlRead, EntityLookup |
| `export_query_to_file` | Reporting | Atlas | Read | No | File | Reporting, Export |
| `export_shown_table` | Reporting | Atlas | Read | No | File | Reporting, Export, VisibleTable |
| `open_document` | Soft1Documents | Atlas | Read | No | Soft1 object | DocumentRead, Soft1Navigation |
| `get_conversion_targets` | Soft1Documents | Atlas | Read | No | None | DocumentRead, DocumentConversion |
| `get_item_template` | Items | Forge | Read | No | None | ItemRead, ItemWrite |
| `create_item` | Items | Forge | Write | Yes | Soft1 object | ItemWrite |
| `find_trader_by_afm` | Traders | Compass | Read | No | None | TraderLookup, TraderWrite |
| `get_aade_data` | Traders | Compass | Read | No | None | TraderLookup, ExternalBusinessData |
| `create_trader_from_aade` | Traders | Compass | Write | Yes | Soft1 object | TraderWrite |
| `search_outlook_contacts` | Email | Echo | Read | No | Contact list | Email, Contacts |
| `show_contact_results` | Email | Echo | Read | No | Contact list | Email, Contacts, UiProjection |
| `filter_email_inbox` | Email | Echo | Read | No | Email list | Email, Inbox, UiProjection |
| `read_email` | Email | Echo | Read | No | Chat text | Email, Inbox |
| `download_email_attachment` | Email | Echo | Read | No | File | Email, Inbox, Attachment |
| `filter_calendar` | Calendar | Echo | Read | No | Calendar list | Calendar, UiProjection |
| `show_calendar_entries` | Calendar | Echo | Read | No | Calendar list | Calendar, UiProjection |
| `read_calendar` | Calendar | Echo | Read | No | Chat text | Calendar |
| `create_outlook_event` | Calendar | Echo | Write | Yes | External action | Calendar, CalendarWrite |
| `create_crm_task` | CRM | Echo | Write | Yes | Soft1 object | CRM, TaskWrite |
| `send_email` | Email | Echo | Write | Yes | External action | Email, EmailWrite |
| `reply_email` | Email | Echo | Write | Yes | External action | Email, EmailWrite |
| `show_courier_documents` | Courier | Sprint | Read | No | Courier list | Courier, CourierRead, UiProjection |
| `get_courier_voucher_data` | Courier | Sprint | Read | No | Chat text | Courier, CourierRead |
| `create_courier_voucher` | Courier | Sprint | Write | Yes | External action | Courier, CourierWrite |
| `cancel_courier_voucher` | Courier | Sprint | Write | Yes | External action | Courier, CourierWrite |
| `open_url` | Browser | Scout | Read | No | Browser | Browser, InternetResearch |
| `read_page_content` | Browser | Scout | Read | No | Chat text | Browser, InternetResearch |
| `extract_page_tables` | Browser | Scout | Read | No | Table | Browser, InternetResearch, WebTable |
| `create_order` | Orders | Scout | Write | Yes | Soft1 object | OrderWrite, BrowserAssistedAction |

Total baseline registrations: **30 tools**.

## Registry fields

Every current and future tool registration must define:

- `Name`
- `Domain`
- `OwnerAgent`
- `Operation` (`Read`, `Write`, `Mixed`)
- `RequiresConfirmation`
- `UiEffect`
- `AllowedAgents`
- `Capabilities`
- `CompactModes`
- `DurableResult`
- `FallbackPolicy`

## New-tool rule

A new AI tool is not considered architecturally complete until all of the following happen in the same change:

1. Implement the tool definition and execution path.
2. Register it once in `JarvisToolRegistry`.
3. Assign one primary `OwnerAgent`.
4. Assign at least one semantic capability.
5. Declare read/write and confirmation policy.
6. Declare UI side effect and durable-result behavior.
7. Declare allowed agents/modes only where required by the current runtime.
8. Run inventory validation and resolve unexpected duplicate/orphan registrations.
9. Update this document only if the architecture/routing map changes; individual tool rows should eventually be generated from the registry rather than maintained manually.

## Validation policy

`JarvisToolRegistry.ValidateInventory()` currently checks metadata integrity:

- duplicate tool names;
- missing owner;
- missing allowed agents;
- missing capability;
- write tools without confirmation policy;
- routing capabilities that have no registered tool capability.

The next inventory-hardening step is to compare the registry automatically against the actual tool definitions exposed by `JarvisTools`, email, courier and browser surfaces. That will allow startup/build diagnostics for:

- runtime tool exists but registry entry is missing;
- registry entry exists but runtime tool was removed/renamed;
- optimizer references an unregistered tool;
- routing references an unknown capability/agent.

## Phase boundaries

### Phase 1 — current task

- Central registry established.
- Current 30-tool baseline mapped.
- Current subAgent ownership mapped.
- Current capability/routing map documented.
- No routing behavior changed.

### Phase 2 — after inventory review

- Make optimizer tool sets derive from the registry.
- Make capability-to-agent routing derive from the registry.
- Remove duplicate hardcoded tool ownership/sets where safe.
- Add automatic runtime/build consistency checks.

### Later training/common-knowledge phase

The same registry metadata can feed a provider-neutral Jarvis capability/training layer. The model should learn what **Jarvis can do**, confirmation/safety rules and expected UI behavior, while internal subAgent names remain implementation details.
