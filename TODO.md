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
