#!/usr/bin/env python3
"""Verify the non-negotiable AI routing policy documented in /AGENTS.md.

Run from the repository root:
    python scripts/verify_ai_routing_policy.py

This is intentionally source-level and dependency-free so it can be run by a
human, an AI coding session, or CI before changes are accepted.
"""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
errors = []

# Runtime C# must never own a concrete AI model identifier. Models come from
# the immutable startup Verilic Health snapshot.
model_literal_patterns = [
    re.compile(r'\b(?:const\s+string\s+)?(?:Model|model|RuntimeAiModel)\s*=\s*"[^"\r\n]+"'),
    re.compile(r'\[\s*"model"\s*\]\s*=\s*"[^"\r\n]+"'),
]
known_model_id = re.compile(
    r'"(?:claude-[^"\s]+|gemini-[^"\s]+|gpt-[^"\s]+|o[1-9](?:-[^"\s]+)?)"',
    re.IGNORECASE,
)

for path in ROOT.rglob('*.cs'):
    rel = path.relative_to(ROOT).as_posix()
    text = path.read_text(encoding='utf-8', errors='replace')
    for lineno, line in enumerate(text.splitlines(), 1):
        if any(pattern.search(line) for pattern in model_literal_patterns):
            errors.append(f'{rel}:{lineno}: hardcoded model assignment: {line.strip()}')
        elif known_model_id.search(line):
            errors.append(f'{rel}:{lineno}: concrete model id in C# source: {line.strip()}')

provider_health = ROOT / 'UI' / 'JarvisShell.ProviderHealth.cs'
if provider_health.exists():
    text = provider_health.read_text(encoding='utf-8', errors='replace')
    if 'CheckRuntimeAccessSilent' in text:
        errors.append(
            'UI/JarvisShell.ProviderHealth.cs: startup Health must not pre-resolve routing/model; '
            'Health itself is the one authoritative schema load.'
        )
    if text.count('new JarvisAgentHealthProbe()') != 1:
        errors.append(
            'UI/JarvisShell.ProviderHealth.cs: expected exactly one startup Health probe construction.'
        )

uat = ROOT / 'UI' / 'JarvisShell.UatRunner.cs'
if uat.exists() and 'JarvisAgentHealthProbe' in uat.read_text(encoding='utf-8', errors='replace'):
    errors.append(
        'UI/JarvisShell.UatRunner.cs: UAT HEALTH must read JarvisAgentRuntimeSnapshot, not call Verilic Health again.'
    )

# Health endpoint access is allowed only in the probe implementation; normal
# runtime code must consume JarvisAgentRuntimeSnapshot instead.
for path in ROOT.rglob('*.cs'):
    rel = path.relative_to(ROOT).as_posix()
    if rel == 'Core/JarvisAgentHealthProbe.cs':
        continue
    text = path.read_text(encoding='utf-8', errors='replace')
    if 'ProviderHealthUri' in text:
        errors.append(f'{rel}: direct ProviderHealthUri access outside JarvisAgentHealthProbe is forbidden.')

required_files = [
    ROOT / 'AGENTS.md',
    ROOT / 'Core' / 'JarvisAgentRuntimeSnapshot.cs',
]
for path in required_files:
    if not path.exists():
        errors.append(f'{path.relative_to(ROOT).as_posix()}: required routing policy file is missing.')

if errors:
    print('AI ROUTING POLICY: FAIL')
    for error in errors:
        print(' - ' + error)
    sys.exit(1)

print('AI ROUTING POLICY: PASS')
print(' - no hardcoded AI model ids/assignments in C#')
print(' - startup Health is the sole agent-schema load path')
print(' - UAT Health reads the immutable startup snapshot')
print(' - AGENTS.md and JarvisAgentRuntimeSnapshot are present')
