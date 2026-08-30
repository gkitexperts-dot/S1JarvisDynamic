# S1Jarvis CURRENT TODO — 30/08/2026

Read together with `AGENTS.md`. This is the current orchestration checkpoint and supersedes stale open checkboxes in the historical sections of `TODO.md`.

## Closed / runtime accepted

- [x] BOOT/HEALTH-only Verilic provisioning lifecycle.
- [x] Single-flight boot provisioning (`boot gate joined existing provisioning`).
- [x] All 8 logical agents load session-scoped Provider + Model + API credential; no hardcoded provider/model fallback.
- [x] Normal prompts use direct provider execution; no per-prompt Verilic routing/health/config calls.
- [x] Provider-neutral adapter boundary, including Google tool schema/tool-choice mapping, numeric enum normalization and thought-signature round-trip.
- [x] Direct-provider `ReportData -> Atlas` runtime acceptance.
- [x] Rich report table/export controls preserved.
- [x] Local dataset refinement accepted: cached dataset -> Jarvis local filter -> no Atlas/SQL re-query.

## Current final blocker / acceptance

- [ ] Controlled multi-task graph must complete without legacy-chat capability fallback.
  - Acceptance prompt contains `ReportData`, dependent `SendEmail`, independent `CreateCrmTask`, independent `CreateCalendarEvent`.
  - Jarvis owns the graph; agents never call agents.
  - Task-local arguments (recipient/title/description/date/subject/start) may be materialized by the registered owner agent from the atomic intent fragment + scoped tools/context; they are not fake cross-object dependencies.
  - Cross-object report -> email body remains a deterministic Jarvis dependency binding.
  - CRM task and personal Outlook event explicitly requested by the user execute in the same controlled run; Outlook invitations with attendees are not auto-authorized.
  - Email payload is composed from the validated report, stripped of internal execution metadata, frozen/hashed, shown to the user, and sent only after next-turn confirmation.
  - No requery/recomposition/model call is allowed after affirmative email confirmation.

## Supervisory behavior — implemented, needs runtime acceptance

- [ ] Verify `[JARVIS-SUPERVISOR]` recovery in runtime.
  - Runtime task/tool registry is authoritative about capability.
  - If an agent says it lacks tools/access while the exact request carried registered tools, Jarvis rejects that prose and retries once with the same logical agent/session target.
  - Real tool/backend/permission errors are never hidden.

## Open architecture item — deterministic semantic ambiguity before document queries

- [ ] Add a central deterministic semantic ambiguity guard before `ReportData` executes document queries.
  - Generic document terms such as "τιμολόγια" must not cause Jarvis/model code to silently choose a narrower business scope such as sales vs purchases when that choice changes the dataset.
  - The guard must use authoritative central document/business knowledge, not prompt-specific keyword branches.
  - If more than one valid business scope remains possible, Jarvis must ask one concise clarification before SQL execution/export/write actions.
  - The clarified scope becomes binding structured context for the whole graph (`ReportData` -> `ExportData` -> CRM/email/calendar descriptions as applicable).
  - FPRMS remains the authoritative row-level document-type discriminator; this TODO concerns pre-query business-scope ambiguity, not FPRMS classification.
  - Acceptance: a generic invoice request must either resolve to one authoritative scope from explicit context or ask clarification; it must never silently invent "πωλήσεων", "αγορών" or another narrowing assumption.

## Current implementation commits

- `26f4c7e22a51bfeb1b3850be8d7ae880cc83ddec` — owner-agent task-local prerequisite state.
- `40170ae67313f05ea52b1c3f356270aad4ab7aba` — supervisory false-capability-denial recovery.
- `56e7a2d49177d9b83335042d7453a854bb72aec6` — whole-plan validation accepts owner-agent execution work.
- `998573d1601c2bb3b16e61a423abba39411e836f` — coordinator materializes deterministic dependencies and delegates scoped task-local inputs.
- `d180cd30d13ca144df4ea3eae02394421f06d5f3` — persistent supervisory invariants in `AGENTS.md`.
- `f2732ee96e530aafe0bd4aa15f553045e14fc3df` — controlled Echo CRM/calendar executor.
- `fdb44c848492fb3eeeeed009d1ca05c27762c940` — promoted multi-task controlled graph harness.
- `ab15c058730987b47c1b799e18c6a39e3e02f068` — internal context excluded from frozen action payload/hash.
- `fd2e7b29274c9c242055bd55b57d074472fdc0dd` — no model placeholder in controlled executor; session registry remains authoritative.
- `85e4f3085af7f21e0354d83e25741946f00041ea` — policy guard aligned with UserControl `Unloaded` shutdown lifecycle.

## Next test

1. Pull `feature/jarvis-orchestration`.
2. Build.
3. Run the same FINIX + email + Soft1 CRM + Outlook Calendar compound prompt.
4. Capture the full response and log through the email draft/frozen confirmation state.
5. If draft is correct, confirm the email and capture the continuation log through `echo_result_accepted`.

## Presentation polish after functional acceptance

- [ ] Improve report column labels/spacing, ISO date formatting and numeric presentation without mutating validated data or removing Excel/CSV/PDF controls.
