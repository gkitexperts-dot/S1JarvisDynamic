using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace S1Jarvis.Core.Courier
{
    internal sealed class JarvisGenikiCourierProvider : IJarvisCourierProvider
    {
        private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
        private static readonly XNamespace Ns = "http://voucher.taxydromiki.gr/JobServicesV2.asmx";

        private readonly JarvisCourierProviderConfig _config;
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private string _authKey;

        private const int ApiHardLimit = 50;

        public JarvisGenikiCourierProvider(JarvisCourierProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _baseUrl = (_config.ApiUrl ?? string.Empty).TrimEnd('/');
            _httpClient = new HttpClient();
        }

        public string ProviderName => _config.ProviderName;
        public int MaxVouchersPerBatch => (_config.MaxBatch > 0 && _config.MaxBatch < ApiHardLimit) ? _config.MaxBatch : ApiHardLimit;
        public bool SupportsCodChequeDate => false;
        public bool SupportsDeliveryTimeWindow => false;
        public bool SupportsDeliveryTimeRange => false;
        public bool SupportsSaturdayDelivery => true;
        public bool SupportsDeliveryDate => false;

        private async Task<XElement> CallSoapAsync(string methodName, params XElement[] parameters)
        {
            var envelope = new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", Soap.NamespaceName),
                new XElement(Soap + "Body", new XElement(Ns + methodName, parameters)));

            using (var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl))
            {
                request.Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
                request.Headers.Add("SOAPAction", "\"" + Ns.NamespaceName + "/" + methodName + "\"");

                var response = await _httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new Exception("Geniki SOAP " + methodName + ": HTTP " + (int)response.StatusCode + " - " + body);

                XDocument doc;
                try { doc = XDocument.Parse(body); }
                catch (Exception ex) { throw new Exception("Geniki SOAP " + methodName + ": μη έγκυρο XML response - " + ex.Message); }

                var resultElement = doc.Descendants(Ns + (methodName + "Result")).FirstOrDefault();
                if (resultElement == null)
                {
                    var fault = doc.Descendants(Soap + "Fault").FirstOrDefault();
                    var faultMsg = fault == null ? null : fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
                    throw new Exception(!string.IsNullOrEmpty(faultMsg)
                        ? "Geniki SOAP " + methodName + ": SOAP Fault - " + faultMsg
                        : "Geniki SOAP " + methodName + ": δεν βρέθηκε " + methodName + "Result.");
                }

                return resultElement;
            }
        }

        private async Task AuthenticateAsync()
        {
            var result = await CallSoapAsync("Authenticate",
                new XElement(Ns + "sUsrName", _config.UserId ?? string.Empty),
                new XElement(Ns + "sUsrPwd", _config.UserPassword ?? string.Empty),
                new XElement(Ns + "applicationKey", _config.ApiKey ?? string.Empty));

            int code = (int?)result.Element(Ns + "Result") ?? -1;
            if (code != 0) throw new Exception("Geniki Authenticate: " + DescribeError(code));

            _authKey = (string)result.Element(Ns + "Key");
            if (string.IsNullOrEmpty(_authKey)) throw new Exception("Geniki Authenticate: δεν επιστράφηκε authentication key.");
        }

        private async Task EnsureAuthenticatedAsync()
        {
            if (string.IsNullOrEmpty(_authKey)) await AuthenticateAsync();
        }

        private async Task<XElement> CallWithRetryAsync(Func<Task<XElement>> call, Func<XElement, int> getResultCode)
        {
            await EnsureAuthenticatedAsync();
            var result = await call();
            if (getResultCode(result) == 11)
            {
                _authKey = null;
                await AuthenticateAsync();
                result = await call();
            }
            return result;
        }

        public async Task<JarvisCourierShipmentResult> CreateShipmentAsync(JarvisCourierShipmentRequest request)
        {
            try
            {
                var result = await CallWithRetryAsync(
                    () => CallSoapAsync("CreateJob",
                        new XElement(Ns + "sAuthKey", _authKey),
                        BuildRecordXml(request),
                        new XElement(Ns + "eType", "Voucher")),
                    r => (int?)r.Element(Ns + "Result") ?? -1);

                int code = (int?)result.Element(Ns + "Result") ?? -1;
                if (code != 0)
                    return new JarvisCourierShipmentResult { Success = false, ErrorMessage = "Geniki CreateJob: " + DescribeError(code) };

                string voucherNo = (string)result.Element(Ns + "Voucher");
                string jobId = (string)result.Element(Ns + "JobId");
                return new JarvisCourierShipmentResult
                {
                    Success = true,
                    ShipmentNumber = voucherNo,
                    TrackingNumber = voucherNo,
                    ProviderJobId = jobId
                };
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier][GENIKI] CreateShipment error: " + ex);
                return new JarvisCourierShipmentResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static XElement BuildRecordXml(JarvisCourierShipmentRequest request)
        {
            string name = !string.IsNullOrEmpty(request.ReceiverContactName) ? request.ReceiverContactName : request.ReceiverName;
            var services = new List<string>();
            if (request.IsCod) services.Add(request.CodPaymentType == 1 ? "αν" : "αμ");
            if (request.SaturdayDelivery) services.Add("5Σ");

            return new XElement(Ns + "oVoucher",
                new XElement(Ns + "OrderId", request.DocumentNumber ?? string.Empty),
                new XElement(Ns + "Name", name ?? string.Empty),
                new XElement(Ns + "Address", request.ReceiverAddress ?? string.Empty),
                new XElement(Ns + "City", request.ReceiverCity ?? string.Empty),
                new XElement(Ns + "Telephone", request.ReceiverPhone ?? string.Empty),
                new XElement(Ns + "Zip", request.ReceiverZipCode ?? string.Empty),
                new XElement(Ns + "Weight", ((decimal)request.Weight).ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "Pieces", request.Pieces),
                new XElement(Ns + "Comments", request.Comments ?? string.Empty),
                new XElement(Ns + "Services", string.Join(",", services)),
                request.IsCod ? new XElement(Ns + "CodAmount", request.CodAmount.ToString(CultureInfo.InvariantCulture)) : null);
        }

        public async Task<JarvisCourierCancelResult> CancelShipmentAsync(string shipmentNumber, string providerJobId = null)
        {
            long jobId;
            if (string.IsNullOrWhiteSpace(providerJobId) || !long.TryParse(providerJobId, out jobId))
                return new JarvisCourierCancelResult
                {
                    Success = false,
                    ErrorMessage = "Geniki CancelJob: απαιτείται το αποθηκευμένο JobId (CCCCOURJOBID) της αποστολής."
                };

            try
            {
                var result = await CallWithRetryAsync(
                    () => CallSoapAsync("CancelJob",
                        new XElement(Ns + "sAuthKey", _authKey),
                        new XElement(Ns + "nJobId", jobId),
                        new XElement(Ns + "bCancel", "true")),
                    r => (int?)r ?? -1);

                int code = (int?)result ?? -1;
                return code == 0
                    ? new JarvisCourierCancelResult { Success = true }
                    : new JarvisCourierCancelResult { Success = false, ErrorMessage = "Geniki CancelJob: " + DescribeError(code) };
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier][GENIKI] CancelShipment error: " + ex);
                return new JarvisCourierCancelResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<byte[]> GetVoucherAsync(string shipmentNumber)
        {
            await EnsureAuthenticatedAsync();
            return await FetchVouchersPdfAsync(new List<string> { shipmentNumber });
        }

        public Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options)
        {
            throw new NotSupportedException("Η μαζική εκτύπωση Geniki δεν ανήκει στο Jarvis Courier v1 scope.");
        }

        private async Task<byte[]> FetchVouchersPdfAsync(List<string> voucherNumbers)
        {
            if (voucherNumbers == null || voucherNumbers.Count == 0)
                throw new Exception("Geniki GetVouchersPdf: κενή λίστα voucher.");

            string query = string.Join("&", voucherNumbers.Select(v => "voucherNumbers=" + Uri.EscapeDataString(v)));
            string url = _baseUrl + "/GetVouchersPdf?authKey=" + Uri.EscapeDataString(_authKey) + "&" + query + "&format=Flyer&extraInfoFormat=None";
            var response = await _httpClient.GetAsync(url);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            bool looksLikePdf = bytes.Length > 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;
            if (!response.IsSuccessStatusCode || !looksLikePdf)
                throw new Exception("Geniki GetVouchersPdf: αποτυχία λήψης PDF - " + Encoding.UTF8.GetString(bytes));
            return bytes;
        }

        public async Task<JarvisCourierTrackingResult> TrackShipmentAsync(string trackingNumber)
        {
            try
            {
                var result = await CallWithRetryAsync(
                    () => CallSoapAsync("TrackAndTrace",
                        new XElement(Ns + "authKey", _authKey),
                        new XElement(Ns + "voucherNo", trackingNumber),
                        new XElement(Ns + "language", "el")),
                    r => (int?)r.Element(Ns + "Result") ?? -1);

                int code = (int?)result.Element(Ns + "Result") ?? -1;
                if (code != 0)
                    return new JarvisCourierTrackingResult { Success = false, ErrorMessage = "Geniki TrackAndTrace: " + DescribeError(code) };

                var output = new JarvisCourierTrackingResult { Success = true };
                var checkpoints = result.Element(Ns + "Checkpoints")?.Elements();
                if (checkpoints != null)
                {
                    foreach (var cp in checkpoints)
                    {
                        DateTime statusDate;
                        DateTime.TryParse((string)cp.Element(Ns + "StatusDate"), out statusDate);
                        output.Entries.Add(new JarvisCourierTrackingEntry
                        {
                            Timestamp = statusDate,
                            Status = (string)cp.Element(Ns + "Status"),
                            Description = (string)cp.Element(Ns + "StatusCode"),
                            Location = (string)cp.Element(Ns + "Shop")
                        });
                    }
                }
                return output;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier][GENIKI] TrackShipment error: " + ex);
                return new JarvisCourierTrackingResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public Task<int> GetDeliveryDaysAsync(string originZip, string destZip)
        {
            return Task.FromResult(1);
        }

        private static string DescribeError(int code)
        {
            switch (code)
            {
                case 0: return "OK";
                case 1: return "General error";
                case 2: return "Invalid data";
                case 3: return "Voucher not found";
                case 11: return "Invalid or expired authentication key";
                default: return "Error code " + code;
            }
        }
    }
}
