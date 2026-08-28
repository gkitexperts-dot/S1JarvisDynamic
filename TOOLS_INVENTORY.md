# Jarvis Tools Inventory

Status: Phase 1 inventory baseline — 28/08/2026

`Core/JarvisToolRegistry.cs` is the machine-readable tool inventory source of truth. This document is the human-readable architecture map for **tools, routing and runtime parameters**. During Phase 1 the registry is descriptive only: existing routing/optimizer behavior is not changed yet.

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

# Runtime Parameters Inventory (`cccParams`)

Audit baseline: **28/08/2026**. This section is intended to become the canonical human-readable parameter inventory alongside the tool inventory.

Audit sources include the active Core/UI code paths, `PARAMS.md`, `PARAMS_RUNTIME_AUDIT_2026-08-26.md` and the post-audit Jarvis Admin implementation. A parameter is considered **active** only when current runtime code reads it or a current runtime component consumes the configured slot.

## Parameter contract

- `ParamCode` is unique.
- Normal parameters use **one value column only**: numeric `ParamValue` or text `ParamValueString`.
- Exception: Dashboard slots `500040`–`500059` deliberately use **both** columns: `ParamValue` for chart type and `ParamValueString` for SQL.
- Missing optional configuration must fail open to a documented default/fallback.
- Feature-required configuration must fail only the affected feature, not Jarvis/Soft1 startup.
- Secrets and behavior-controlling values must never be emitted in normal logs or user-visible diagnostics.
- New runtime parameters must be added to this inventory in the same change that introduces the code path.

## Active parameter list

