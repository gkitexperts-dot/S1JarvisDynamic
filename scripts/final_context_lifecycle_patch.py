from pathlib import Path

p = Path(__file__).resolve().parents[1] / "Core" / "JarvisExecutionShadowHarness.cs"
text = p.read_text(encoding="utf-8-sig")
old = '''                outcome.Handled = true;
                if (activeContext != null) activeContext.CapturePlanning(planning);
                bool hasEmail = HasTask(planning, "SendEmail");'''
new = '''                outcome.Handled = true;
                if (activeContext != null && !activeContext.HasOpenRun) activeContext.Begin(userPrompt);
                bool hasEmail = HasTask(planning, "SendEmail");'''
if old not in text:
    raise RuntimeError("active context open anchor not found")
text = text.replace(old, new, 1)
old = '''                ResolveDeterministicSendRecipient(planning);
                ResolveDeterministicRuntimeContext(planning, xSupport);

                var coordinator'''
new = '''                ResolveDeterministicSendRecipient(planning);
                ResolveDeterministicRuntimeContext(planning, xSupport);
                if (activeContext != null) activeContext.CapturePlanning(planning);

                // A supported re-plan without SendEmail supersedes any previously
                // frozen email payload. Never leave stale confirmation state alive.
                if (!hasEmail && pendingSession != null && pendingSession.HasPending)
                {
                    pendingSession.Clear();
                    if (activeContext != null) activeContext.ClearPendingConfirmation();
                }

                var coordinator'''
if old not in text:
    raise RuntimeError("capture planning anchor not found")
text = text.replace(old, new, 1)
p.write_text(text, encoding="utf-8")
print("updated", p)
