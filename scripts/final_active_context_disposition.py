from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_method(text, signature, replacement):
    start = text.find(signature)
    if start < 0:
        raise RuntimeError("method signature not found: " + signature)
    brace = text.find("{", start)
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
                if escape: escape = False
                elif ch == '\\': escape = True
                elif ch == '"': in_string = False
        else:
            if ch == '"':
                in_string = True
                verbatim = i > 0 and text[i - 1] == '@'
            elif ch == '{': depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    return text[:start] + replacement.rstrip() + text[i + 1:]
        i += 1
    raise RuntimeError("closing brace not found: " + signature)


def update_intent():
    p = ROOT / "Core" / "JarvisIntentOrchestration.cs"
    text = p.read_text(encoding="utf-8-sig")
    status = '''    internal enum JarvisIntentObjectStatus
    {
        Pending,
        Resolved,
        NeedsDynamicPass,
        NeedsClarification,
        Invalid
    }
'''
    addition = status + '''
    internal enum JarvisActiveContextDisposition
    {
        Replace,
        Continue
    }
'''
    if 'enum JarvisActiveContextDisposition' not in text:
        if status not in text: raise RuntimeError("status enum anchor not found")
        text = text.replace(status, addition, 1)

    text = text.replace(
        '            Objects = new List<JarvisIntentObject>();\n        }\n\n        public string OriginalPrompt { get; private set; }',
        '            Objects = new List<JarvisIntentObject>();\n            ActiveContextDisposition = JarvisActiveContextDisposition.Replace;\n        }\n\n        public string OriginalPrompt { get; private set; }\n        public JarvisActiveContextDisposition ActiveContextDisposition { get; set; }', 1)

    text = text.replace(
        '"Schema: {\\"intentObjects\\":[{\\"id\\":\\"o1\\",\\"intentFragment\\":\\"...\\",\\"inputs\\":{\\"name\\":\\"value\\"},\\"candidates\\":[{\\"taskType\\":\\"...\\",\\"confidence\\":0.94}]}]}\\n\\n" +',
        '"Schema: {\\"activeContextDisposition\\":\\"replace|continue\\",\\"intentObjects\\":[{\\"id\\":\\"o1\\",\\"intentFragment\\":\\"...\\",\\"inputs\\":{\\"name\\":\\"value\\"},\\"candidates\\":[{\\"taskType\\":\\"...\\",\\"confidence\\":0.94}]}]}\\n\\n" +', 1)

    old = '''            JObject root;
            try { root = JObject.Parse(responseJson); }
            catch (JsonException ex) { errors.Add("Intent decomposer returned invalid JSON: " + ex.Message); issues = errors.ToArray(); return false; }
            JArray array = root["intentObjects"] as JArray;'''
    new = '''            JObject root;
            try { root = JObject.Parse(responseJson); }
            catch (JsonException ex) { errors.Add("Intent decomposer returned invalid JSON: " + ex.Message); issues = errors.ToArray(); return false; }

            bool hasActiveContext = (originalPrompt ?? string.Empty).IndexOf(
                "[JARVIS_ACTIVE_ORCHESTRATION_CONTEXT]", StringComparison.Ordinal) >= 0;
            string disposition = root["activeContextDisposition"] == null
                ? string.Empty
                : root["activeContextDisposition"].ToString().Trim();
            if (string.Equals(disposition, "continue", StringComparison.OrdinalIgnoreCase))
                objectSet.ActiveContextDisposition = JarvisActiveContextDisposition.Continue;
            else if (string.Equals(disposition, "replace", StringComparison.OrdinalIgnoreCase) ||
                     (!hasActiveContext && string.IsNullOrWhiteSpace(disposition)))
                objectSet.ActiveContextDisposition = JarvisActiveContextDisposition.Replace;
            else if (hasActiveContext && string.IsNullOrWhiteSpace(disposition))
                errors.Add("Intent decomposer must return activeContextDisposition for an active orchestration context.");
            else
                errors.Add("Invalid activeContextDisposition: " + disposition);

            JArray array = root["intentObjects"] as JArray;'''
    if old not in text: raise RuntimeError("parse anchor not found")
    text = text.replace(old, new, 1)
    p.write_text(text, encoding="utf-8")
    print("updated", p)


def update_harness():
    p = ROOT / "Core" / "JarvisExecutionShadowHarness.cs"
    text = p.read_text(encoding="utf-8-sig")
    old = '''                JarvisShadowOrchestrationResult planning = await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, planningPrompt);
                if (!IsSupportedControlledPlan(planning))
                    return outcome;

                outcome.Handled = true;
                if (activeContext != null && !activeContext.HasOpenRun) activeContext.Begin(userPrompt);'''
    new = '''                JarvisShadowOrchestrationResult planning = await JarvisOrchestrationShadowCoordinator.RunAsync(xSupport, planningPrompt);
                bool replaceActiveRun = planning != null && planning.IntentObjects != null &&
                    planning.IntentObjects.ActiveContextDisposition == JarvisActiveContextDisposition.Replace;
                if (!IsSupportedControlledPlan(planning))
                {
                    if (activeContext != null && activeContext.HasOpenRun && replaceActiveRun)
                        activeContext.Clear();
                    return outcome;
                }

                outcome.Handled = true;
                if (activeContext != null && (!activeContext.HasOpenRun || replaceActiveRun))
                    activeContext.Begin(userPrompt);'''
    if old not in text: raise RuntimeError("harness disposition anchor not found")
    text = text.replace(old, new, 1)
    p.write_text(text, encoding="utf-8")
    print("updated", p)


def update_registry():
    p = ROOT / "Core" / "JarvisPolicyRegistry.cs"
    text = p.read_text(encoding="utf-8-sig")
    if 'DECOMPOSER.ACTIVE_CONTEXT_DISPOSITION' in text:
        return
    anchor = '''            P("DECOMPOSER.SELF_CONTAINED_OBJECT", JarvisPolicyScope.Task, JarvisPolicyEnforcement.Training,
                "Κάθε atomic object είναι self-contained: κληρονομεί από το ίδιο user prompt μόνο τα shared facts που απαιτούνται για αυτόνομη εκτέλεση. Μην αφήνεις fragment τύπου 'και στο calendar' που χάνει πρόσωπο, ημερομηνία, ώρα, entity ή recipient.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 915),
'''
    addition = anchor + '''
            P("DECOMPOSER.ACTIVE_CONTEXT_DISPOSITION", JarvisPolicyScope.Orchestration, JarvisPolicyEnforcement.Both,
                "Όταν υπάρχει JARVIS_ACTIVE_ORCHESTRATION_CONTEXT, δήλωσε activeContextDisposition=continue μόνο αν το CURRENT_OPERATOR_MESSAGE διορθώνει, συμπληρώνει, επιβεβαιώνει ή συνεχίζει το ενεργό run. Δήλωσε replace όταν είναι ανεξάρτητο νέο αίτημα. Η απόφαση είναι semantic και δεν βασίζεται σε λίστα λέξεων/φράσεων.", agents: A("Jarvis"), tasks: A("__decomposition"), priority: 914),
'''
    if anchor not in text: raise RuntimeError("decomposer policy anchor not found")
    text = text.replace(anchor, addition, 1)
    p.write_text(text, encoding="utf-8")
    print("updated", p)


def main():
    update_intent()
    update_harness()
    update_registry()


if __name__ == "__main__":
    main()
