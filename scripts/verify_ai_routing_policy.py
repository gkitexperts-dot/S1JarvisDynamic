#!/usr/bin/env python3
"""Verify the non-negotiable AI provisioning/runtime policy in /AGENTS.md.

Run from the repository root:
    python scripts/verify_ai_routing_policy.py

The verifier is intentionally source-level and dependency-free so it can be
run locally and in CI before AI-related changes are accepted.
"""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
errors = []

# Business/orchestration C# must never own a concrete AI model identifier.
# Provider adapter endpoint constants are allowed in JarvisDirectAiTransport;
# concrete MODEL selection is never allowed there or anywhere else.
assignment_patterns = [
    re.compile(r'^\s*(?:private|public|internal|protected)?\s*(?:static\s+)?(?:readonly\s+)?(?:const\s+)?string\s+(?:Model|RuntimeAiModel)\s*=\s*"[^"\r\n]+"'),
    re.compile(r'^\s*model\s*=\s*"[^"\r\n]+"\s*[,;]'),
    re.compile(r'^\s*\[\s*"model"\s*\]\s*=\s*"[^"\r\n]+"\s*[,;]'),
]
known_model_id = re.compile(
    r'"(?:claude-[^"\s]+|gemini-[^"\s]+|gpt-[^"\s]+|o[1-9](?:-[^"\s]+)?)"',
    re.IGNORECASE,
)

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

# Normal runtime must never relay AI messages through Verilic. This literal is
# forbidden in all runtime C# source. The text may exist in AGENTS.md only as a
# documented anti-pattern, which this scanner intentionally does not inspect.
for path in ROOT.rglob('*.cs'):
    rel = path.relative_to(ROOT).as_posix()
    text = path.read_text(encoding='utf-8', errors='replace')
    if '/api/jarvis-ai/messages' in text or 'api/jarvis-ai/messages' in text:
        errors.append(f'{rel}: normal runtime Verilic AI message proxy is forbidden.')

# Only the provisioning probe/configuration may know the remote Health URI.
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

provider_health = ROOT / 'UI' / 'JarvisShell.ProviderHealth.cs'
if provider_health.exists():
    text = provider_health.read_text(encoding='utf-8', errors='replace')
    if 'CheckRuntimeAccessSilent' in text:
        errors.append(
            'UI/JarvisShell.ProviderHealth.cs: BOOT/HEALTH provisioning must not pre-resolve a model.'
        )
    if text.count('new JarvisAgentHealthProbe()') != 1:
        errors.append(
            'UI/JarvisShell.ProviderHealth.cs: BOOT and explicit HEALTH must share one provisioning probe path.'
        )
    if 'JarvisAgentRuntimeSnapshot.TryRefresh' not in text:
        errors.append(
            'UI/JarvisShell.ProviderHealth.cs: explicit HEALTH must atomically refresh the session registry.'
        )
    if 'Closed += ProviderHealthCheck_Closed' not in text or 'JarvisAgentRuntimeSnapshot.Reset()' not in text:
        errors.append(
            'UI/JarvisShell.ProviderHealth.cs: Jarvis shutdown must clear the in-memory agent registry.'
        )

# The session registry must enforce all execution material and secret clearing.
snapshot = ROOT / 'Core' / 'JarvisAgentRuntimeSnapshot.cs'
if snapshot.exists():
    text = snapshot.read_text(encoding='utf-8', errors='replace')
    required_markers = [
        'source.Provider',
        'source.Model',
        'source.ApiKey',
        'SetApiKey',
        'ClearSecret',
        'TryRefresh',
    ]
    for marker in required_markers:
        if marker not in text:
            errors.append(
                f'Core/JarvisAgentRuntimeSnapshot.cs: missing required session-registry invariant: {marker}'
            )

# Direct provider transport must exist and must not load Verilic configuration.
direct_transport = ROOT / 'Access' / 'Verilic' / 'JarvisDirectAiTransport.cs'
if direct_transport.exists():
    text = direct_transport.read_text(encoding='utf-8', errors='replace')
    if 'VerilicRuntimeConfiguration' in text or 'ProviderHealthUri' in text or 'RoutingUri' in text:
        errors.append(
            'Access/Verilic/JarvisDirectAiTransport.cs: direct runtime transport must never contact Verilic.'
        )
else:
    errors.append('Access/Verilic/JarvisDirectAiTransport.cs: direct provider runtime transport is missing.')

# Compatibility dispatcher may retain its historical class name, but it may
# only read the session registry and call the direct transport.
dispatcher = ROOT / 'Access' / 'Verilic' / 'VerilicAiMessagesClient.cs'
if dispatcher.exists():
    text = dispatcher.read_text(encoding='utf-8', errors='replace')
    if 'VerilicRuntimeConfiguration' in text or 'VerilicInstallationStateStore' in text:
        errors.append(
            'Access/Verilic/VerilicAiMessagesClient.cs: normal runtime must not load Verilic config/install state.'
        )
    if 'JarvisDirectAiTransport.SendAsync' not in text:
        errors.append(
            'Access/Verilic/VerilicAiMessagesClient.cs: dispatcher must execute through direct provider transport.'
        )

# UAT itself must not create a second independent provisioning client. HEALTH
# is routed through the JarvisShell Health path, which owns the allowed refresh.
uat = ROOT / 'UI' / 'JarvisShell.UatRunner.cs'
if uat.exists() and 'new JarvisAgentHealthProbe()' in uat.read_text(encoding='utf-8', errors='replace'):
    errors.append(
        'UI/JarvisShell.UatRunner.cs: UAT must not create an independent Verilic provisioning probe.'
    )

required_files = [
    ROOT / 'AGENTS.md',
    ROOT / 'Core' / 'JarvisAgentRuntimeSnapshot.cs',
    ROOT / 'Core' / 'JarvisAgentHealthProbe.cs',
    ROOT / 'Access' / 'Verilic' / 'JarvisDirectAiTransport.cs',
]
for path in required_files:
    if not path.exists():
        errors.append(f'{path.relative_to(ROOT).as_posix()}: required AI policy file is missing.')

if errors:
    print('AI PROVISIONING POLICY: FAIL')
    for error in errors:
        print(' - ' + error)
    sys.exit(1)

print('AI PROVISIONING POLICY: PASS')
print(' - no hardcoded concrete AI model ids/assignments in C#')
print(' - no normal-runtime Verilic AI message proxy')
print(' - BOOT and explicit HEALTH share the sole remote provisioning path')
print(' - agent session targets require Provider + Model + API credential')
print(' - explicit HEALTH supports atomic registry refresh')
print(' - Jarvis shutdown clears the session credential registry')
print(' - normal AI calls use direct provider transport')
