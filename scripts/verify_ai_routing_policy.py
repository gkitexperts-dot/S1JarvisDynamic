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
# the immutable startup Verilic Health snapshot. Match actual code assignment
# syntax, not logging strings such as " model=".
assignment_patterns = [
    re.compile(r'^\s*(?:private|public|internal|protected)?\s*(?:static\s+)?(?:readonly\s+)?(?:const\s+)?string\s+(?:Model|RuntimeAiModel)\s*=\s*"[^"\r\n]+"'),
    re.compile(r'^\s*model\s*=\s*"[^"\r\n]+"\s*[,;]'),
    re.compile(r'^\s*\[\s*"model"\s*\]\s*=\s*"[^"\r\n]+"\s*[,;]'),
]
known_model_id = re.compile(
    r'"(?:claude-[^"\s]+|gemini-[^"\s]+|gpt-[^"\s]+|o[1-9](?:-[^"\s]+)?)"',
    re.IGNORECASE,
)

# This exact value is not an AI target: it is the UI source label requested for
# deterministic local replies (`IN 0 OUT 0 JARVIS`).
local_ui_marker = 'UI/JarvisShell.OrchestrationShadow.cs'

for path in ROOT.rglob('*.cs'):
    rel = path.relative_to(ROOT).as_posix()
    text = path.read_text(encoding='utf-8', errors='replace')
    for lineno, line in enumerate(text.splitlines(), 1):
        if rel == local_ui_marker and '["model"] = "JARVIS"' in line:
            continue
        if any(pattern.search(line) for pattern in assignment_patterns):
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

# Direct health endpoint use is implementation detail of configuration + probe.
allowed_health_uri_files = {
    'Core/JarvisAgentHealthProbe.cs',
    'Access/Verilic/VerilicRuntimeConfiguration.cs',
}
for path in ROOT.rglob('*.cs'):
    rel = path.relative_to(ROOT).as_posix()
    if rel in allowed_health_uri_files:
        continue
    text = path.read_text(encoding='utf-8', errors='replace')
    if 'ProviderHealthUri' in text:
        errors.append(f'{rel}: direct ProviderHealthUri access outside configuration/probe is forbidden.')

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
