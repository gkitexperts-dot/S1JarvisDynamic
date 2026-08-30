# S1Jarvis TODO

## CRITICAL CHECKPOINT — Jarvis AI session provisioning / orchestration recovery — 30/08/2026

This section is the authoritative checkpoint to recall before continuing Jarvis orchestration work. Do not open new optimization work until the critical sequence below is closed.

### Target architecture / hard invariants

- [ ] **Jarvis ↔ Verilic relationship is limited to startup provisioning and explicit HEALTH refresh.**
  - Jarvis startup first performs the normal licence / installation / identity checks.
  - During boot Jarvis downloads the complete effective AI configuration for all agents: `Agent + Provider + Model + API Key + inherited/dedicated routing metadata`.
  - Required agents are `Jarvis`, `Atlas`, `Forge`, `Compass`, `Echo`, `Sprint`, `Scout`, `Sage`.
  - The complete configuration is stored only in the Jarvis process/session memory.
  - Normal prompts must not call Verilic for licence, routing, provider, model or API key resolution.
  - Each agent executes directly against its assigned AI provider using the provider/model/key already loaded in the session registry.
  - On Jarvis shutdown all credential buffers are cleared/zeroed from memory.
  - Explicit `HEALTH` is the only runtime exception: it may contact Verilic, refresh licence/configuration/credentials and atomically replace the session registry only when the complete refresh is valid. A failed HEALTH refresh must preserve the previous working registry.
  - No hardcoded concrete provider/model fallback is allowed in orchestration/business code.

### Immediate blockers observed on 30/08/2026

- [ ] **Remove duplicate boot provisioning trigger.**
  - Runtime log currently proves two provisioning starts in one startup: `[AI-SESSION-REGISTRY] boot provisioning start` followed later by `[AI-SESSION-REGISTRY] boot provisioning start (boot gate)`.
  - Provisioning must have one authoritative startup trigger and be single-flight/idempotent.
  - Keep explicit lifecycle logs for provisioning start/success/failure; never return silently from this path again.

- [ ] **Fix the real `provider_model_missing` source in NexusDynamic instead of masking it in the desktop client.**
  - Current authoritative backend repository/branch: `gkitexperts-dot/NexusDynamic` / `feature/multi-ai-configuration`.
  - Current runtime result: both duplicate `/health` calls fail with `provider_model_missing`.
  - `JarvisAiRoutingController.HealthAsync()` calls `BuildEffectiveTargetsAsync(routing)`. If no saved multi-agent target is resolved, it falls back to legacy `routing.AgentAccountRef/routing.Model`; the compatibility routing path intentionally does not load a model, therefore the fallback returns `provider_model_missing`.
  - Diagnose why the saved `JarvisAiConfiguration` is not resolving for the current `ContractId + CustomerId + Soft1Serial + CompanyCode + BranchCode + Soft1UserId`, or why the resolved `DefaultTarget.Model` is empty.
  - Do not solve this by hardcoding a model or reintroducing normal-prompt Verilic routing.
  - Once fixed, `/health` must return all 8 effective targets with non-empty `AgentAccountRef`, `Provider`, `Model` and `ApiKey` for ready targets.

### Regression tests required before declaring provisioning closed

- [ ] Add/complete automated tests covering the provisioning contract.
  - Successful target serializes non-empty `AgentAccountRef + Provider + Model + ApiKey`.
  - Failed target never exposes an API key.
  - Dedicated agent target returns its dedicated account/model/key.
  - Inherited helper target returns the default/inherited account/model/key.
  - Missing model fails explicitly and never receives a hardcoded fallback.
  - Saved configuration is not destructively removed because of stale provider discovery metadata before the real provider probe runs.
  - Desktop startup provisioning is single-flight: one startup -> one provisioning request.
  - Failed HEALTH refresh leaves the previous valid in-memory registry untouched.
  - Shutdown clears credential buffers.

### Runtime acceptance sequence after the blockers are fixed

- [ ] **Boot acceptance.**
  - Publish/build the required NexusDynamic change, then build/restart S1JarvisDynamic.
  - Startup must contain exactly one provisioning start and a successful `[AI-SESSION-REGISTRY] loaded ... /Credential=Loaded` snapshot for all 8 agents.
  - Startup must not depend on a pre-resolved provider/model from the legacy compatibility routing path.

