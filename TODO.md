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
