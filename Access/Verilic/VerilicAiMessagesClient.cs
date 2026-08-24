using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Softone;
using S1Jarvis.Core;

namespace S1Jarvis.Access.Verilic
{
    /// <summary>
    /// Sends Jarvis provider requests through the signed Verilic messages
    /// endpoint. Provider credentials, authoritative AgentAccountRef and the
    /// configured model remain server-side. The client contributes only its
    /// activated installation proof, Soft1 identity and provider request JSON.
    /// </summary>
    internal sealed class VerilicAiMessagesClient
    {
        private static readonly HttpClient Http = new HttpClient();
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

        private sealed class MessagesRequest
        {
            public string ProductId { get; set; }
            public string InstallationId { get; set; }
            public string ProductVersion { get; set; }
            public string[] RequestedFeatures { get; set; }
            public string Soft1Serial { get; set; }
            public string CompanyCode { get; set; }
            public string BranchCode { get; set; }
            public string Soft1UserId { get; set; }
            public string ProviderRequestJson { get; set; }
        }

        private sealed class MessagesResponse
        {
            public bool Success { get; set; }
            public string ReasonCode { get; set; }
            public string ResponseText { get; set; }
            public bool CreditsExhausted { get; set; }
            public int UsageInputTokens { get; set; }
            public int UsageOutputTokens { get; set; }
            public string RawResponseJson { get; set; }
        }

        static VerilicAiMessagesClient()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        }

