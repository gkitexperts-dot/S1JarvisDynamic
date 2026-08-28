# Jarvis AI context optimization audit — 2026-08-26

## Trigger

A runtime OpenAI test with the user message `είσαι εδώ;` reported approximately 11.8k input tokens for a ~20-token answer. The user content itself was negligible, so the dominant cost was static/system context and tool schemas.

## Findings

1. `JarvisAgentClient` builds a mature, large system contract and broad tool catalog before transport. This is appropriate for ambiguous operational work but excessive for simple chat.
2. `VerilicProviderRequestOptimizer` already compacted Atlas read-only requests and dedicated agents, but the main logical `Jarvis` route deliberately returned the original request unchanged.
3. Final no-tools iterations for dedicated agents still carried the original large system prompt.
4. Tool schemas are a major fixed token cost. Sending them for greetings/presence chatter provides no capability benefit.
5. Provider choice is not the root cause. The same provider-neutral request is built before Verilic translates it to Anthropic/OpenAI/Gemini.
6. Conversation history can become another token source, but pruning arbitrary tool/attachment history is unsafe because function-call pairing and provider reasoning state must be preserved.

## Optimization protocol implemented

The optimization boundary remains immediately before the signed Verilic `/api/jarvis-ai/messages` request. This keeps provider/model/routing authority unchanged and applies equally to Anthropic, OpenAI and Gemini.

### Level 1 — conversational fast path

For clearly non-business messages such as greetings, presence checks and thanks:

- replace the large system contract with a minimal conversational Jarvis prompt;
- send zero tool schemas;
- cap `max_tokens` at 512 for that turn;
- keep at most the last 6 messages only when the entire history is plain text;
- never prune history containing tool results, images, documents or other structured blocks.

Strong Soft1/business vocabulary and action/read signals explicitly prevent this fast path from activating on operational requests.

### Level 2 — main Jarvis intent compaction

When the main Jarvis request has exactly one clear domain, filter the broad tool catalog to the matching role contract:

- read/reporting -> Atlas read-only tools;
- item management -> Forge;
- trader/AFM/AADE -> Compass;
- email/calendar/contacts -> Echo;
- courier -> Sprint;
- browser/web -> Scout.

Composite or uncertain requests fail open to the complete mature Jarvis contract.

### Level 3 — dedicated agent compaction

Atlas, Forge, Compass, Echo, Sprint, Scout and Sage retain role-specific compact prompts and tool allow-lists. The optimizer never adds a capability that was not already present in the original request.

### Level 4 — final-turn compaction

A no-tools final iteration for a known dedicated agent now receives the same compact role prompt instead of the original large training prompt. Main Jarvis final turns remain unchanged unless they qualified for the conversational fast path.

### Level 5 — runtime evidence

Each applied optimization writes a local diagnostic such as:

`[AI-CONTEXT] protocol agent=Jarvis mode=conversation requestChars=... systemChars=... tools=... messages=...`

This provides direct before/after evidence without logging prompt or user content.

## Safety invariants

- Provider/model/routing authority is never modified.
- No write/action tool is introduced by optimization.
- Ambiguous or multi-domain work keeps the full request.
- Structured tool/provider history is not pruned.
- Attachments disable the conversational-history shortcut.
- Exceptions fail open to the original request.

## Acceptance tests

1. `είσαι εδώ;` on main Jarvis: expect `mode=conversation`, `tools -> 0`, input tokens reduced from ~11.8k to a small fraction of that value.
2. `πόσος είναι ο τζίρος του πελάτη Χ;`: expect read/reporting compaction and `query_data` availability.
3. `βρες τον προμηθευτή με ΑΦΜ ...`: expect Compass-domain tools, not the complete catalog.
4. `στείλε email στον ...`: expect Echo-domain tools; no conversational fast path.
5. Composite request combining web + email or item + trader work: expect fail-open/full contract unless already routed to a dedicated agent.
6. Existing multi-turn tool call: verify function-call/tool-result pairing remains intact and final answer succeeds.
7. Repeat the tests with OpenAI, Anthropic and Gemini; token counts may differ by tokenizer, but the request-shape reduction should be provider-independent.
