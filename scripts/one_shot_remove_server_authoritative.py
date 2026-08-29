from pathlib import Path
p = Path('UI/JarvisShell.DrAssistant.cs')
s = p.read_text(encoding='utf-8')
old = '                    ["model"] = "server-authoritative",\n'
if old not in s:
    raise SystemExit('DR server-authoritative model placeholder not found')
s = s.replace(old, '', 1)
p.write_text(s, encoding='utf-8')