- [ ] **Direct-provider runtime acceptance.**
  - Execute a normal Main Chat prompt after successful boot.
  - Verify the selected agent uses its session-scoped `Provider + Model + API Key` and calls the provider directly.
  - Verify no normal prompt calls Verilic for licence/routing/provider/model/key.
  - `HEALTH` remains the only runtime refresh path.

### Two unfinished orchestration steps that must be closed next

- [ ] **Presentation/action/email flow.**
  - Target flow: `latest document -> validated data -> natural Jarvis draft -> freeze exact payload/hash -> explicit confirmation -> Echo send exact frozen payload`.
  - No requery/recompute after confirmation.
  - No AI call after the affirmative confirmation itself.
  - Echo must receive exactly the confirmation-bound payload.
  - No automatic retry after an irreversible send.
  - Presentation may phrase/format validated facts but must never mutate the underlying data.
  - Rich Main Chat behavior (Markdown tables, links, export controls, charts where applicable) must be preserved.

- [ ] **Cached dataset local refinement.**
  - Target flow: `Atlas validated dataset -> Jarvis session dataset cache -> follow-up filter/sort/project/limit locally`.
  - Example acceptance: `δείξε μου τα τελευταία 20 παραστατικά` then `από αυτά κράτα μόνο όσα έχουν ποσό πάνω από 1000`.
  - Successful refinement must not execute new Atlas SQL.
  - Expected marker: `[ORCH-DATASET] local refinement applied; rows=<n>`.
  - Refined results remain available for chained follow-ups.

### Work that follows only after the two unfinished steps above are clean

- [ ] Expand the registry-driven orchestration beyond the controlled `ReportData -> SendEmail` pilot.
  - Preserve invariant: agents never orchestrate or call each other. Every task result returns to Jarvis; Jarvis validates and decides/dispatches the next task.
  - Expand/test remaining owners/tasks: Forge `CreateItem`; Compass `FindTrader/CreateTrader`; Echo inbox/calendar/CRM/reply flows; Sprint courier flows; Scout internet research/order; Sage help; Atlas export/open/conversion.
  - Inspect each real executor/tool contract before enabling it.

### Summary of work completed to reach this checkpoint

- Established Tool Registry / Task Registry architecture: `Task -> Capability -> Owner Agent -> Allowed Tools`, including reconciliation/audit and atomic task cleanup.
- Implemented controlled Main Chat orchestration pilot and feature-gated routing rather than immediately replacing the mature legacy path.
- Implemented semantic validation for Atlas read-only reporting, including deterministic latest-document ordering and SERIES join semantics after runtime tests exposed incorrect "latest" behavior.
- Implemented `JarvisPresentationComposer` so execution/data validation stays deterministic while Jarvis controls natural presentation separately.
- Implemented `JarvisDatasetSession` for validated dataset caching and deterministic local refinement without unnecessary SQL re-execution.
- Implemented confirmation-bound action handling so a presented email/action payload can be frozen and sent only after explicit confirmation.
- Moved toward provider-neutral routing: orchestration/business code must not hardcode concrete models/providers.
- Changed the intended AI lifecycle so boot/HEALTH provision all agent AI settings from Verilic and normal prompts execute directly against providers.
- Implemented session-scoped agent runtime targets carrying `Provider + Model + API Key`, credential clearing on shutdown, and direct provider transport adapters for Google/OpenAI/Anthropic.
- Updated NexusDynamic `/health` contract to return per-agent provisioning information including API key for ready targets, with `Cache-Control: no-store` behavior and no key logging.
- Added/updated policy checks documenting that normal AI execution must not call Verilic and that provisioning is limited to boot/HEALTH.

### Problems/regressions encountered on the way here

