using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    internal sealed class JarvisTraderRoleDescriptor
    {
        internal JarvisTraderRoleDescriptor(int sodType, string role, string objectName, bool incoming, bool outgoing)
        {
            SodType = sodType;
            Role = role ?? string.Empty;
            ObjectName = objectName ?? string.Empty;
            IncomingCandidate = incoming;
            OutgoingCandidate = outgoing;
        }

        internal int SodType { get; private set; }
        internal string Role { get; private set; }
        internal string ObjectName { get; private set; }
        internal bool IncomingCandidate { get; private set; }
        internal bool OutgoingCandidate { get; private set; }
    }

    /// <summary>
    /// Authoritative business-entity semantics shared by orchestration, agents
    /// and deterministic resolvers. Models may interpret user language, but they
    /// must never invent Soft1 entity discriminators or Designer object names.
    ///
    /// Only mappings already confirmed elsewhere in the repository are allowed
    /// here. Unknown roles/types remain unknown; fail closed instead of guessing.
    /// </summary>
    internal static class JarvisBusinessEntityCatalog
    {
        private static readonly JarvisTraderRoleDescriptor[] TraderRoles =
        {
            new JarvisTraderRoleDescriptor(12, "Supplier", "SUPPLIER", true, false),
            new JarvisTraderRoleDescriptor(13, "Customer", "CUSTOMER", false, true),
            new JarvisTraderRoleDescriptor(15, "Debtor", "DEBTOR", false, true),
            new JarvisTraderRoleDescriptor(16, "Creditor", "CREDITOR", true, false)
        };

        internal static IReadOnlyList<JarvisTraderRoleDescriptor> AllTraderRoles
        {
            get { return TraderRoles; }
        }

        internal static JarvisTraderRoleDescriptor FindTraderRole(int sodType)
        {
            return TraderRoles.FirstOrDefault(x => x.SodType == sodType);
        }

        internal static JarvisTraderRoleDescriptor FindTraderRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return null;
            return TraderRoles.FirstOrDefault(x => string.Equals(x.Role, role.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        internal static string RoleName(int sodType)
        {
            JarvisTraderRoleDescriptor descriptor = FindTraderRole(sodType);
            return descriptor == null ? "Other" : descriptor.Role;
        }

        internal static bool IsIncomingTraderRole(int sodType)
        {
            JarvisTraderRoleDescriptor descriptor = FindTraderRole(sodType);
            return descriptor != null && descriptor.IncomingCandidate;
        }

        internal static bool IsOutgoingTraderRole(int sodType)
        {
            JarvisTraderRoleDescriptor descriptor = FindTraderRole(sodType);
            return descriptor != null && descriptor.OutgoingCandidate;
        }

        internal static JObject BuildAgentContext()
        {
            var roles = new JArray();
            foreach (JarvisTraderRoleDescriptor role in TraderRoles)
            {
                roles.Add(new JObject
                {
                    ["role"] = role.Role,
                    ["sodType"] = role.SodType,
                    ["objectName"] = role.ObjectName,
                    ["incomingCandidate"] = role.IncomingCandidate,
                    ["outgoingCandidate"] = role.OutgoingCandidate
                });
            }

            return new JObject
            {
                ["TRDR"] = new JObject
                {
                    ["identityField"] = "TRDR",
                    ["roleDiscriminator"] = "SODTYPE",
                    ["roles"] = roles,
                    ["policy"] = "Use only registered role discriminators. Unknown SODTYPE/object mappings must never be invented."
                },
                ["FINDOC"] = new JObject
                {
                    ["identityField"] = "FINDOC",
                    ["documentCodeField"] = "FINCODE",
                    ["transactionDateField"] = "TRNDATE",
                    ["traderForeignKey"] = "TRDR",
                    ["seriesField"] = "SERIES",
                    ["sourceField"] = "SOSOURCE"
                }
            };
        }
    }
}
