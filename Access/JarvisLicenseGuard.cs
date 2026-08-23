using System;
using System.Collections.Generic;
using Softone;

namespace S1Jarvis.Access
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisLicenseGuard
    //
    // Runtime access boundary for the Jarvis product family. The legacy Nexus
    // lookup remains cached for the transitional mode and, after Verilic
    // cutover, for opaque AI-routing resolution only.
    // ══════════════════════════════════════════════════════════════════════
    internal static class JarvisLicenseGuard
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, (AccessCheckResponse result, DateTime at)> _cache =
            new Dictionary<string, (AccessCheckResponse, DateTime)>();

        // Step 12F composition remains intentionally on the legacy provider
        // until the Verilic endpoint and activated installation are explicitly
        // ready for cutover. The SplitVerilicRuntimeAccessProvider is the
        // cutover implementation and never falls back after a Verilic deny.
        private static readonly IJarvisRuntimeAccessProvider _runtimeAccessProvider =
            new LegacyNexusRuntimeAccessProvider(CheckLegacyAccessSilent);

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
        /// response shape stays unchanged, but its licensing Allowed value now
        /// comes from the configured runtime access provider. This lets Step 12
        /// cut over the authority without a broad UI rewrite.
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
