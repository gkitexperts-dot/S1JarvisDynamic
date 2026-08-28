# S1Jarvis TODO

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
  - Layer 4: Provider adapters containing only provider/protocol-specific behavior.
  - Do not restart provider-by-provider prompt optimization before this restructuring.

## Tools Inventory

- [ ] **Create an explicit tools inventory as part of the restructuring.**
  - [x] Central machine-readable registry created in `Core/JarvisToolRegistry.cs`.
  - [x] Initial baseline maps 30 current tools to domain, owner subAgent, allowed agents, read/write policy, confirmation requirement, UI side effect, capabilities, compact modes, durable-result behavior and fallback policy.
  - [x] Current capability -> subAgent routing map documented.
  - [x] Human-readable architecture map created in `TOOLS_INVENTORY.md`.
  - [x] Metadata validation added for duplicates, missing owners/capabilities/allowed agents, write tools without confirmation policy and orphan routing capabilities.
  - [ ] Automatically compare the registry against the real runtime tool definitions/dispatch surface and flag missing/removed/renamed tools.
  - [ ] Review the baseline mappings and resolve intentional shared-tool/Help-mode exceptions before making the registry authoritative.
  - [ ] Phase 2: make optimizer tool sets and capability routing derive from the registry; remove duplicated hardcoded ownership/routing data only after review.

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
