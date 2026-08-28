using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace S1Jarvis.Access.Verilic
{
    internal sealed class VerilicProvisioningImportResult
    {
        public bool Success { get; set; }
        public string ReasonCode { get; set; }
        public string Message { get; set; }
        public VerilicLocalConfiguration Configuration { get; set; }
    }

    internal sealed class VerilicProvisioningClient
    {
        private const string ProductCode = "S1JARVIS";
        private static readonly HttpClient Http = new HttpClient();
        private readonly Uri _importUri;
        private readonly TimeSpan _timeout;

        static VerilicProvisioningClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public VerilicProvisioningClient(
            string provisioningOrigin = "https://verilic.gr",
            int timeoutSeconds = 15)
        {
            Uri origin;
            if (string.IsNullOrWhiteSpace(provisioningOrigin) ||
                !Uri.TryCreate(provisioningOrigin.Trim(), UriKind.Absolute, out origin) ||
                !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Verilic provisioning requires an absolute HTTPS origin.",
                    nameof(provisioningOrigin));

            if (timeoutSeconds <= 0 || timeoutSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            var normalizedOrigin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/");
            _importUri = new Uri(normalizedOrigin, "api/licensing/v1/provisioning/import");
            _timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        public VerilicProvisioningImportResult Import(string provisioningCode)
        {
            if (string.IsNullOrWhiteSpace(provisioningCode))
                return Denied("provisioning_code_missing", "Provisioning code is required.");

            try
            {
                return Task.Run(() => ImportCoreAsync(provisioningCode.Trim()))
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                return Denied(
                    "provisioning_transport_failed",
                    "Unable to contact Verilic provisioning.");
            }
        }

        private async Task<VerilicProvisioningImportResult> ImportCoreAsync(
            string provisioningCode)
        {
            var body = new ProvisioningRequest
            {
                ProvisioningCode = provisioningCode,
                ProductCode = ProductCode
            };

            string json = JsonConvert.SerializeObject(body);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(json);

            using (var request = new HttpRequestMessage(HttpMethod.Post, _importUri))
            {
                request.Content = new ByteArrayContent(bodyBytes);
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using (var cts = new CancellationTokenSource(_timeout))
                using (HttpResponseMessage response = await Http.SendAsync(request, cts.Token))
                {
                    string responseJson = await response.Content.ReadAsStringAsync();
                    ProvisioningResponse payload = null;

                    if (!string.IsNullOrWhiteSpace(responseJson))
                    {
                        try
                        {
                            payload = JsonConvert.DeserializeObject<ProvisioningResponse>(responseJson);
                        }
                        catch (JsonException)
                        {
                            payload = null;
                        }
                    }

                    if (!response.IsSuccessStatusCode || payload == null || !payload.Success)
                    {
                        return Denied(
                            payload == null || string.IsNullOrWhiteSpace(payload.ReasonCode)
                                ? "provisioning_not_allowed"
                                : payload.ReasonCode,
                            payload == null || string.IsNullOrWhiteSpace(payload.Message)
                                ? "Verilic did not allow provisioning for this licence."
                                : payload.Message);
                    }

                    VerilicLocalConfiguration configuration = MapConfiguration(payload);
                    VerilicRuntimeConfiguration.ValidateLocalConfiguration(configuration);

                    return new VerilicProvisioningImportResult
                    {
                        Success = true,
                        ReasonCode = "provisioning_imported",
                        Message = "Verilic provisioning configuration received.",
                        Configuration = configuration
                    };
                }
            }
        }

        private static VerilicLocalConfiguration MapConfiguration(
            ProvisioningResponse payload)
        {
            ProvisioningProductReference jarvis = RequireProduct(payload, "S1JARVIS");
            ProvisioningProductReference courier = RequireProduct(payload, "JARVISCOURIER");
            ProvisioningProductReference docReader = RequireProduct(payload, "JARVISDOCREADER");

            return new VerilicLocalConfiguration
            {
                Version = payload.ConfigurationVersion,
                Mode = payload.Mode,
                Origin = payload.Origin,
                StateDirectory = payload.StateDirectory,
                DpapiScope = payload.DpapiScope,
                VendorId = payload.VendorId,
                JarvisProductId = jarvis.ProductId,
                JarvisLicenceId = jarvis.LicenceId,
                CourierProductId = courier.ProductId,
                CourierLicenceId = courier.LicenceId,
                DocReaderProductId = docReader.ProductId,
                DocReaderLicenceId = docReader.LicenceId
            };
        }

        private static ProvisioningProductReference RequireProduct(
            ProvisioningResponse payload,
            string productCode)
        {
            ProvisioningProductReference product;
            if (payload.Products == null ||
                !payload.Products.TryGetValue(productCode, out product) ||
                product == null ||
                string.IsNullOrWhiteSpace(product.ProductId))
                throw new InvalidOperationException(
                    "Verilic provisioning response is missing " + productCode + ".");

            return product;
        }

        private static VerilicProvisioningImportResult Denied(
            string reasonCode,
            string message)
        {
            return new VerilicProvisioningImportResult
            {
                Success = false,
                ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
                    ? "provisioning_failed"
                    : reasonCode,
                Message = string.IsNullOrWhiteSpace(message)
                    ? "Verilic provisioning failed."
                    : message
            };
        }

        private sealed class ProvisioningRequest
        {
            [JsonProperty("provisioningCode")]
            public string ProvisioningCode { get; set; }

            [JsonProperty("productCode")]
            public string ProductCode { get; set; }
        }

        private sealed class ProvisioningResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("reasonCode")]
            public string ReasonCode { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }

            [JsonProperty("configurationVersion")]
            public int ConfigurationVersion { get; set; }

            [JsonProperty("mode")]
            public string Mode { get; set; }

            [JsonProperty("origin")]
            public string Origin { get; set; }

            [JsonProperty("vendorId")]
            public string VendorId { get; set; }

            [JsonProperty("products")]
            public Dictionary<string, ProvisioningProductReference> Products { get; set; }

            [JsonProperty("dpapiScope")]
            public string DpapiScope { get; set; }

            [JsonProperty("stateDirectory")]
            public string StateDirectory { get; set; }
        }

        private sealed class ProvisioningProductReference
        {
            [JsonProperty("productId")]
            public string ProductId { get; set; }

            [JsonProperty("licenceId")]
            public string LicenceId { get; set; }
        }
    }
}
