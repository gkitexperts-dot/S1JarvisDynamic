using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Softone;
using S1Jarvis.Core;

namespace S1Jarvis.Access.Verilic
{
    internal sealed class VerilicRuntimeAuthorization
    {
        internal AccessCheckResponse Access { get; set; }
        internal string ClientKey { get; set; }
        internal DateTime ExpiresAtUtc { get; set; }
    }

    internal static class VerilicRuntimeSession
    {
        internal const int ApiUsernameParamCode = 500060;
        internal const int ApiValueParamCode = 500061;

        private static readonly Uri Origin = new Uri("https://verilic.gr/");
        private static readonly Uri AuthUri = new Uri(Origin, "api/runtime/auth");
        private static readonly Uri AccessUri = new Uri(Origin, "access/check");
        private static readonly Uri AgentProxyUri = new Uri(Origin, "agent/vision");
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly SemaphoreSlim AuthGate = new SemaphoreSlim(1, 1);
        private static readonly object Sync = new object();

        private static string _clientKey;
        private static DateTime _expiresAtUtc;
        private static string _credentialFingerprint;
        private static string _agentAccountRef;
        private static string _contractToolName;

        static VerilicRuntimeSession()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        internal static Uri AgentUri => AgentProxyUri;

        internal static string CurrentAgentAccountRef
        {
            get { lock (Sync) return _agentAccountRef; }
        }

        internal static async Task<VerilicRuntimeAuthorization> AuthorizeAsync(
            XSupport xSupport,
            string toolName,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));

            string clientKey = await EnsureClientKeyAsync(xSupport, cancellationToken).ConfigureAwait(false);
            var info = xSupport.ConnectionInfo;
            if (info == null) throw new InvalidOperationException("Soft1 connection identity is unavailable.");

            string authenticatedTool;
            lock (Sync) authenticatedTool = _contractToolName;
            if (string.IsNullOrWhiteSpace(authenticatedTool))
                authenticatedTool = toolName;
            if (string.IsNullOrWhiteSpace(authenticatedTool))
                throw new InvalidOperationException("Authenticated contract tool is unavailable.");

            var payload = new AccessCheckRequest
            {
                Serial = info.SerialNum == null ? "" : info.SerialNum.ToString(),
                CompanyCode = info.CompanyId.ToString(),
                BranchCode = info.BranchId.ToString(),
                Soft1UserId = info.UserId.ToString(),
                ToolName = authenticatedTool.Trim()
            };

            AccessCheckResponse access = await SendAccessCheckAsync(payload, clientKey, cancellationToken).ConfigureAwait(false);
            if (access != null && access.Allowed && !string.IsNullOrWhiteSpace(access.AgentAccountRef))
            {
                lock (Sync) _agentAccountRef = access.AgentAccountRef.Trim();
            }

