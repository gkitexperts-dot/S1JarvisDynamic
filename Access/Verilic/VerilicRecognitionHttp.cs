using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Access.Verilic
{
    // ══════════════════════════════════════════════════════════════════════
    // VerilicRecognitionHttp — POST σε Verilic endpoints με Recognition auth.
    // ΙΔΙΟ pattern με VerilicRuntimeLicenceProvider.Verify: credential από
    // VerilicRuntimeConfiguration.ResolveProductCredential(productCode),
    // headers X-Verilic-Recognition-Key-Id/Secret, productId στο body.
    // Χρησιμοποιείται από JarvisSkills και DrProfileService.
    // ══════════════════════════════════════════════════════════════════════
    internal static class VerilicRecognitionHttp
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        static VerilicRecognitionHttp() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13; }

        // Επιστρέφει response JObject ή null (fail-soft). Το body περιέχει
        // productId + runtime context + data (αν δοθεί).
        public static async Task<JObject> PostAsync(XSupport xSupport, string productCode, string relativePath, JObject data, string logTag)
        {
            try
            {
                VerilicRuntimeConfiguration configuration = VerilicRuntimeConfiguration.Load();
                VerilicProductRecognitionCredential credential = configuration.ResolveProductCredential(productCode);
                var connection = xSupport?.ConnectionInfo;
                if (credential == null || connection == null) return null;

                var body = new JObject
                {
                    ["productId"] = credential.ProductId,
                    ["soft1Serial"] = connection.SerialNum == null ? "" : connection.SerialNum.ToString(),
                    ["companyCode"] = connection.CompanyId.ToString(),
                    ["branchCode"] = connection.BranchId.ToString(),
                    ["soft1UserId"] = connection.UserId.ToString()
                };
                if (data != null) body["data"] = data;

                Uri uri = new Uri(configuration.LicensingOrigin, relativePath);
                using (var msg = new HttpRequestMessage(HttpMethod.Post, uri))
                {
                    msg.Headers.TryAddWithoutValidation("X-Verilic-Recognition-Key-Id", credential.KeyId);
                    msg.Headers.TryAddWithoutValidation("X-Verilic-Recognition-Secret", credential.Secret);
                    msg.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                    using (var resp = await Http.SendAsync(msg).ConfigureAwait(false))
                    {
                        string raw = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        S1Jarvis.Core.DebugLog.Log("[" + logTag + "] " + relativePath + " http=" + (int)resp.StatusCode);
                        if (!resp.IsSuccessStatusCode) return null;
                        return string.IsNullOrWhiteSpace(raw) ? new JObject() : JObject.Parse(raw);
                    }
                }
            }
            catch (Exception ex)
            {
                S1Jarvis.Core.DebugLog.Log("[" + logTag + "] " + relativePath + " failed: " + ex.Message);
                return null;
            }
        }
    }
}
