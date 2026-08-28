using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace S1Jarvis.Core
{
    internal sealed class JarvisToolReconciliationResult
    {
        public int RuntimeDefinitionCount { get; set; }
        public int RegistryCount { get; set; }
        public string[] Issues { get; set; }
        public string[] Warnings { get; set; }
        public bool Success { get { return Issues == null || Issues.Length == 0; } }
    }

    /// <summary>
    /// Non-throwing reconciliation between the real runtime tool-definition
    /// surface and JarvisToolRegistry. This is intentionally diagnostic only:
    /// it does not drive routing or change which tools are exposed.
    /// </summary>
    internal static class JarvisToolInventoryReconciler
    {
        private static readonly Type[] RuntimeToolOwners =
        {
            typeof(JarvisTools),
            typeof(JarvisEmailAccess),
            typeof(JarvisCourier),
            typeof(JarvisItems)
        };

        private static readonly HashSet<string> IntentionalRoutingOnlyCapabilities =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Help"
            };

        public static JarvisToolReconciliationResult RunAndLog()
        {
            JarvisToolReconciliationResult result = Run();
            try
            {
                if (result.Success)
                {
                    DebugLog.Log(
                        "[TOOL-INVENTORY] reconciliation OK runtimeDefinitions=" +
                        result.RuntimeDefinitionCount +
                        " registry=" + result.RegistryCount +
                        " warnings=" + (result.Warnings == null ? 0 : result.Warnings.Length));

                    if (result.Warnings != null)
                        foreach (string warning in result.Warnings)
                            DebugLog.Log("[TOOL-INVENTORY] warning: " + warning);
                }
                else
                {
                    DebugLog.Log(
                        "[TOOL-INVENTORY] reconciliation FAILED runtimeDefinitions=" +
                        result.RuntimeDefinitionCount +
                        " registry=" + result.RegistryCount +
                        " issues=" + string.Join(" | ", result.Issues ?? new string[0]));
                }
            }
            catch
            {
                // Inventory diagnostics must never interfere with Jarvis startup.
            }
            return result;
        }

        public static JarvisToolReconciliationResult Run()
        {
            var issues = new List<string>();
            var warnings = new List<string>();
            var runtimeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (Type owner in RuntimeToolOwners)
                    DiscoverDefinitions(owner, runtimeNames, issues);

                string[] registryNames = JarvisToolRegistry.AllTools
                    .Select(x => x.Name)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                foreach (string runtimeName in runtimeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    if (!registryNames.Contains(runtimeName, StringComparer.OrdinalIgnoreCase))
                        issues.Add("Runtime tool is not registered: " + runtimeName);

                foreach (string registryName in registryNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    if (!runtimeNames.Contains(registryName))
                        issues.Add("Registry tool has no runtime definition: " + registryName);

                foreach (string metadataIssue in JarvisToolRegistry.ValidateInventory())
                {
                    const string routePrefix = "Route capability without registered tool: ";
                    if (metadataIssue.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string capability = metadataIssue.Substring(routePrefix.Length).Trim();
                        if (IntentionalRoutingOnlyCapabilities.Contains(capability))
                        {
                            warnings.Add(metadataIssue + " (intentional routing-only capability)");
                            continue;
                        }
                    }
                    issues.Add(metadataIssue);
                }

                return new JarvisToolReconciliationResult
                {
                    RuntimeDefinitionCount = runtimeNames.Count,
                    RegistryCount = registryNames.Length,
                    Issues = issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                };
            }
            catch (Exception ex)
            {
                issues.Add("Reconciliation failed safely: " + ex.GetType().Name + " - " + ex.Message);
                return new JarvisToolReconciliationResult
                {
                    RuntimeDefinitionCount = runtimeNames.Count,
                    RegistryCount = JarvisToolRegistry.AllTools.Count,
                    Issues = issues.ToArray(),
                    Warnings = warnings.ToArray()
                };
            }
        }

        private static void DiscoverDefinitions(
            Type owner,
            HashSet<string> runtimeNames,
            List<string> issues)
        {
            BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (FieldInfo field in owner.GetFields(flags))
            {
                if (!field.Name.EndsWith("ToolDefinition", StringComparison.Ordinal))
                    continue;
                AddDefinition(owner, field.Name, field.GetValue(null), runtimeNames, issues);
            }

            foreach (PropertyInfo property in owner.GetProperties(flags))
            {
                if (!property.Name.EndsWith("ToolDefinition", StringComparison.Ordinal) ||
                    property.GetIndexParameters().Length != 0)
                    continue;
                AddDefinition(owner, property.Name, property.GetValue(null, null), runtimeNames, issues);
            }
        }

        private static void AddDefinition(
            Type owner,
            string memberName,
            object definition,
            HashSet<string> runtimeNames,
            List<string> issues)
        {
            if (definition == null)
            {
                issues.Add(owner.Name + "." + memberName + " is null");
                return;
            }

            PropertyInfo nameProperty = definition.GetType().GetProperty(
                "name",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            string name = nameProperty == null
                ? null
                : Convert.ToString(nameProperty.GetValue(definition, null));

            if (string.IsNullOrWhiteSpace(name))
            {
                issues.Add(owner.Name + "." + memberName + " has no readable tool name");
                return;
            }

            if (!runtimeNames.Add(name.Trim()))
                issues.Add("Duplicate runtime tool definition: " + name.Trim());
        }
    }
}
