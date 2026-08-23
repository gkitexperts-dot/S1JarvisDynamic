using System;
using System.Collections.Generic;

namespace S1Jarvis.Access.Verilic
{
    internal interface IVerilicRuntimeLicenceProvider
    {
        JarvisLicenceAccessDecision Check(string productCode);
    }

    /// <summary>
    /// Authoritative Verilic runtime licensing check for an already activated
    /// installation. The commercial Jarvis product code is mapped explicitly to
    /// the registered Verilic ProductId; the two identities are never assumed to
    /// be equal.
    ///
    /// Successful server decisions may be cached in memory only until the
    /// earliest server/client re-verification bound. Denials are never cached.
    /// </summary>
    internal sealed class VerilicRuntimeLicenceProvider :
        IVerilicRuntimeLicenceProvider
    {
        private static readonly TimeSpan MaximumAllowCacheDuration =
            TimeSpan.FromMinutes(5);

        private readonly VerilicInstallationStateStore _stateStore;
        private readonly Uri _verificationUri;
        private readonly string _productVersion;
        private readonly Func<string, string> _productIdResolver;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<string, CachedAllowDecision> _allowCache =
            new Dictionary<string, CachedAllowDecision>(StringComparer.Ordinal);

        public VerilicRuntimeLicenceProvider(
            VerilicInstallationStateStore stateStore,
            Uri verificationUri,
            string productVersion,
            Func<string, string> productIdResolver)
        {
            _stateStore = stateStore ??
                throw new ArgumentNullException(nameof(stateStore));

            if (verificationUri == null ||
                !verificationUri.IsAbsoluteUri ||
                !string.Equals(
                    verificationUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "An absolute HTTPS Verilic verification URI is required.",
                    nameof(verificationUri));

            if (string.IsNullOrWhiteSpace(productVersion) ||
                productVersion.Length > 200)
                throw new ArgumentException(
                    "A product version is required.",
                    nameof(productVersion));
            if (productIdResolver == null)
                throw new ArgumentNullException(nameof(productIdResolver));

            _verificationUri = verificationUri;
            _productVersion = productVersion.Trim();
            _productIdResolver = productIdResolver;
        }

        public JarvisLicenceAccessDecision Check(string productCode)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_request_invalid");

            try
            {
                string productId = _productIdResolver(productCode);
                if (string.IsNullOrWhiteSpace(productId))
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "product_binding_invalid");

                VerilicInstallationState state =
                    _stateStore.Load(productCode);

                if (state == null || !state.ActivationCompleted)
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "installation_not_activated");

                if (!string.Equals(
                        state.VerilicProductId,
                        productId,
                        StringComparison.Ordinal))
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "product_binding_invalid");

                if (string.IsNullOrWhiteSpace(state.InstallationId) ||
                    state.InstallationId.StartsWith(
                        "pending_",
                        StringComparison.Ordinal))
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "installation_binding_invalid");

                if (!string.Equals(
                        state.KeyAlgorithm,
                        "ES256",
                        StringComparison.Ordinal) ||
                    state.PrivateKeyMaterial == null ||
                    state.PrivateKeyMaterial.Length == 0)
                    return JarvisLicenceAccessDecision.Deny(
                        productCode,
                        "installation_key_invalid");

                string cacheKey = BuildCacheKey(
                    productCode,
                    productId,
                    state.InstallationId);
                JarvisLicenceAccessDecision cached =
                    TryGetCachedAllow(cacheKey, DateTime.UtcNow);
                if (cached != null)
                    return cached;

                // The existing net48 authorizer snapshots its proof ProductId
                // from ProductCode. Give it a proof-only view containing the
                // registered Verilic ProductId while keeping persistent state
                // keyed by the stable Jarvis commercial code.
                var proofState = new VerilicInstallationState
                {
                    ProductCode = productId,
                    InstallationId = state.InstallationId,
                    KeyAlgorithm = state.KeyAlgorithm,
                    PrivateKeyMaterial = state.PrivateKeyMaterial
                };

                using (var authorizer =
                    new VerilicEs256RequestAuthorizer(proofState))
                {
                    IVerilicLicenceTransport transport =
                        new VerilicLicenceHttpTransport(
                            _verificationUri,
                            authorizer);

                    VerilicVerifyLicenceResult result =
                        transport.Verify(
                            new VerilicVerifyLicenceRequest
                            {
                                ProductId = productId,
                                InstallationId = state.InstallationId,
                                ProductVersion = _productVersion,
                                RequestedFeatures = new List<string>()
                            });

                    JarvisLicenceAccessDecision decision =
                        JarvisLicenceAccessDecision.FromVerilic(
                            productCode,
                            result);

                    if (decision.Allowed)
                        TryCacheAllow(
                            cacheKey,
                            decision,
                            result,
                            DateTime.UtcNow);

                    return decision;
                }
            }
            catch
            {
                return JarvisLicenceAccessDecision.Deny(
                    productCode,
                    "verification_failed");
            }
        }

        private JarvisLicenceAccessDecision TryGetCachedAllow(
            string cacheKey,
            DateTime nowUtc)
        {
            lock (_cacheLock)
            {
                CachedAllowDecision cached;
                if (!_allowCache.TryGetValue(cacheKey, out cached))
                    return null;

                if (cached.ExpiresAtUtc <= nowUtc)
                {
                    _allowCache.Remove(cacheKey);
                    return null;
                }

                return cached.Decision;
            }
        }

        private void TryCacheAllow(
            string cacheKey,
            JarvisLicenceAccessDecision decision,
            VerilicVerifyLicenceResult result,
            DateTime nowUtc)
        {
            if (decision == null || !decision.Allowed || result == null ||
                !result.Allowed || !result.RefreshAfterUtc.HasValue)
                return;

            DateTime refreshAfterUtc =
                result.RefreshAfterUtc.Value.ToUniversalTime();
            if (refreshAfterUtc <= nowUtc)
                return;

            DateTime expiresAtUtc = refreshAfterUtc;

            if (result.ValidUntilUtc.HasValue)
            {
                DateTime validUntilUtc =
                    result.ValidUntilUtc.Value.ToUniversalTime();
                if (validUntilUtc <= nowUtc)
                    return;
                if (validUntilUtc < expiresAtUtc)
                    expiresAtUtc = validUntilUtc;
            }

            DateTime clientMaximumUtc =
                nowUtc.Add(MaximumAllowCacheDuration);
            if (clientMaximumUtc < expiresAtUtc)
                expiresAtUtc = clientMaximumUtc;

            if (expiresAtUtc <= nowUtc)
                return;

            lock (_cacheLock)
            {
                _allowCache[cacheKey] = new CachedAllowDecision
                {
                    Decision = decision,
                    ExpiresAtUtc = expiresAtUtc
                };
            }
        }

        private static string BuildCacheKey(
            string productCode,
            string productId,
            string installationId)
        {
            return productCode + "\n" + productId + "\n" + installationId;
        }

        private sealed class CachedAllowDecision
        {
            public JarvisLicenceAccessDecision Decision { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
        }
    }
}
