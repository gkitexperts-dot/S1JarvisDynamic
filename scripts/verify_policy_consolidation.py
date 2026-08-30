from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def method_body(text: str, signature: str) -> str:
    start = text.find(signature)
    if start < 0:
        raise AssertionError(f"missing method: {signature}")
    brace = text.find("{", start)
    if brace < 0:
        raise AssertionError(f"missing method body: {signature}")
    depth = 0
    in_string = False
    escape = False
    i = brace
    while i < len(text):
        ch = text[i]
        if in_string:
            if escape:
                escape = False
            elif ch == "\\":
                escape = True
            elif ch == '"':
                in_string = False
        else:
            if ch == '"':
                in_string = True
            elif ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
                if depth == 0:
                    return text[brace:i + 1]
        i += 1
    raise AssertionError(f"unterminated method: {signature}")


def fail(message: str, issues: list[str]) -> None:
    issues.append(message)


def main() -> int:
    issues: list[str] = []

    registry = read("Core/JarvisPolicyRegistry.cs")
    required_agents = ["Atlas", "Forge", "Compass", "Echo", "Sprint", "Scout", "Sage", "Jarvis"]
    for agent in required_agents:
        if f'"{agent}"' not in registry:
            fail(f"policy registry does not mention required logical agent {agent}", issues)

    client = read("Core/JarvisAgentClient.cs")
    legacy_prompt = method_body(client, "private string BuildSystemPrompt(")
    forbidden_legacy_policy_markers = [
        "ΓΝΩΣΤΟ SCHEMA",
        "ΑΝΟΙΓΜΑ/ΔΗΜΙΟΥΡΓΙΑ",
        "HELP MODE",
        "BROWSER MODE",
        "EMAIL MODE",
        "COURIER MODE",
        "ΑΝΕΠΙΣΤΡΕΠΤ",
        "ΔΙΕΥΚΡΙΝΙΣΤΙΚΕΣ ΕΡΩΤΗΣΕΙΣ",
    ]
    for marker in forbidden_legacy_policy_markers:
        if marker in legacy_prompt:
            fail(f"legacy BuildSystemPrompt contains scattered policy marker: {marker}", issues)
    if "JARVIS_POLICY_CONTEXT" not in legacy_prompt:
        fail("legacy BuildSystemPrompt does not delegate behavioral policy to JARVIS_POLICY_CONTEXT", issues)

    transport = read("Access/Verilic/VerilicAiMessagesClient.cs")
    identity = method_body(transport, "private static string ApplyProductIdentityPolicy(")
    if "const string identityRule" in identity or "user-facing assistant identity" in identity:
        fail("transport still contains a separate product identity policy", issues)
    correction = method_body(transport, "private static string BuildCapabilityCorrectionRequest(")
    if "Policy=GLOBAL.REGISTRY_IS_AUTHORITY" not in correction:
        fail("capability correction no longer references centralized registry policy id", issues)

    entity_catalog = read("Core/JarvisBusinessEntityCatalog.cs")
    if '["policy"]' in entity_catalog:
        fail("business entity catalog contains behavioral policy prose", issues)

    tool_registry = read("Core/JarvisToolRegistry.cs")
    if "string fallbackPolicy" in tool_registry or "string fallback)" in tool_registry:
        fail("tool registry has reintroduced embedded fallback policy prose", issues)
    if "JarvisPolicyRegistry.GetToolFallbackPolicy" not in tool_registry:
        fail("tool fallback compatibility projection is not backed by central policy registry", issues)

    runtime = read("Core/JarvisAgentRuntimeSnapshot.cs")
    if "JarvisPolicyRequestEnricher.Apply" not in runtime:
        fail("not every outbound logical-agent request passes through the central policy enricher", issues)

    context_builder = read("Core/JarvisAgentContextBuilder.cs")
    if "JarvisTaskRegistry.AllTasks" not in context_builder or "task.OwnerAgent" not in context_builder:
        fail("multi-tool policy resolution is not owner-generic across all registered tasks", issues)

    if issues:
        print("Policy consolidation verification FAILED:")
        for issue in issues:
            print(f"- {issue}")
        return 1

    print("Policy consolidation verification passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