            return new VerilicRuntimeAuthorization
            {
                Access = access,
                ClientKey = clientKey,
                ExpiresAtUtc = ReadExpiry()
            };
        }

        internal static async Task<string> EnsureClientKeyAsync(
            XSupport xSupport,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string apiUsername = ReadRequiredStringParam(xSupport, ApiUsernameParamCode, "Verilic ApiUsername");
            string apiValue = ReadRequiredStringParam(xSupport, ApiValueParamCode, "Verilic ApiValue");
            string fingerprint = apiUsername + "\n" + apiValue;

            lock (Sync)
            {
                if (!string.IsNullOrWhiteSpace(_clientKey) &&
                    DateTime.UtcNow.AddMinutes(1) < _expiresAtUtc &&
                    string.Equals(_credentialFingerprint, fingerprint, StringComparison.Ordinal))
                    return _clientKey;
            }

            await AuthGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (Sync)
                {
                    if (!string.IsNullOrWhiteSpace(_clientKey) &&
                        DateTime.UtcNow.AddMinutes(1) < _expiresAtUtc &&
                        string.Equals(_credentialFingerprint, fingerprint, StringComparison.Ordinal))
                        return _clientKey;
                }

                var body = JsonConvert.SerializeObject(new
                {
                    apiUsername = apiUsername,
                    apiValue = apiValue
                });

                using (var request = new HttpRequestMessage(HttpMethod.Post, AuthUri))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        RuntimeAuthResponse auth = null;
                        try { auth = JsonConvert.DeserializeObject<RuntimeAuthResponse>(json); }
                        catch { }

                        if (!response.IsSuccessStatusCode || auth == null || !auth.Authenticated || string.IsNullOrWhiteSpace(auth.ClientKey))
                        {
                            string reason = auth == null || string.IsNullOrWhiteSpace(auth.ReasonCode)
                                ? "runtime_auth_failed"
                                : auth.ReasonCode.Trim();
                            throw new InvalidOperationException(reason);
                        }
                        if (string.IsNullOrWhiteSpace(auth.ToolName))
                            throw new InvalidOperationException("runtime_auth_tool_missing");

                        DateTime expires = auth.ExpiresAtUtc.HasValue
                            ? auth.ExpiresAtUtc.Value.ToUniversalTime()
                            : DateTime.UtcNow.AddMinutes(20);

                        lock (Sync)
                        {
                            _clientKey = auth.ClientKey.Trim();
                            _expiresAtUtc = expires;
                            _credentialFingerprint = fingerprint;
                            _contractToolName = auth.ToolName.Trim();
                        }

                        DebugLog.Log("[VERILIC-AUTH] contract authentication accepted; tool=" +
                            auth.ToolName.Trim() + " expires=" + expires.ToString("o"));
                        return auth.ClientKey.Trim();
                    }
                }
            }
            finally
            {
                AuthGate.Release();
            }
        }

        internal static void Invalidate()
        {
            lock (Sync)
            {
                _clientKey = null;
                _expiresAtUtc = default(DateTime);
                _credentialFingerprint = null;
                _agentAccountRef = null;
                _contractToolName = null;
            }
        }

        private static async Task<AccessCheckResponse> SendAccessCheckAsync(
            AccessCheckRequest payload,
            string clientKey,
            CancellationToken cancellationToken)
        {
            string json = JsonConvert.SerializeObject(payload);
            using (var request = new HttpRequestMessage(HttpMethod.Post, AccessUri))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("X-Client-Key", clientKey);
                using (HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AccessCheckResponse result = null;
                    try { result = JsonConvert.DeserializeObject<AccessCheckResponse>(responseJson); }
                    catch { }

                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Invalidate();
                        throw new InvalidOperationException("runtime_client_key_rejected");
                    }
                    if (!response.IsSuccessStatusCode || result == null)
                        throw new InvalidOperationException("access_check_failed");

                    return result;
                }
            }
        }

        private static string ReadRequiredStringParam(XSupport xSupport, int code, string label)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            XTable table = xSupport.GetSQLDataSet(
                "SELECT TOP 1 ParamValueString FROM cccParams WHERE ParamCode=:1", code);
            string value = table == null || table.Count == 0 || table.Current["ParamValueString"] == null ||
                           table.Current["ParamValueString"] == DBNull.Value
                ? null
                : Convert.ToString(table.Current["ParamValueString"]);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(label + " is missing from cccParams (" + code.ToString() + ").");
            return value.Trim();
        }

        private static DateTime ReadExpiry()
        {
            lock (Sync) return _expiresAtUtc;
        }

        private sealed class RuntimeAuthResponse
        {
            [JsonProperty("authenticated")]
            public bool Authenticated { get; set; }

            [JsonProperty("clientKey")]
            public string ClientKey { get; set; }

            [JsonProperty("toolName")]
            public string ToolName { get; set; }

            [JsonProperty("expiresAtUtc")]
            public DateTime? ExpiresAtUtc { get; set; }

            [JsonProperty("reasonCode")]
            public string ReasonCode { get; set; }
        }
    }
}