        public async Task<AgentProxyResponse> SendAsync(
            XSupport xSupport,
            string providerRequestJson,
            CancellationToken cancellationToken)
        {
            if (xSupport == null)
                return Failure("messages_identity_missing");
            if (string.IsNullOrWhiteSpace(providerRequestJson))
                return Failure("provider_request_invalid");

            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();
                if (configuration.Mode != VerilicRuntimeMode.Verilic ||
                    configuration.LicensingOrigin == null)
                    return Failure("messages_configuration_invalid");

                string productId = configuration.ResolveProductId(
                    JarvisProducts.Jarvis);

                var stateStore = new VerilicInstallationStateStore(
                    configuration.StateDirectory,
                    configuration.ProtectionScope);
                VerilicInstallationState state = stateStore.Load(
                    JarvisProducts.Jarvis);

                if (state == null || !state.ActivationCompleted ||
                    string.IsNullOrWhiteSpace(state.InstallationId) ||
                    state.InstallationId.StartsWith("pending_", StringComparison.Ordinal) ||
                    !string.Equals(state.VerilicProductId, productId, StringComparison.Ordinal) ||
                    !string.Equals(state.KeyAlgorithm, "ES256", StringComparison.Ordinal) ||
                    state.PrivateKeyMaterial == null || state.PrivateKeyMaterial.Length == 0)
                    return Failure("messages_installation_invalid");

                var info = xSupport.ConnectionInfo;
                if (info == null)
                    return Failure("messages_identity_missing");

                var body = new MessagesRequest
                {
                    ProductId = productId,
                    InstallationId = state.InstallationId,
                    ProductVersion = configuration.ProductVersion,
                    RequestedFeatures = new string[0],
                    Soft1Serial = info.SerialNum == null ? null : info.SerialNum.ToString(),
                    CompanyCode = info.CompanyId.ToString(),
                    BranchCode = info.BranchId.ToString(),
                    Soft1UserId = info.UserId.ToString(),
                    ProviderRequestJson = providerRequestJson
                };

                string json = JsonConvert.SerializeObject(body);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

                var origin = new Uri(
                    configuration.LicensingOrigin.GetLeftPart(UriPartial.Authority) + "/");
                var messagesUri = new Uri(origin, "api/jarvis-ai/messages");

                var proofState = new VerilicInstallationState
                {
                    ProductCode = productId,
                    InstallationId = state.InstallationId,
                    KeyAlgorithm = state.KeyAlgorithm,
                    PrivateKeyMaterial = state.PrivateKeyMaterial
                };

                using (var authorizer = new VerilicEs256RequestAuthorizer(proofState))
                using (var request = new HttpRequestMessage(HttpMethod.Post, messagesUri))
                using (var timeout = new CancellationTokenSource(Timeout))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token))
                {
                    var content = new ByteArrayContent(bodyBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                    {
                        CharSet = "utf-8"
                    };
                    request.Content = content;
                    authorizer.Authorize(request, bodyBytes);

                    using (HttpResponseMessage response = await Http.SendAsync(
                        request,
                        linked.Token))
                    {
                        string responseJson = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            return Failure(
                                "messages_http_" + ((int)response.StatusCode).ToString());

                        MessagesResponse result;
                        try
                        {
                            result = JsonConvert.DeserializeObject<MessagesResponse>(
                                responseJson);
                        }
                        catch (JsonException)
                        {
                            return Failure("messages_response_invalid");
                        }

                        if (result == null)
                            return Failure("messages_response_invalid");

                        return new AgentProxyResponse
                        {
                            Success = result.Success,
                            CreditsExhausted = result.CreditsExhausted,
                            ErrorMessage = result.Success
                                ? null
                                : BuildSafeErrorMessage(result.ReasonCode),
                            ResponseText = result.ResponseText,
                            RawResponseJson = result.RawResponseJson ?? string.Empty,
                            UsageInputTokens = result.UsageInputTokens,
                            UsageOutputTokens = result.UsageOutputTokens
                        };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw;

                return Failure("provider_timeout");
            }
            catch
            {
                return Failure("messages_transport_failed");
            }
        }

        private static AgentProxyResponse Failure(string reasonCode)
        {
            return new AgentProxyResponse
            {
                Success = false,
                CreditsExhausted = string.Equals(
                    reasonCode,
                    "provider_credits_exhausted",
                    StringComparison.Ordinal),
                ErrorMessage = BuildSafeErrorMessage(reasonCode),
                RawResponseJson = string.Empty
            };
        }

        private static string BuildSafeErrorMessage(string reasonCode)
        {
            string reason = string.IsNullOrWhiteSpace(reasonCode)
                ? "provider_unavailable"
                : reasonCode.Trim();

            switch (reason)
            {
                case "provider_auth_failed":
                    return "Ο AI provider απέρριψε τα διαπιστευτήρια.";
                case "provider_model_or_request_invalid":
                    return "Το επιλεγμένο AI model ή το αίτημα δεν είναι έγκυρο.";
                case "provider_credits_exhausted":
                    return "Το AI account αυτής της άδειας έχει εξαντλήσει τα credits του.";
                case "provider_rate_limited":
                    return "Ο AI provider έχει προσωρινό όριο κλήσεων. Δοκίμασε ξανά σε λίγο.";
                case "provider_timeout":
                    return "Ο AI provider δεν απάντησε εγκαίρως.";
                case "provider_chat_adapter_unavailable":
                    return "Ο provider δεν υποστηρίζεται ακόμη από το Jarvis chat.";
                case "provider_credential_unavailable":
                    return "Τα διαπιστευτήρια του AI provider δεν είναι διαθέσιμα.";
                case "provider_model_missing":
                    return "Δεν έχει οριστεί AI model για αυτή τη ρύθμιση.";
                case "agent_account_unavailable":
                    return "Το AI agent account δεν είναι διαθέσιμο.";
                case "provider_upstream_error":
                    return "Ο AI provider επέστρεψε προσωρινό σφάλμα.";
                default:
                    if (reason.StartsWith("routing_", StringComparison.Ordinal) ||
                        reason.StartsWith("licence_", StringComparison.Ordinal) ||
                        reason.StartsWith("proof_", StringComparison.Ordinal) ||
                        reason.StartsWith("messages_", StringComparison.Ordinal))
                        return "Η ασφαλής δρομολόγηση AI δεν είναι διαθέσιμη (" + reason + ").";

                    return "Η κλήση προς τον AI provider απέτυχε (" + reason + ").";
            }
        }
    }
}
