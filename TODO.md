# S1Jarvis TODO

## Current company awareness

- [ ] **Resolve the active Soft1 company dynamically and remove Jetoil assumptions from Jarvis context.**
  - Source of truth for the active company id: `XSupport.ConnectionInfo.CompanyId`.
  - Load the corresponding row from Soft1 `COMPANY` using the active `COMPANY` id.
  - Build a small runtime company context from the actual `COMPANY` row (company code/name and other safe business identity fields that exist in the installation).
  - Inject that context into the Jarvis system prompt / agent context so the assistant knows which company the operator is currently working in.
  - Never hardcode `Jetoil` as the current company. Jetoil-specific business knowledge must only be applied when the active company record actually identifies Jetoil or when explicitly configured as company-specific admin context.
  - Re-resolve when the Soft1 company/session changes; do not cache one company identity for the lifetime of the DLL if the operator can switch company without restarting Soft1.
  - Fail safe: if `COMPANY` cannot be read, keep only the numeric `CompanyId` and do not guess the company name.
  - Keep the lookup synchronous on the Soft1 UI/integration thread; no `Task.Run` around `XSupport`, `GetSQLDataSet`, `XModule`, or `XTable`.
  - Add debug evidence such as `[COMPANY-CONTEXT] companyId=... name=...` without logging sensitive fields.

## AI usage aggregation stability

- [x] **Fix startup warning `[AI-USAGE-AGG] failed; Variant or safe array index out of bounds`.**
  - Root cause: repeated Soft1 positional placeholders `:1/:2/:3` inside one multi-statement SQL batch while only three arguments were supplied. The Soft1 binder treated occurrences positionally and read past the argument array.
  - Fixed by binding each Soft1 parameter exactly once into SQL variables (`@Serial`, `@UserId`, `@Before`) and using the SQL variables throughout the transaction.
  - Runtime verified 27/08/2026: `[AI-USAGE-AGG] previous-day aggregation completed` and subsequent raw `CCCJAILOG` usage insert succeeded.
  - Startup remains fail-open: reporting failure can never block Jarvis startup/provider readiness.

## Dashboard AI usage analytics

- [ ] **Runtime/UAT verify the new deterministic AI Usage Dashboard pages.**
  - `AI Usage · Σήμερα`: summary cards plus detailed breakdown by User / Agent / Provider / Model from `CCCJAILOG`.
  - `AI Usage · 30 ημέρες`: daily summary/trend using closed days from `CCCJAIDAY` plus today's raw `CCCJAILOG` rows (and any older unprocessed rows as fallback).
  - Access is enforced in the SQL/data layer:
    - Soft1 users `1` and `262` -> usage for all users of the current Soft1 serial.
    - every other Soft1 user -> only their own `CCCUSERID` rows.
  - Both views are deterministic and make no AI/provider call.
  - Verify on one admin user (`1` or `262`) and one normal user before closing this item.

## Provider-neutral UI behavior audit

- [ ] **Audit UI behavior across OpenAI / Anthropic / Google without reopening provider optimization.**
  - Same clickable file-link behavior and styling.
  - Same clarification quick-reply buttons / canonical output grammar.
  - Same tables, document links, export cards, success/error/loading states and markdown/fallback rendering.
  - Prefer canonical output normalization before rendering; do not add provider-specific UI branches unless a protocol-specific difference genuinely requires it.

## Common behavior / Soft1 knowledge / agent restructuring

- [ ] **Next optimization phase: restructure common behavior and knowledge before doing any further optimizer tuning.**
  - Layer 1: Common Jarvis Behavior.
  - Layer 2: Common Soft1 Knowledge / Training (also reusable by DR where applicable).
  - Layer 3: Agent-specific responsibilities / tool translation.
  - Layer 4: Provider adapters containing only provider/protocol-specific behavior.
  - Do not restart provider-by-provider prompt optimization before this restructuring.

## Tools Inventory

- [ ] **Create an explicit tools inventory as part of the restructuring.**
  - Tool name and owner domain/agent.
  - Read vs write/action.
  - Required parameters and confirmation requirements.
  - Result shape and UI side effects.
  - Durable-context requirements and compact modes that need the tool.
  - Common vs agent-specific classification.
  - Provider/protocol considerations.
  - Failure/fallback behavior.

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

## Architecture consolidation — single On-Premise + Cloud/Azure release

- [ ] **Consolidate the validated On-Premise and Cloud/Azure implementations into one common S1 Jarvis release.**
  - Compare `S1JarvisDynamic` and `JarvisAzureDynamic` and explicitly document every runtime/build difference before merging behavior.
  - Separate genuinely Azure-specific compatibility work from improvements that belong in the common runtime.
  - Bring the validated shared `XSupport` / AppDomain runtime-context handling into the common architecture without regressing the stable On-Premise installation.
  - Remove environment-specific paths and assumptions from product code; resolve Soft1 runtime/dependency locations safely for each supported deployment model.
  - Define one stable strategy for Soft1 Cache/Azure duplicate-assembly loading and runtime identity.
  - Keep licensing, AI routing, usage logging, WebView2 bootstrap and tool initialization behavior common across deployment models.
  - Consolidate project/build configuration so one source baseline produces the supported release artifact.
  - Add a repeatable regression checklist covering both On-Premise and Cloud/Azure before declaring any future build stable.
  - Preserve the currently validated repositories/builds until the consolidated version has passed full end-to-end validation in both environments.
  - **Definition of Done:** one common repository/source baseline, one build and one S1 Jarvis DLL can be installed and run end-to-end on both On-Premise and Cloud/Azure Soft1 without a deployment-specific fork or runtime binary.
