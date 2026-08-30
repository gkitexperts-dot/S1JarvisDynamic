# S1Jarvis Development Invariants

This file is a persistent engineering contract for anyone (human or AI) changing S1Jarvis. **Read this file first whenever a new coding conversation/session starts** and before modifying licensing, provisioning, routing, providers, models, credentials, Health, agent orchestration, or AI request construction.

## Jarvis ↔ Verilic lifecycle — NON-NEGOTIABLE

1. **Verilic participates in AI configuration only at Jarvis BOOT and explicit HEALTH.**
   - BOOT keeps all existing licence, activation, installation identity, proof/signature, and readiness checks.
   - After licence validation, BOOT provisions the complete AI execution configuration for every required logical agent.
   - Explicit `HEALTH` is the only allowed runtime exception; it may re-check and refresh provisioning.
   - A normal user prompt, tool iteration, orchestration step, presentation call, dataset refinement, or agent dispatch MUST NOT contact Verilic for AI routing, model selection, credentials, or message proxying.

2. **Every session agent execution object MUST contain all three AI execution values.**
   - `Provider` / AI company.
   - `Model`.
   - `API credential` for that customer's agent account.
   - Plus the logical agent name (`Jarvis`, `Atlas`, `Forge`, `Compass`, `Echo`, `Sprint`, `Scout`, `Sage`) and routing metadata (`Dedicated/Inherited`) where applicable.
   - Missing Provider, Model, or credential means the agent is not executable. Fail closed; never invent a fallback.

3. **Credentials are session-only secrets.**
   - Provider API keys are downloaded only by BOOT provisioning or explicit HEALTH refresh.
   - They live only in the in-memory Jarvis session registry.
   - Never persist them to app.config, local JSON, registry, DPAPI state, database, telemetry, exception text, debug log, UI, or exported files.
   - On Jarvis shutdown/reset, clear/dispose credential buffers.
   - On the next Jarvis opening, provision them again.

4. **Normal AI execution is direct provider execution.**
   - Runtime code selects a logical agent locally.
   - It reads Provider + Model + API credential from `Core/JarvisAgentRuntimeSnapshot.cs`.
   - It calls that AI provider directly.
   - `/api/jarvis-ai/messages` (or any equivalent Verilic message relay) MUST NOT be used by normal Jarvis runtime prompts.
   - Verilic is the licence/provisioning authority, not the normal AI message proxy.

5. **HEALTH refresh is atomic.**
   - HEALTH obtains a complete candidate agent registry from Verilic.
   - Validate all required agents and all required Provider + Model + credential values before activation.
   - Only after full validation replace the existing in-memory registry atomically.
   - If HEALTH fails or returns an incomplete registry, preserve the currently working registry unchanged.
   - HEALTH output may show agent/provider/model/routing and `Credential=Loaded/Missing`; it must never reveal the credential itself.

## Agent/provider/model neutrality — NON-NEGOTIABLE

1. **Logical agents are provider-neutral identities, never aliases for vendors or models.**
   - `Jarvis`, `Atlas`, `Forge`, `Compass`, `Echo`, `Sprint`, `Scout`, and `Sage` express responsibilities/capabilities only.
   - No agent behavior, task contract, tool registry entry, orchestration rule, dataset rule, presentation rule, or business flow may depend on Google/Gemini, OpenAI, Anthropic/Claude, or any concrete model family.

2. **The Jarvis AI request/response contract must remain provider-neutral.**
   - Core code may express neutral semantics such as system instructions, messages, tools, tool choice, images/documents, token limits, temperature and structured tool results.
   - Core code must not reshape those semantics merely because one provider accepts a smaller/different wire schema.
   - A provider limitation must be handled at the provider adapter boundary, not by weakening or forking the core orchestration contract.

3. **Provider-specific translation belongs only in the direct provider adapter.**
   - `Access/Verilic/JarvisDirectAiTransport.cs` translates the neutral Jarvis request into each provider's native wire format and normalizes each native response back to the neutral Jarvis response contract.
   - Tool/function schemas and tool-choice semantics must be translated independently for Google, OpenAI, Anthropic, and future providers while preserving the same logical intent.
   - Provider-specific unsupported schema keywords may be normalized/dropped only in that provider adapter. The source neutral tool schema remains unchanged.
   - Do not add model-specific branches in orchestration/business code to make one model pass a test.

