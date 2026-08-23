using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Net48 transport model matching Verilic.Api's VerifyLicenceRequest.
    /// This is request data only; it is not an authorization decision.
    /// </summary>
    internal sealed class VerilicVerifyLicenceRequest
    {
        [JsonProperty("productId")]
        public string ProductId { get; set; }

        [JsonProperty("installationId")]
        public string InstallationId { get; set; }

        [JsonProperty("productVersion")]
        public string ProductVersion { get; set; }

        [JsonProperty("requestedFeatures")]
        public List<string> RequestedFeatures { get; set; } = new List<string>();
    }

    /// <summary>
    /// Net48 transport model matching Verilic.Api's VerifyLicenceResult.
    /// Only the server may set Allowed=true. Client-side transport failures
    /// are represented as explicit fail-closed results.
    /// </summary>
    internal sealed class VerilicVerifyLicenceResult
    {
        [JsonProperty("allowed")]
        public bool Allowed { get; set; }

        [JsonProperty("reasonCode")]
        public string ReasonCode { get; set; }

        [JsonProperty("decisionId")]
        public string DecisionId { get; set; }

        [JsonProperty("productId")]
        public string ProductId { get; set; }

        [JsonProperty("installationId")]
        public string InstallationId { get; set; }

        [JsonProperty("validUntilUtc")]
        public DateTime? ValidUntilUtc { get; set; }

        [JsonProperty("refreshAfterUtc")]
        public DateTime? RefreshAfterUtc { get; set; }

        [JsonProperty("features")]
        public List<string> Features { get; set; } = new List<string>();

        [JsonProperty("limits")]
        public Dictionary<string, long> Limits { get; set; } =
            new Dictionary<string, long>();

        [JsonProperty("signedLicenceToken")]
        public string SignedLicenceToken { get; set; }

        public static VerilicVerifyLicenceResult Deny(string reasonCode)
        {
            return new VerilicVerifyLicenceResult
            {
                Allowed = false,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "verification_failed"
                    : reasonCode
            };
        }
    }
}
