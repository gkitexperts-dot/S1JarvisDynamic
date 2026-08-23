using System;
using System.Collections.Generic;
using Softone;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.Access
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisLicenseGuard
    //
    // Runtime access boundary for the Jarvis product family. Legacy mode keeps
    // the existing combined Nexus lookup. Verilic mode composes authoritative
    // licensing plus signed AI-routing resolution with no legacy fallback.
    // ══════════════════════════════════════════════════════════════════════
    internal static class JarvisLicenseGuard
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, (AccessCheckResponse result, DateTime at)> _cache =
            new Dictionary<string, (AccessCheckResponse, DateTime)>();

        private static readonly IJarvisRuntimeAccessProvider _runtimeAccessProvider =
            CreateRuntimeAccessProvider();

        private static IJarvisRuntimeAccessProvider CreateRuntimeAccessProvider()
        {
            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();

                if (configuration.Mode == VerilicRuntimeMode.Legacy)
                    return new LegacyNexusRuntimeAccessProvider(
                        CheckLegacyAccessSilent);

                var stateStore = new VerilicInstallationStateStore(
                    configuration.StateDirectory,
                    configuration.ProtectionScope);

                IVerilicRuntimeLicenceProvider licensing =
                    new VerilicRuntimeLicenceProvider(
                        stateStore,
                        configuration.VerificationUri,
                        configuration.ProductVersion,
                        configuration.ResolveProductId);

                IVerilicRuntimeAiRoutingProvider routing =
                    new VerilicRuntimeAiRoutingProvider(
                        stateStore,
                        configuration.RoutingUri,
                        configuration.ProductVersion,
                        configuration.ResolveProductId);

                return new SplitVerilicRuntimeAccessProvider(
                    licensing,
                    routing);
            }
            catch
            {
                // If Verilic mode was explicitly requested but its configuration
                // cannot be composed, never fall back to legacy authorization.
                return new FailClosedRuntimeAccessProvider(
                    "runtime_configuration_invalid");
            }
        }

        public static JarvisRuntimeAccessResult CheckRuntimeAccessSilent(
            XSupport xSupport,
            string productCode = null)
        {
            try
            {
                return _runtimeAccessProvider.Check(
                    xSupport,
                    productCode ?? JarvisProducts.Jarvis);
            }
            catch
            {
                string effectiveProduct =
                    productCode ?? JarvisProducts.Jarvis;
                return JarvisRuntimeAccessResult.Create(
                    JarvisLicenceAccessDecision.Deny(
                        effectiveProduct,
                        "runtime_access_failed"),
                    JarvisAgentRoutingDecision.None());
            }
        }

        /// <summary>
        /// Compatibility API for the existing JarvisShell call sites. The
        /// response shape stays unchanged, but its licensing Allowed value comes
        /// from the configured runtime access provider.
        /// </summary>
        public static AccessCheckResponse CheckAccessSilent(
            XSupport xSupport,
            string toolName = null)
        {
            return CheckRuntimeAccessSilent(
                    xSupport,
                    toolName ?? JarvisProducts.Jarvis)
                .ToLegacyCompatibilityResponse();
        }

        private static AccessCheckResponse CheckLegacyAccessSilent(
            XSupport xSupport,
            string toolName)
        {
            if (xSupport == null)
                return AccessCheckResponse.Deny(
                    toolName,
                    "Αποτυχία ελέγχου άδειας χρήσης.");

            toolName = toolName ?? AccessConfig.ToolName;
            var info = xSupport.ConnectionInfo;

            string key = $"{info.SerialNum}|{info.CompanyId}|{info.BranchId}|{info.UserId}|{toolName}";

            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var cached) &&
                    DateTime.Now - cached.at < CacheDuration)
                {
                    return cached.result;
                }

                var result = DoLegacyCheck(xSupport, toolName);
                _cache[key] = (result, DateTime.Now);
                return result;
            }
        }

        private static AccessCheckResponse DoLegacyCheck(
            XSupport xSupport,
            string toolName)
        {
            try
            {
                var info = xSupport.ConnectionInfo;

                IAccessControl access = new HttpAccessControl(
                    AccessConfig.ServiceUrl,
                    AccessConfig.ClientKey);

                return access.CheckAccess(new AccessCheckRequest
                {
                    Serial = info.SerialNum?.ToString(),
                    CompanyCode = info.CompanyId.ToString(),
                    BranchCode = info.BranchId.ToString(),
                    Soft1UserId = info.UserId.ToString(),
                    ToolName = toolName,
                });
            }
            catch
            {
                // Do not surface transport/internal exception details to the UI.
                return AccessCheckResponse.Deny(
                    toolName,
                    "Αποτυχία ελέγχου άδειας χρήσης.");
            }
        }

        public static string BuildMessage(AccessCheckResponse result)
        {
            string msg = string.IsNullOrWhiteSpace(result.Message)
                ? "Η άδεια χρήσης έχει λήξει. Παρακαλώ ανανεώστε μέσω του Μεταπωλητή σας."
                : result.Message;

            if (!string.IsNullOrWhiteSpace(result.ValidUntil))
                msg += $" (Ισχύς έως: {result.ValidUntil})";

            return msg;
        }
    }
}