4. **Never solve provider compatibility by changing the selected provider/model.**
   - Do not silently switch model/provider when a request shape fails.
   - Fix the adapter mapping or fail with a diagnostic.
   - Provider/model selection remains exclusively the BOOT/HEALTH-provisioned configuration from Verilic.

## Provider/model rules — NON-NEGOTIABLE

1. **Never hardcode a concrete AI provider or model in desktop/client business or orchestration code.**
   - No literal fallback model.
   - No per-agent model constants.
   - No hidden Claude/Gemini/OpenAI model fallback in orchestration, legacy chat, Document Reader, tests, or error recovery.
   - Client business code chooses a logical agent only.
   - Provider and model come from the boot/HEALTH session registry.

2. **Do not re-fetch agent configuration per prompt.**
   - No per-prompt routing endpoint.
   - No per-iteration Health/provisioning call.
   - No per-tool-call credential lookup from Verilic.
   - No silent background refresh.

3. **Fail closed — never invent an AI target.**
   - If BOOT provisioning is missing/incomplete, block AI execution.
   - If a required session target loses its credential, require successful HEALTH or restart.
   - Never switch provider/model automatically because one is unavailable.

## Enforcement points

- Existing licensing/activation classes under `Access/Verilic/` — remain authoritative for licence/installation validation and must not be removed as part of AI provisioning changes.
- `UI/JarvisShell.ProviderHealth.cs` — BOOT provisioning and the only explicit runtime refresh (`HEALTH`); also clears session credentials on shell shutdown.
- `Core/JarvisAgentHealthProbe.cs` — signed BOOT/HEALTH provisioning request. This must never be invoked by normal prompt execution.
- `Core/JarvisAgentRuntimeSnapshot.cs` — in-memory session execution registry; every executable agent requires Provider + Model + API credential; supports atomic HEALTH replacement and secret clearing.
- `Access/Verilic/JarvisDirectAiTransport.cs` — normal direct provider transport after provisioning. It must never contact Verilic; all provider-specific request/response/schema/tool-choice translation stays here.
- `Access/Verilic/VerilicAiMessagesClient.cs` — legacy class name retained only as the local session dispatcher/compatibility boundary; it must not send runtime messages to Verilic.
- `Core/JarvisAgentClient.cs` and orchestration clients — select logical agents/tasks only; they must not own provider/model/API-key configuration.

## Security rules for AI credentials

- Never print or serialize an API key in a log.
- Never include an API key in `AgentProxyResponse`, usage telemetry, UI Health tables, exception messages, or orchestration state.
- Do not return a secret-bearing target from display/list APIs; UI snapshots must be secret-free clones.
- Keep the lifetime of any managed string created from a credential as short as practical (for the HTTP header only).
- Wipe mutable secret buffers on replacement/shutdown where technically possible on .NET Framework.

## Automated guard

Run this before considering any AI-routing/provisioning change complete:

```text
python scripts/verify_ai_routing_policy.py
```

The same check is wired into `.github/workflows/ai-routing-policy.yml`. It must reject runtime C# changes that reintroduce:

- concrete/hardcoded model assignments,
- normal-prompt `/api/jarvis-ai/messages` proxying,
- per-prompt routing/Health/provisioning lookups,
- or persisted/logged provider credentials.

## Before changing AI code

Search the branch for model/provider literals, `/api/jarvis-ai/messages`, routing/Health calls, and credential persistence/logging. A change is not complete if it introduces any of those outside the BOOT/explicit-HEALTH provisioning boundary.

Also inspect whether a proposed fix belongs in the provider adapter. If the issue is a native provider wire-format difference (tool schema, tool choice, multimodal blocks, response shape, usage fields, reasoning controls), keep the core Jarvis contract unchanged and translate only at the adapter boundary.

This invariant exists because Verilic owns licence/provisioning while an open Jarvis session owns deterministic, inexpensive, direct AI execution using the customer's session-only agent credentials, with logical agents remaining independent of the selected AI vendor/model.
