from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_method(text: str, signature: str, replacement: str) -> str:
    start = text.find(signature)
    if start < 0:
        raise RuntimeError(f"method signature not found: {signature}")
    brace = text.find("{", start)
    if brace < 0:
        raise RuntimeError(f"opening brace not found: {signature}")
    depth = 0
    in_string = False
    verbatim = False
    escape = False
    i = brace
    while i < len(text):
        ch = text[i]
        if in_string:
            if verbatim:
                if ch == '"':
                    if i + 1 < len(text) and text[i + 1] == '"':
                        i += 2
                        continue
                    in_string = False
                    verbatim = False
            else:
                if escape:
                    escape = False
                elif ch == '\\':
                    escape = True
                elif ch == '"':
                    in_string = False
        else:
            if ch == '"':
                in_string = True
                verbatim = i > 0 and text[i - 1] == '@'
            elif ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    return text[:start] + replacement.rstrip() + text[i + 1:]
        i += 1
    raise RuntimeError(f"closing brace not found: {signature}")


def update(path: str, transform) -> None:
    file_path = ROOT / path
    original = file_path.read_text(encoding="utf-8-sig")
    changed = transform(original)
    if changed == original:
        print(f"unchanged: {path}")
        return
    file_path.write_text(changed, encoding="utf-8")
    print(f"updated: {path}")


def consolidate_agent_client(text: str) -> str:
    replacement = r'''        private string BuildSystemPrompt(
            XSupport xSupport, bool forceFinalAnswer = false, bool helpMode = false,
            int reportDecimalPlaces = 2, bool browserMode = false, bool emailMode = false,
            bool courierMode = false, string extraInstructions = null,
            bool itemMode = false, bool traderMode = false, string currentUserName = null)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null)
                throw new InvalidOperationException("Jarvis runtime context is unavailable.");

            var info = xSupport.ConnectionInfo;
            string mode = helpMode ? "help"
                : browserMode ? "browser"
                : courierMode ? "courier"
                : emailMode ? "email"
                : itemMode ? "item"
                : traderMode ? "trader"
                : "general";

            var context = new JObject
            {
                ["companyId"] = info.CompanyId,
                ["branchId"] = info.BranchId,
                ["currentUserId"] = info.UserId,
                ["currentUserName"] = currentUserName ?? string.Empty,
                ["mode"] = mode,
                ["reportDecimalPlaces"] = reportDecimalPlaces,
                ["forceFinalAnswer"] = forceFinalAnswer,
                ["businessEntityKnowledge"] = JarvisBusinessEntityCatalog.BuildAgentContext()
            };

            if (!string.IsNullOrWhiteSpace(extraInstructions))
                context["administratorBusinessContext"] = extraInstructions;

            return
                "Είσαι ο Jarvis μέσα στο Soft1. Το ακόλουθο JSON είναι μόνο runtime/knowledge context. " +
                "Οι behavioral policies παρέχονται αποκλειστικά από το JARVIS_POLICY_CONTEXT.\\n" +
                context.ToString(Formatting.None);
        }'''
    text = replace_method(text, "        private string BuildSystemPrompt(", replacement)
    return text


def consolidate_transport(text: str) -> str:
    identity_replacement = r'''        private static string ApplyProductIdentityPolicy(
            string internalAgentName,
            string providerRequestJson)
        {
            // Product identity is a centralized global policy injected by
            // JarvisPolicyRequestEnricher. Keep this compatibility hook free of
            // independent policy prose.
            return providerRequestJson;
        }'''
    text = replace_method(text, "        private static string ApplyProductIdentityPolicy(", identity_replacement)

    correction_replacement = r'''        private static string BuildCapabilityCorrectionRequest(
            string providerRequestJson,
            string rejectedText,
            IEnumerable<string> attachedTools)
        {
            JObject request = JObject.Parse(providerRequestJson ?? "{}");
            JArray messages = request["messages"] as JArray;
            if (messages == null)
            {
                messages = new JArray();
                request["messages"] = messages;
            }

            if (!string.IsNullOrWhiteSpace(rejectedText))
                messages.Add(new JObject { ["role"] = "assistant", ["content"] = rejectedText });

            string toolList = string.Join(", ", (attachedTools ?? Enumerable.Empty<string>())
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            messages.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = "[JARVIS_SUPERVISORY_CORRECTION] Policy=GLOBAL.REGISTRY_IS_AUTHORITY; attachedTools=" + toolList
            });

            return request.ToString(Formatting.None);
        }'''
    text = replace_method(text, "        private static string BuildCapabilityCorrectionRequest(", correction_replacement)
    text = text.replace(
        '"UNKNOWN (δεν ανακτήθηκε από COMPANY - μην υποθέσεις όνομα/κλάδο)"',
        '"UNKNOWN"')
    return text


def consolidate_entity_catalog(text: str) -> str:
    # Defensive no-op when already cleaned by a direct commit.
    return text.replace(
        ',\n                    ["policy"] = "Use only registered role discriminators. Unknown SODTYPE/object mappings must never be invented."',
        '')


def consolidate_registry(text: str) -> str:
    if 'GLOBAL.PRODUCT_IDENTITY' not in text:
        anchor = '''            P("GLOBAL.REGISTRY_IS_AUTHORITY", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Both,\n                "Task/tool registries και deterministic runtime contracts είναι authoritative για capabilities, prerequisites και outputs. Prose από model δεν μπορεί να αναιρέσει capability που έχει πράγματι δοθεί στο request.", priority: 995),\n'''
        insertion = anchor + '''\n            P("GLOBAL.PRODUCT_IDENTITY", JarvisPolicyScope.Global, JarvisPolicyEnforcement.Training,\n                "Η μοναδική user-facing ταυτότητα είναι ο Jarvis. Atlas, Forge, Compass, Echo, Sprint, Scout και Sage είναι εσωτερικοί execution roles και δεν αυτοπαρουσιάζονται στον χειριστή ως ξεχωριστοί assistants.", priority: 994),\n'''
        if anchor not in text:
            raise RuntimeError("policy registry anchor not found")
        text = text.replace(anchor, insertion, 1)
    return text


def main() -> None:
    update("Core/JarvisAgentClient.cs", consolidate_agent_client)
    update("Access/Verilic/VerilicAiMessagesClient.cs", consolidate_transport)
    update("Core/JarvisBusinessEntityCatalog.cs", consolidate_entity_catalog)
    update("Core/JarvisPolicyRegistry.cs", consolidate_registry)


if __name__ == "__main__":
    main()
