using System;
using System.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Resolves a capability to its internal owner without duplicating routing
    /// knowledge in the semantic planner.
    ///
    /// Resolution order:
    /// 1. Explicit canonical route from JarvisToolRegistry.
    /// 2. If no explicit route exists, infer the owner only when every tool
    ///    advertising the capability has one unambiguous OwnerAgent.
    ///
    /// This allows granular capabilities such as Export, EmailWrite,
    /// CalendarWrite, CourierRead and CourierWrite to remain meaningful task
    /// capabilities without forcing the planner to know agent names.
    /// </summary>
    internal static class JarvisCapabilityResolver
    {
        internal static string ResolveOwner(string capability)
        {
            if (string.IsNullOrWhiteSpace(capability))
                return null;

            string normalized = capability.Trim();
            string explicitOwner = JarvisToolRegistry.ResolveOwnerForCapability(normalized);
            if (!string.IsNullOrWhiteSpace(explicitOwner))
                return explicitOwner;

            string[] owners = JarvisToolRegistry.ForCapability(normalized)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.OwnerAgent))
                .Select(x => x.OwnerAgent.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return owners.Length == 1 ? owners[0] : null;
        }

        internal static bool IsAmbiguous(string capability)
        {
            if (string.IsNullOrWhiteSpace(capability))
                return false;

            string normalized = capability.Trim();
            if (!string.IsNullOrWhiteSpace(JarvisToolRegistry.ResolveOwnerForCapability(normalized)))
                return false;

            return JarvisToolRegistry.ForCapability(normalized)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.OwnerAgent))
                .Select(x => x.OwnerAgent.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count() > 1;
        }
    }
}