| ParamCode | Name / purpose | Column | Default / fallback | Requirement | Subsystem / tool impact | Audit classification |
|---:|---|---|---|---|---|---|
| `500000` | Debug file logging | `ParamValue` | off | Optional | `DebugLog`; shared Jarvis/Courier/DR diagnostics | Active / common |
| `500002` | Courier dynamic receiver mapping | `ParamValueString` | Hardcoded FINDOC/MTRDOC/TRDR mapping | Optional | Courier receiver resolution | Active / Courier |
| `500008` | Knowledge Base / Q&A log SERIES | `ParamValue` | none | Feature-required | Q&A/knowledge logging, `create_crm_task` related SOACTION flow | Active / feature-required |
| `500009` | Report decimal places | `ParamValue` | `2` | Optional | Common report/table presentation in Jarvis prompt | Active / common behavior |
| `500011` | Direct export max rows | `ParamValue` | `5000`; `0` = unlimited | Optional | `export_query_to_file` | Active / Atlas |
| `500012` | CRM Tasks SERIES | `ParamValue` | none | Feature-required | `create_crm_task` | Active / Echo/CRM |
| `500013` | CRM task default ACTSTATES | `ParamValue` | `1001` | Optional | `create_crm_task` defaults | Active / Echo/CRM |
| `500014` | CRM task default ACTSTATUS | `ParamValue` | `1` | Optional | `create_crm_task` defaults | Active / Echo/CRM |
| `500015` | Dashboard Tasks auto-refresh minutes | `ParamValue` | `5` | Optional | Tasks dashboard refresh | Active / UI |
| `500016` | Order-entry confidence threshold | `ParamValue` | `85` | Optional | AI-assisted order creation from email/content | Active / OrderWrite |
| `500017` | Order prompt-log SERIES | `ParamValue` | none | Feature-required | `create_order` audit/prompt logging | Active / OrderWrite |
| `500018` | Native Soft1 form override by SOSOURCE | `ParamValueString` | default native behavior | Optional | `open_document`, `create_order`, Soft1 navigation | Active / shared navigation |
| `500019` | Email OAuth Client ID | `ParamValueString` | none | Feature-required | Outlook/Graph Email feature | Active / sensitive config |
| `500020` | Email OAuth Tenant ID | `ParamValueString` | none | Feature-required | Outlook/Graph Email feature | Active / sensitive config |
| `500021` | Email OAuth Client Secret | `ParamValueString` | none | Feature-required | Outlook/Graph Email feature | **Active / secret** |
| `500022` | Inbox maximum emails | `ParamValue` | `100` | Optional | `filter_email_inbox`, inbox loading | Active / Echo |
| `500023` | Calendar maximum events | `ParamValue` | `100` | Optional | calendar loading/filtering | Active / Echo |
| `500024` | Dashboard Tasks max rows | `ParamValue` | `100` | Optional | Tasks dashboard | Active / UI |
| `500025` | Browser `read_page_content` max characters | `ParamValue` | `40000` | Optional | `read_page_content` context cap | Active / Scout |
| `500026` | Item-copy field whitelist | `ParamValueString` | hardcoded whitelist | Optional | `get_item_template`, `create_item`; server-side whitelist | Active / Forge / security-relevant |
| `500027` | Additional administrator instructions | `ParamValueString` | empty / omitted | Optional | Common Jarvis system instructions across modes | **Active / behavior-sensitive** |
| `500028` | Bulk-import max iterations | `ParamValue` | `40` for bulk flow | Optional | Main/Browser long tool loops | Active / execution budget |
| `500029` | Atlas model request override | `ParamValueString` | `claude-opus-5` client fallback | Optional | Client request construction for Atlas | Active compatibility input; **not authoritative routing** |
| `500030` | Forge model request override | `ParamValueString` | `claude-opus-5` client fallback | Optional | Client request construction for Forge | Active compatibility input; **not authoritative routing** |
| `500031` | Compass model request override | `ParamValueString` | `claude-opus-5` client fallback | Optional | Client request construction for Compass | Active compatibility input; **not authoritative routing** |
| `500032` | Echo model request override | `ParamValueString` | `claude-opus-5` client fallback | Optional | Client request construction for Echo | Active compatibility input; **not authoritative routing** |
| `500033` | Sprint model request override | `ParamValueString` | `claude-opus-5` client fallback | Optional | Client request construction for Sprint | Active compatibility input; **not authoritative routing** |
| `500034` | Scout model request override | `ParamValueString` | `claude-opus-5` client fallback | Optional | Client request construction for Scout | Active compatibility input; **not authoritative routing** |
| `500035` | Sage model request override | `ParamValueString` | `claude-opus-5` client fallback | Optional | Client request construction for Sage | Active compatibility input; **not authoritative routing** |
| `500036` | Jarvis Admin Soft1 user IDs | `ParamValueString` | empty = no admins | Required only for admin features | Jarvis Wise COMPANY context administration and future admin-only operations | **Active / authorization / fail-closed** |
| `500040` | Commercial Dashboard slot 01 — Top customers/day turnover | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active configurable slot |
| `500041` | Commercial Dashboard slot 02 — Top items/day quantity | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active configurable slot |
| `500042` | Commercial Dashboard slot 03 — Top items/day turnover | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active configurable slot |
| `500043` | Commercial Dashboard slot 04 — Current item prices | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active configurable slot |
| `500044` | Commercial Dashboard slot 05 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500045` | Commercial Dashboard slot 06 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500046` | Commercial Dashboard slot 07 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500047` | Commercial Dashboard slot 08 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500048` | Commercial Dashboard slot 09 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500049` | Commercial Dashboard slot 10 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500050` | Commercial Dashboard slot 11 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500051` | Commercial Dashboard slot 12 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500052` | Commercial Dashboard slot 13 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500053` | Commercial Dashboard slot 14 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500054` | Commercial Dashboard slot 15 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500055` | Commercial Dashboard slot 16 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500056` | Commercial Dashboard slot 17 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500057` | Commercial Dashboard slot 18 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500058` | Commercial Dashboard slot 19 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |
| `500059` | Commercial Dashboard slot 20 | `ParamValue` + `ParamValueString` | empty SQL = disabled | Optional | Commercial dashboard | Active free slot |

**Active runtime/configurable slots represented: 50 ParamCodes.**

## Dashboard slot value contract (`500040`–`500059`)

For every dashboard slot:

- `ParamValue = 1` → bar
- `ParamValue = 2` → line
- `ParamValue = 3` → pie
- `ParamValue = 4` → doughnut
- anything else / null → bar fallback
- `ParamValueString` → one read-only dashboard SQL query
- optional `:1` placeholder → selected dashboard date
- empty `ParamValueString` → slot disabled, no error
- query failure/zero rows → only that panel is skipped; the dashboard remains available

## Security / sensitivity classification

| Parameter | Risk | Required handling |
|---|---|---|
| `500021` Client Secret | Credential | Never log/display value; treat as secret material. |
| `500027` Admin instructions | Behavior / prompt-control | Admin-controlled only; supplementary instructions must never override safety/confirmation invariants. |
| `500026` Item copy whitelist | Write-surface security | Treat as server-side allowlist, not merely model guidance. |
| `500036` Admin IDs | Authorization | Fail closed. Missing/unreadable value grants no admin rights. |
| `500002` Courier mapping SQL | Configurable SQL | Must remain constrained to the intended receiver-resolution read path. |
| `500040`–`500059` Dashboard SQL | Configurable SQL | Read-only dashboard queries only; a bad panel must remain isolated. |