- Earlier controlled orchestration tests were semantically successful but silently fell back to the mature legacy path because the controlled AI route reported `provider_model_missing`; therefore the two final runtime behaviors could not be considered closed.
- The initial session registry validation used a lambda over an `out` parameter and failed under the project's C# 7.3 compiler (`CS1628`); replaced with a compatible loop.
- Shutdown cleanup initially used a `Window.Closed` pattern on `JarvisShell`, which is a `UserControl`, causing compile errors; changed to `Unloaded` cleanup.
- An earlier startup snapshot only carried provider/model metadata and did not prove credentials were loaded. The required acceptance marker was changed to `[AI-SESSION-REGISTRY] ... /Credential=Loaded`.
- Boot provisioning was initially made dependent on legacy `_agentAccountRef`; when that value was unavailable the code waited and then returned silently, producing startup logs with no provisioning evidence.
- Additional logging exposed that the provider-health provisioning handler itself was not wired into the actual startup path. A boot-gate wiring change then started provisioning, but introduced/retained two startup triggers.
- The current log finally gives deterministic evidence: provisioning is now reached, it is invoked twice, and both requests fail with `provider_model_missing`.
- Backend inspection shows the current failure path is the legacy fallback inside `JarvisAiRoutingController.HealthAsync()` after `BuildEffectiveTargetsAsync()` returns no effective saved targets. Because compatibility `/resolve` intentionally no longer loads AI model configuration, its `routing.Model` is empty and that fallback cannot provision the session.
- A previous backend attempt also revealed that provider discovery/catalog validation must not destructively null a persisted target before the actual provider health probe; saved configuration is the provisioning source of truth, while the live probe determines whether the provider/model/key works.

### Current repositories / branches at this checkpoint

- Desktop: `gkitexperts-dot/S1JarvisDynamic` / `feature/jarvis-orchestration`.
- Backend: `gkitexperts-dot/NexusDynamic` / `feature/multi-ai-configuration`.
- Desktop runtime log at the checkpoint proves shutdown credential cleanup is firing (`[AI-SESSION-REGISTRY] cleared on Jarvis shutdown`) and boot reaches provisioning, but provisioning is not yet accepted because of duplicate invocation + `provider_model_missing`.

## Current company awareness

- [x] **Resolve the active Soft1 company dynamically and remove Jetoil assumptions from Jarvis context.**
  - Source of truth is `XSupport.ConnectionInfo.CompanyId` and the active `COMPANY` row.
  - Runtime company identity/context is injected dynamically; Jetoil is not assumed as the current company.
  - Company-specific Jarvis Wise context and admin-controlled context maintenance are implemented and runtime/UAT verified 28/08/2026.
  - Fail-safe behavior keeps the runtime from guessing company identity when Soft1 company data cannot be read.

## AI usage aggregation stability

- [x] **Fix startup warning `[AI-USAGE-AGG] failed; Variant or safe array index out of bounds`.**
  - Root cause: repeated Soft1 positional placeholders `:1/:2/:3` inside one multi-statement SQL batch while only three arguments were supplied. The Soft1 binder treated occurrences positionally and read past the argument array.
  - Fixed by binding each Soft1 parameter exactly once into SQL variables (`@Serial`, `@UserId`, `@Before`) and using the SQL variables throughout the transaction.
  - Runtime verified 27/08/2026: `[AI-USAGE-AGG] previous-day aggregation completed` and subsequent raw `CCCJAILOG` usage insert succeeded.
  - Startup remains fail-open: reporting failure can never block Jarvis startup/provider readiness.

## Jarvis Activity Layer

- [x] **Generic transient activity/status indicator across Jarvis chat surfaces.**
  - Runtime/UAT completed 28/08/2026.
  - Main Chat background Internet research: PASS, including hidden-browser research, evolving activity captions, suppression of premature primary-chat/cancellation replies and final-answer completion.
  - Browser curtain: PASS.
  - Help curtain: PASS.
  - Email curtain: PASS; activity captions plus real inbox read/classification flow verified. Inbox-analysis results are reflected in the Email list instead of dumping the detailed classification into chat.
  - Courier curtain: PASS.
  - Document Reader intentionally excluded because its interaction model is object/workflow based and does not expose an equivalent curtain chat box.
  - Activity state is transient UI state and is not persisted as normal assistant transcript/history or Jarvis Wise knowledge.

## Dashboard AI usage analytics

- [x] **Runtime/UAT verify the new deterministic AI Usage Dashboard pages.**
  - `AI Usage · Σήμερα`: summary cards plus detailed breakdown by User / Agent / Provider / Model from `CCCJAILOG`.
  - `AI Usage · 30 ημέρες`: daily summary/trend using closed days from `CCCJAIDAY` plus today's raw `CCCJAILOG` rows (and any older unprocessed rows as fallback).
  - Access is enforced in the SQL/data layer:
    - Soft1 users `1` and `262` -> usage for all users of the current Soft1 serial.
    - every other Soft1 user -> only their own `CCCUSERID` rows.
  - Both views are deterministic and make no AI/provider call.
  - Implemented and accepted in runtime/UAT before 28/08/2026.

