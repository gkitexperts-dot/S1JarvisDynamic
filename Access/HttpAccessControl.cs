using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace S1Jarvis.Access
{
    // Ίδιο μοτίβο με S1Courier.Access.HttpAccessControl - βλ. εκεί για το
    // πλήρες σκεπτικό (sync-over-async μέσω Task.Run, TLS1.2 explicit,
    // fail-closed).
    public class HttpAccessControl : IAccessControl
    {
        private readonly string _apiBaseUrl;
        private readonly string _apiClientKey;
        private readonly TimeSpan _timeout;

        private static readonly HttpClient _http = new HttpClient();

        static HttpAccessControl()
        {
            System.Net.ServicePointManager.SecurityProtocol |=
                System.Net.SecurityProtocolType.Tls12;
        }

        public HttpAccessControl(string apiBaseUrl, string apiClientKey, int timeoutSeconds = 15)
        {
            _apiBaseUrl   = apiBaseUrl?.TrimEnd('/')
                            ?? throw new ArgumentNullException(nameof(apiBaseUrl));
            _apiClientKey = apiClientKey ?? "";
            _timeout      = TimeSpan.FromSeconds(timeoutSeconds);
        }

        public AccessCheckResponse CheckAccess(AccessCheckRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            try
            {
                return Task.Run(() => CheckAccessCoreAsync(request))
                           .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return AccessCheckResponse.Deny(
                    request.ToolName,
                    "Αποτυχία επικοινωνίας με τον διακομιστή αδειών: " + ex.Message);
            }
        }

        private async Task<AccessCheckResponse> CheckAccessCoreAsync(AccessCheckRequest request)
        {
            string url  = _apiBaseUrl + "/access/check";
            string body = JsonConvert.SerializeObject(request);

            using (var msg = new HttpRequestMessage(HttpMethod.Post, url))
            {
                msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(_apiClientKey))
                    msg.Headers.Add("X-Client-Key", _apiClientKey);

                using (var cts = new System.Threading.CancellationTokenSource(_timeout))
                using (var resp = await _http.SendAsync(msg, cts.Token))
                {
                    string json = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                        return AccessCheckResponse.Deny(
                            request.ToolName,
                            $"Ο διακομιστής απάντησε με σφάλμα ({(int)resp.StatusCode}).");

                    var result = JsonConvert.DeserializeObject<AccessCheckResponse>(json);
                    if (result == null)
                        return AccessCheckResponse.Deny(request.ToolName, "Άκυρη απάντηση διακομιστή.");

                    if (string.IsNullOrEmpty(result.ToolName)) result.ToolName = request.ToolName;
                    return result;
                }
            }
        }
    }
}
