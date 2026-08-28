# Jarvis AI Tool / Context Audit — 2026-08-26

## Runtime evidence that triggered the audit

Observed with the OpenAI provider after the first provider-neutral context optimization:

- Casual greeting: ~11.8k input tokens before optimization -> 205 input tokens after conversational fast-path.
- Read-only top-customer report: ~5k input tokens, acceptable for a real Soft1 query/tool turn.
- Email/action flows: input grew to ~37k-58k tokens over follow-up turns.
- Report -> export -> email workflow: input reached ~151k tokens before confirmation and ~65k on the confirmation/send turn.

The email numbers show that tool schemas are only part of the cost. Completed internal tool traces were being retained in conversation history and resent on later human turns.

## Root causes found

1. Main Jarvis historically carried a broad tool catalog.
2. Dedicated role tool sets were smaller, but main-chat action workflows often fell back to the full catalog.
3. `JarvisAgentClient` correctly stores each actual tool result as a `tool_result` block so the active multi-turn tool loop can continue.
4. Those same completed `tool_use` / `tool_result` blocks remained in the persistent conversation and were resent when the user started a later turn.
5. A short answer such as `ναι` is semantically ambiguous: it can be harmless conversation OR confirmation of a pending send/write action. Therefore it must never enter the zero-tool conversational fast-path solely because it is short.

## Optimization protocol now implemented

### 1. Current-turn integrity

The latest real human message is the anchor. Every message/block from that anchor forward is preserved exactly. This guarantees that an active tool loop keeps its `tool_use` / `tool_result` pairing and provider state.

### 2. Completed-turn trace compaction

Before a new provider call, completed internal blocks that belong to older human turns may be removed:

- `tool_use`
- `tool_result`
- `thinking`
- `redacted_thinking`

Visible user/assistant text is retained. Therefore a draft email remains available to a later `ναι, στείλτο`, while the previous SQL rows/contact-search/tool payloads do not need to be paid for again.

### 3. Confirmation safety

Ambiguous acknowledgement words (`ναι`, `οκ`, `ωραία`, etc.) were removed from the conversational fast-path.

For short confirmations, the optimizer inspects the immediately preceding visible assistant response. When that response clearly contains a pending email draft/send or courier action, the confirmation is routed to the corresponding compact tool set rather than to zero tools or to the full catalog.

### 4. Main-chat domain union

Main Jarvis can now compact up to two explicit domains by unioning only the required tool sets. Common examples:

- customer + email
- Soft1 report + export + email
- browser + email

More complex/uncertain requests still fail open to the mature full contract.

### 5. Echo/report-email capability

The Echo set now includes `export_query_to_file` and `export_shown_table`, because real email workflows commonly require:

`query_data -> export -> send_email`

This prevents those workflows from requiring the entire main Jarvis catalog just to create an attachment.

## Tool schema budget telemetry

The optimizer now logs a schema budget once for each distinct agent/tool-set signature:

`[AI-TOOL-BUDGET] agent=... tools=N schemaChars=X estTokens~Y top=toolA:chars,toolB:chars,...`

This gives a measured top-10 list of the largest tool schemas in the actual runtime request. We can then shorten descriptions/schema guidance starting with the tools that materially affect token cost rather than manually reviewing every tool equally.

The context log also reports completed trace removal:

`[AI-CONTEXT] ... oldTraceBlocksRemoved=N oldTraceCharsRemoved=X`

## Next runtime acceptance tests

1. Greeting (`γεια σου`) — expected ~hundreds of input tokens and zero tools.
2. Read-only Soft1 query — expected compact read tool set.
3. Email recipient lookup + draft — expected Echo tool set, not the full catalog.
4. `ψάξε στα εισερχόμενά μου` — expected Echo tool set.
5. Confirmation (`ναι` / `ναι στείλτο`) after an email draft — MUST retain send capability and should show old trace blocks removed.
6. Soft1 report -> CSV/XLSX -> email — expected query/export/email tools without unrelated item/courier/browser schemas.
7. Capture `[AI-TOOL-BUDGET]` lines and use the top-10 schema sizes for the next per-tool description/schema reduction pass.

## Safety constraints

- Never remove current-turn tool traces.
- Never add write capability that was not already present in the original request.
- Provider/model/routing authority remains server-side and unchanged.
- Any optimizer exception returns the original provider request.
- If domain intent is too complex or uncertain, prefer capability preservation over token reduction.
