using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// NativeS1 named-user runtime identity. All four values are mandatory for
    /// /api/licensing/v1/verify; the client never substitutes installation or
    /// device identity for the active Soft1 session identity.
    /// </summary>
    internal sealed class VerilicRuntimeContext
    {
        [JsonProperty("soft1Serial")]
        public string Soft1Serial { get; set; }

        [JsonProperty("companyCode")]
        public string CompanyCode { get; set; }

        [JsonProperty("branchCode")]
        public string BranchCode { get; set; }

        [JsonProperty("soft1UserId")]
        public string Soft1UserId { get; set; }
    }

    /// <summary>
    /// Public NativeS1 startup verification request. Keep this DTO deliberately
    /// minimal because the public endpoint rejects unknown request members.
    /// </summary>
    internal sealed class VerilicVerifyLicenceRequest
    {
        [JsonProperty("productId")]
        public string ProductId { get; set; }

        [JsonProperty("productVersion")]
        public string ProductVersion { get; set; }

        [JsonProperty("runtimeContext")]
        public VerilicRuntimeContext RuntimeContext { get; set; }
    }

    internal sealed class VerilicVerifyProductResult
    {
        [JsonProperty("productId")]
        public string ProductId { get; set; }

        [JsonProperty("allowed")]
        public bool Allowed { get; set; }

        [JsonProperty("validFromUtc")]
        public DateTime? ValidFromUtc { get; set; }

        [JsonProperty("validUntilUtc")]
        public DateTime? ValidUntilUtc { get; set; }

        [JsonProperty("runtimeReady")]
        public bool RuntimeReady { get; set; }

        [JsonProperty("runtimeReasonCode")]
        public string RuntimeReasonCode { get; set; }

        [JsonProperty("runtimeMessage")]
        public string RuntimeMessage { get; set; }

        [JsonProperty("contractIds")]
        public List<string> ContractIds { get; set; } = new List<string>();

        // The integration guide intentionally treats the concrete AI
        // configuration payload as server-owned runtime material. Preserve it
        // losslessly until the dedicated decryptor consumes the documented
        // envelope; never choose an entry client-side based on array order.
        [JsonProperty("aiConfigurations")]
        public List<JObject> AiConfigurations { get; set; } = new List<JObject>();
    }

    /// <summary>
    /// Public NativeS1 verification wrapper. An HTTP success is not itself an
    /// authorization decision: Allowed and the matching product entry must both
    /// be validated by the client.
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

        [JsonProperty("validUntilUtc")]
        public DateTime? ValidUntilUtc { get; set; }

        [JsonProperty("products")]
        public List<VerilicVerifyProductResult> Products { get; set; } =
            new List<VerilicVerifyProductResult>();

        [JsonIgnore]
        public int? HttpStatusCode { get; set; }

        [JsonIgnore]
        public int? RetryAfterSeconds { get; set; }

        public VerilicVerifyProductResult FindRequestedProduct(string productId)
        {
            if (string.IsNullOrWhiteSpace(productId) || Products == null)
                return null;

            VerilicVerifyProductResult match = null;
            foreach (VerilicVerifyProductResult product in Products)
            {
                if (product == null ||
                    !string.Equals(product.ProductId, productId, StringComparison.Ordinal))
                    continue;

                // Duplicate product entries make the response ambiguous and must
                // not be resolved by first-record ordering.
                if (match != null)
                    return null;
                match = product;
            }

            return match;
        }

        public static VerilicVerifyLicenceResult Deny(
            string reasonCode,
            int? httpStatusCode = null,
            int? retryAfterSeconds = null)
        {
            return new VerilicVerifyLicenceResult
            {
                Allowed = false,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "verification_failed"
                    : reasonCode,
                HttpStatusCode = httpStatusCode,
                RetryAfterSeconds = retryAfterSeconds
            };
        }
    }
}