## Provider-neutral UI behavior audit

- [ ] **Audit UI behavior across OpenAI / Anthropic / Google without reopening provider optimization.**
  - Same clickable file-link behavior and styling.
  - Same clarification quick-reply buttons / canonical output grammar.
  - Same tables, document links, export cards, success/error/loading states and markdown/fallback rendering.
  - Prefer canonical output normalization before rendering; do not add provider-specific UI branches unless a protocol-specific difference genuinely requires it.

## Common behavior / Soft1 knowledge / agent restructuring

- [ ] **Next optimization phase: restructure common behavior and knowledge before doing any further optimizer tuning.**
  - Layer 1: Common Jarvis Behavior.
    - [x] Product identity invariant: user always interacts with Jarvis; internal agent names are implementation details and must not leak or be invented.
  - Layer 2: Common Soft1 Knowledge / Training (also reusable by DR where applicable).
  - Layer 3: Agent-specific responsibilities / tool translation.
    - Use the completed `JarvisToolRegistry` as the source architecture map when moving optimizer tool sets and capability routing away from duplicated hardcoded sets.
  - Layer 4: Provider adapters containing only provider/protocol-specific behavior.
  - Do not restart provider-by-provider prompt optimization before this restructuring.

### Orchestration / routing checkpoint — 28/08/2026

- [x] Tool Registry / Task Registry architecture established: `Task -> Capability -> Owner Agent -> Allowed Tools`.
- [x] Cross-registry reconciliation audit added (`JarvisTaskRegistryAudit`).
- [x] Granular capability owner resolution added so capabilities such as `Export`, `EmailWrite`, `CalendarWrite`, `CourierRead` and `CourierWrite` resolve from the real Tool Registry instead of requiring a second hardcoded routing table.
- [x] Initial atomic-task cleanup started:
  - `ManageCalendar` split into `ReadCalendar` and `CreateCalendarEvent`.
  - email write flow split into `SendEmail` and `ReplyEmail`.
- [x] Semantic planner is gated by registry consistency: it must not build a planner catalog when reconciliation finds invalid metadata.
- [x] Task-registry reconciliation is wired into the existing non-blocking startup audit/logging path.
- [ ] **Calendar / Teams capability extensions before resuming routing work.**
  - Extend `create_outlook_event` with Teams meeting support (`isOnlineMeeting=true`, `onlineMeetingProvider=teamsForBusiness`) and return the generated Teams join URL when available.
  - Add a new calendar-response tool/task for meeting invitations so Jarvis can `accept`, `tentative` or `decline` a resolved Outlook/Teams invitation after explicit user confirmation.
  - Keep both capabilities under the existing Echo / CalendarWrite ownership model; do not create a separate Teams agent.
- [ ] **Resume routing optimization after the Calendar / Teams extensions above.**
  - Audit `RequiredInputs`, `OptionalInputs` and `Produces` for every atomic task against the real tool schemas.
  - Normalize any remaining task/tool capability mismatches.
  - Verify startup log reaches clean Task Registry reconciliation with zero blocking mismatches.
  - Connect `JarvisSemanticPlanning` to one isolated provider planner call: prompt -> strict JSON plan -> validation -> `JarvisPlan`, with no live tool execution yet.
  - Test single-task, dependent multi-task, parallel multi-task, missing-input and hallucinated-task rejection cases.
  - Only after isolated planner UAT, integrate registry-driven orchestration into Main Chat and retire duplicated legacy routing gradually.

## Tools Inventory

