from pathlib import Path
p = Path('UI/JarvisShell.ProviderHealth.cs')
s = p.read_text(encoding='utf-8')
old = '''                if (explicitCommand && JarvisAgentRuntimeSnapshot.IsInitialized)
                {
                    PostProviderHealthSnapshotCommandResult();
                    return;
                }
'''
new = '''                if (explicitCommand)
                {
                    if (JarvisAgentRuntimeSnapshot.IsInitialized)
                    {
                        PostProviderHealthSnapshotCommandResult();
                    }
                    else
                    {
                        PostProviderHealthCommandResult(
                            "AI agent: το startup snapshot δεν είναι διαθέσιμο. Κλείσε και άνοιξε ξανά τον Jarvis.",
                            "error");
                    }
                    return;
                }
'''
if old not in s:
    raise SystemExit('explicit HEALTH block not found')
s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8')
