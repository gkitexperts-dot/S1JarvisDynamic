from pathlib import Path

p = Path('UI/JarvisShell.ProviderHealth.cs')
s = p.read_text(encoding='utf-8')
old = '''                JarvisRuntimeAccessResult runtime = await Task.Run(() =>
                    JarvisLicenseGuard.CheckRuntimeAccessSilent(_xSupport));

                model = runtime?.AgentRouting?.Model;
                if (string.IsNullOrWhiteSpace(model))
                {
                    message = "AI provider: δεν έχει οριστεί μοντέλο";
                    state = "error";
                    await ShowProviderHealthStatusAsync(message, state);
                    if (explicitCommand)
                        PostProviderHealthCommandResult(message, state);
                    return;
                }

                if (runtime.AgentRouting == null ||
                    !runtime.AgentRouting.Available ||
                    !string.Equals(
                        runtime.AgentRouting.AgentAccountRef,
                        _agentAccountRef,
                        StringComparison.Ordinal))
                {
                    message = "AI provider: η δρομολόγηση άλλαξε · " + model;
                    state = "error";
                    await ShowProviderHealthStatusAsync(message, state);
                    if (explicitCommand)
                        PostProviderHealthCommandResult(message, state);
                    return;
                }

                var probe = new JarvisAgentHealthProbe();
                JarvisAgentHealthResult result = await probe.ProbeAsync(
                    _xSupport,
                    _agentAccountRef,
                    model);
'''
new = '''                // SINGLE remote agent-schema load. The startup Health response
                // itself returns Jarvis + every helper Provider/Model target.
                // Do not pre-resolve routing/model here and do not refresh it later.
                var probe = new JarvisAgentHealthProbe();
                JarvisAgentHealthResult result = await probe.ProbeAsync(
                    _xSupport,
                    _agentAccountRef);
                model = result == null ? null : result.Model;
'''
if old not in s:
    raise SystemExit('ProviderHealth legacy pre-routing block not found')
s = s.replace(old, new, 1)
s = s.replace('model = result.Model ?? model;', 'model = result.Model;', 1)
p.write_text(s, encoding='utf-8')

p = Path('UI/JarvisShell.UatRunner.cs')
s = p.read_text(encoding='utf-8')
start = s.index('        private async Task<UatTestResult> RunHealthUatAsync(UatTestCase test)')
end = s.index('        private static string BuildHealthTargetSummary', start)
replacement = '''        private Task<UatTestResult> RunHealthUatAsync(UatTestCase test)
        {
            try
            {
                // UAT HEALTH is deliberately local after Jarvis startup.
                // It validates/reports the immutable snapshot and MUST NOT call
                // Verilic Health/routing again in the same open Jarvis session.
                IReadOnlyList<JarvisAgentRuntimeTarget> targets =
                    JarvisAgentRuntimeSnapshot.GetAll();
                if (!JarvisAgentRuntimeSnapshot.IsInitialized ||
                    targets == null || targets.Count == 0)
                {
                    return Task.FromResult(new UatTestResult(
                        test,
                        "FAIL",
                        "Το startup AI agent snapshot δεν είναι διαθέσιμο.",
                        string.Empty));
                }

                string summary = string.Join("; ", targets.Select(x =>
                    (x.Agent ?? "—") + "=Connected/" +
                    (x.Provider ?? "—") + "/" +
                    (x.Model ?? "—") + "/" +
                    (x.Inherited ? "Inherited" : "Dedicated")));

                return Task.FromResult(new UatTestResult(
                    test,
                    "PASS",
                    "Startup snapshot: όλοι οι " + targets.Count +
                    " effective AI targets είναι διαθέσιμοι χωρίς νέο Verilic lookup.",
                    summary));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new UatTestResult(
                    test,
                    "FAIL",
                    "HEALTH snapshot exception: " + ex.Message,
                    string.Empty));
            }
        }

'''
s = s[:start] + replacement + s[end:]
p.write_text(s, encoding='utf-8')

if 'CheckRuntimeAccessSilent' in Path('UI/JarvisShell.ProviderHealth.cs').read_text(encoding='utf-8'):
    raise SystemExit('ProviderHealth still contains CheckRuntimeAccessSilent')
if 'ProbeAsync' in Path('UI/JarvisShell.UatRunner.cs').read_text(encoding='utf-8'):
    raise SystemExit('UAT still contains ProbeAsync')