- [x] **Create an explicit tools and parameters inventory as part of the restructuring.**
  - Central machine-readable tool registry created in `Core/JarvisToolRegistry.cs`.
  - Baseline maps 30 current tools to domain, owner subAgent, allowed agents, read/write policy, confirmation requirement, UI side effect, capabilities, compact modes, durable-result behavior and fallback policy.
  - Current capability -> subAgent routing map documented.
  - Human-readable architecture map and comprehensive parameters inventory maintained in `TOOLS_INVENTORY.md`.
  - Parameters audit covers active/configurable `cccParams`, including security-sensitive configuration and intentional inactive historical roadmap codes.
  - Metadata validation covers duplicates, missing owners/capabilities/allowed agents, write tools without confirmation policy and orphan routing capabilities.
  - Runtime reconciliation implemented in `Core/JarvisToolInventoryReconciler.cs`: it automatically discovers real static `*ToolDefinition` objects from JarvisTools / Email / Courier / Items and compares them bidirectionally with the registry.
  - Reconciliation is executed on the existing non-blocking startup audit path and logs `[TOOL-INVENTORY] reconciliation OK ...` or exact missing/removed/renamed/duplicate diagnostics without affecting Soft1 startup.
  - The current execution/definition surface and registry both contain the same 30-tool baseline. `Help` is intentionally a routing-only capability that reuses shared read tools and is treated as a documented warning rather than an orphan tool.
  - Future rule: every new tool must be registered in `JarvisToolRegistry`; runtime reconciliation will flag any definition added/removed/renamed without the matching registry update.
  - Optimizer/routing deriving directly from the registry is **not** part of Inventory closure; it belongs to the Common behavior / agent restructuring phase above.

## Soft1 threading rule

- [ ] Audit remaining Soft1 SDK calls and keep every `XSupport` / `XModule` / `XTable` call synchronous on the Soft1 integration/UI thread. Do not move Soft1 SDK work to `Task.Run` or thread-pool workers.

## Soft1 UI isolation rule

- [ ] **Minimize Jarvis dependence on Soft1 UI/native modal behavior.** Soft1 should be treated primarily as the host/container and business integration surface, not as the owner of Jarvis UI behavior.
  - Soft1 responsibilities should be limited, where practical, to:
    - providing the host panel/container in which Jarvis and Verilic Licensing screens are mounted;
    - exposing the Soft1 object/integration commands required for ERP operations;
    - providing `XSupport`/session context and access to Soft1 data/business objects.
  - Jarvis UI, navigation, dialogs, file selection, confirmations, progress, errors and auxiliary screens should be owned by Jarvis whenever technically possible.
  - Avoid native modal dialogs opened from WebView2 callbacks or other re-entrant Soft1 UI paths. Previous `ShowDialog()` experiments already produced native `EExternalException`/host instability.
  - Avoid HTML/WebView2 `<input type="file">` for DR file selection inside the Soft1-hosted WebView. Replace it with a Jarvis-controlled file-selection flow so WebView2 does not invoke its own native file picker inside the Delphi/Soft1 host.
  - File selection must return only the selected paths/metadata into the DR workflow; subsequent file reading/parsing remains Jarvis-owned.
  - Add debug checkpoints around file-selection lifecycle: request, picker-open, picker-return/cancel, selected paths accepted, file-read start/end.
  - Any UI-side failure must remain contained inside Jarvis and must never propagate as an unhandled exception to the Soft1 host process.

## Architecture consolidation — common Soft1 hook for On-Premise + Cloud/Azure

- [x] **Consolidate the Soft1 bootstrap/hook strategy used by On-Premise and Cloud/Azure.**
  - Scope agreed 28/08/2026: the critical consolidation point is the Soft1 hook/bootstrap path, not a broad rewrite of the already-aligned runtime.
  - `JarvisRuntimeLoader` and `JarvisObject` were already identical between `S1JarvisDynamic` and `JarvisAzureDynamic`.
  - The only material hook difference was `XSupport` handoff: plain assembly-static storage On-Premise vs static + shared AppDomain fallback in Azure.
  - The Azure-safe pattern is applicable to On-Premise with no behavioral downside and is now the common implementation.
  - Common key is deployment-neutral: `S1Jarvis.Shared.XSupport`.
  - `S1Init.Initialize()` now calls `JarvisCore.SetXSupport(XSupport)` and `JarvisHostForm` resolves through `JarvisCore.GetXSupport()`.
  - Single-load On-Premise continues to use the fast static value; duplicate assembly loads can recover the same `XSupport` through AppDomain shared data.
  - No Azure-specific filesystem path or deployment assumption is introduced into the common hook.
  - Final smoke test after pull/build: open Jarvis in the On-Premise Soft1 installation and confirm normal startup and core interaction.