## Provider/model parameter audit (`500029`–`500035`)

These seven parameters are still read by `JarvisAgentClient.ResolveAgentModel()` and can alter the `model` field placed in the client-side provider request. They are therefore **not dead code**.

However, the current Verilic architecture keeps provider credentials and final routing/model authority server-side. These parameters must therefore be treated as **legacy/compatibility request preferences**, not the architectural source of truth for which provider/model ultimately executes a turn.

Future cleanup should decide one of two explicit outcomes:

1. remove these parameters once the server-owned routing contract fully supersedes them; or
2. formally support them as a validated client preference that the server may accept/reject.

Until that decision, do not build new routing logic on top of `500029`–`500035`.

## Jarvis Admin parameter (`500036`) — audit correction

`500036` is active and was introduced after the older parameter documentation/audit. Current runtime contract:

- column: `ParamValueString`
- format: comma-separated positive Soft1 `UserId` values, e.g. `1,262`
- whitespace is ignored
- missing/empty/unreadable parameter → empty admin set
- authorization is fail-closed
- currently gates Jarvis Wise company-context administration

This was the main documentation gap discovered by the 28/08/2026 deep audit.

## Non-active / roadmap parameter codes

The following old five-digit codes appear in historical roadmap documentation but **are not read by the current Jarvis runtime and must not be treated as active configuration**:

| ParamCode | Historical planned purpose | Current status |
|---:|---|---|
| `50003` | AI email-order SERIES | Not implemented / inactive |
| `50004` | AI eShop retail SERIES | Not implemented / inactive |
| `50005` | AI eShop invoice SERIES | Not implemented / inactive |
| `50006` | Auto customer email toggle for email-orders | Not implemented / inactive |
| `50007` | Auto customer email toggle for eShop | Not implemented / inactive |

Do not reuse those historical entries as evidence of runtime support. New Jarvis parameters follow the six-digit `500XXX` convention.

## Known free/gap codes

The current active inventory intentionally contains gaps (`500001`, `500003`–`500007`, `500010`, `500037`–`500039`, and codes above `500059`). A gap is **not** evidence that a parameter exists. Before allocating any new code, audit current source and this inventory.

## DR parameter audit

There is currently **no mandatory DR-specific `cccParams` code** in the active Document Reader workflows. DR shares `500000` for debug logging. `500026` is conceptually related to item-copy/carry-over behavior but has its own fallback and is not a DR startup requirement.

Missing parameters must therefore never be used as a generic explanation for a DR process failure without evidence from logs/runtime inspection.

## Parameter validation policy

Current `JarvisParameterAudit` checks the core feature-required numeric codes `500008`, `500012`, `500017`, and inspects Email credentials `500019`–`500021` without making them Jarvis boot blockers.

Inventory hardening should eventually expand validation metadata so that parameter validation, like tool validation, is data-driven rather than duplicated in ad-hoc arrays. In particular:

- validate `500036` format as comma-separated positive user IDs;
- validate numeric ranges/defaults centrally;
- validate Dashboard chart types and SELECT-only SQL contract;
- classify secrets/behavior-sensitive values centrally;
- flag runtime-read ParamCodes missing from this inventory;
- flag inventory ParamCodes no longer read by runtime;
- preserve feature-scoped fail-open/fail-closed semantics.

## New-parameter rule

A new `cccParams` parameter is not architecturally complete until the same change defines:

1. unique `ParamCode`;
2. value column (`ParamValue`, `ParamValueString`, or documented dual-column exception);
3. subsystem/tool/feature owner;
4. required vs optional vs feature-required status;
5. default/fallback behavior;
6. validation/range/format;
7. sensitivity classification;
8. failure semantics (fail-open, feature-fail, authorization fail-closed, etc.);
9. runtime read location;
10. entry in this Parameters Inventory.

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
- Current `cccParams` inventory audited and documented.
- No routing behavior changed.

### Phase 2 — after inventory review

- Make optimizer tool sets derive from the registry.
- Make capability-to-agent routing derive from the registry.
- Remove duplicate hardcoded tool ownership/sets where safe.
- Add automatic runtime/build consistency checks.
- Consider moving parameter metadata into a machine-readable `JarvisParameterRegistry` using the same architecture pattern.

### Later training/common-knowledge phase

The same registry metadata can feed a provider-neutral Jarvis capability/training layer. The model should learn what **Jarvis can do**, confirmation/safety rules and expected UI behavior, while internal subAgent names remain implementation details.
