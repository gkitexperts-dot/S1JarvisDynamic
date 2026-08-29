# S1Jarvis Development Invariants

This file is a persistent engineering contract for anyone (human or AI) changing S1Jarvis. Read it before modifying routing, providers, models, agent orchestration, Health, or AI request construction.

## AI agent/provider/model routing — NON-NEGOTIABLE

1. **Never hardcode an AI provider or model in desktop/client code.**
   - No literal fallback model.
   - No per-agent model constants.
   - No hidden default such as a Claude/Gemini model in orchestration, legacy chat, Document Reader, tests, or error recovery.
   - Client code selects a **logical agent only** (`Jarvis`, `Atlas`, `Forge`, `Compass`, `Echo`, `Sprint`, `Scout`, `Sage`).

2. **Verilic agent/provider/model configuration is loaded exactly once when the Jarvis shell starts.**
   - The startup Provider Health check is the authoritative source of the effective agent schema (`Agent + Provider + Model + Dedicated/Inherited`).
   - A successful startup Health result initializes `Core/JarvisAgentRuntimeSnapshot.cs`.
   - The Jarvis UI must not become ready unless this snapshot is valid.

3. **The startup snapshot is immutable for the lifetime of the open Jarvis shell.**
   - Every AI call uses the model from `JarvisAgentRuntimeSnapshot` for its logical agent.
   - Do not call Verilic routing/Health/schema endpoints again per prompt, per iteration, per tool call, or per agent dispatch.
   - Do not silently refresh the snapshot while Jarvis is open.

4. **Verilic changes take effect on the next Jarvis startup.**
   - If an administrator changes an agent/provider/model in Verilic, an already-open Jarvis session keeps its startup snapshot.
   - Closing and reopening Jarvis performs the next authoritative Health load and adopts the new configuration.

5. **Fail closed — never invent a fallback.**
   - If the startup snapshot is missing, incomplete, or invalid, block AI execution and require Jarvis restart / successful startup Health.
   - If runtime metadata conflicts with the startup snapshot, stop the call and require restart. Never switch to another model or agent automatically.

## Enforcement points

- `UI/JarvisShell.ProviderHealth.cs` — the one normal startup load of the Verilic agent schema.
- `Core/JarvisAgentRuntimeSnapshot.cs` — immutable in-memory session snapshot.
- `Access/Verilic/VerilicAiMessagesClient.cs` — final AI boundary; injects the startup model for the logical agent and rejects missing/drifted snapshots.
- `Core/JarvisAgentClient.cs` and orchestration clients — choose logical agents only; they must not own model configuration.

## Before changing AI code

Search the branch for model/provider literals and for calls to routing/Health endpoints. A change is not complete if it introduces:

- a hardcoded model/provider name,
- a second normal-session agent-schema fetch,
- a per-prompt routing/Health lookup,
- or a fallback that bypasses `JarvisAgentRuntimeSnapshot`.

This invariant exists because agent configuration belongs to Verilic, while an open Jarvis session must be deterministic, inexpensive, and stable.
