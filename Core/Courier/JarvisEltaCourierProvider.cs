using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core.Courier
{
    // Jarvis-native ELTA implementation. It intentionally does not depend on
    // the standalone S1Courier assembly; licensing remains JARVISCOURIER.
    internal sealed class JarvisEltaCourierProvider : IJarvisCourierProvider
    {
        private readonly JarvisCourierProviderConfig _config;
        private readonly HttpClient _httpClient;
        private string _apiKey;
        private DateTime _apiKeyExpiration = DateTime.MinValue;

        public JarvisEltaCourierProvider(JarvisCourierProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClient = new HttpClient { BaseAddress = new Uri(_config.ApiUrl) };
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public string ProviderName => _config.ProviderName;
        public int MaxVouchersPerBatch => 1;
        public bool SupportsCodChequeDate => true;
        public bool SupportsDeliveryTimeWindow => false;
        public bool SupportsDeliveryTimeRange => false;
        public bool SupportsSaturdayDelivery => false;
        public bool SupportsDeliveryDate => false;

        private async Task<JObject> PostAsync(string endpoint, object payload, bool withApiKey)
        {
            string json = JsonConvert.SerializeObject(payload);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var msg = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                msg.Content = content;
                if (withApiKey)
                {
                    if (string.IsNullOrWhiteSpace(_apiKey))
                        throw new Exception("ELTA " + endpoint + ": το api_Key είναι κενό.");
                    if (!msg.Headers.TryAddWithoutValidation("APIKEY", _apiKey.Trim()))
                        throw new Exception("Δεν ήταν δυνατή η προσθήκη του APIKEY header.");
                }

                var response = await _httpClient.SendAsync(msg);
                string responseStr = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new Exception("ELTA " + endpoint + ": HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + "\r\n" + responseStr);
                if (string.IsNullOrWhiteSpace(responseStr))
                    throw new Exception("ELTA " + endpoint + ": κενό response.");
                return JObject.Parse(responseStr);
            }
        }

        private static bool HasError(JObject result)
        {
            return result["baseResponse"]?["error_code"]?.ToString() != "0";
        }

        private static string GetErrorMessage(JObject result)
        {
            return result["baseResponse"]?["error_remarks"]?.ToString();
        }

        private async Task EnsureAuthenticatedAsync()
        {
            if (!string.IsNullOrEmpty(_apiKey) && DateTime.Now < _apiKeyExpiration.AddMinutes(-1))
                return;

            var result = await PostAsync("authorization", new
            {
                customerCode = _config.CustCode,
                customerUser = _config.CustUser,
                customerPassword = _config.CustPass
            }, false);

            if (HasError(result))
                throw new Exception("ELTA Authorization: " + GetErrorMessage(result));

            _apiKey = result["api_Key"]?.ToString();
            if (string.IsNullOrEmpty(_apiKey))
                throw new Exception("ELTA Authorization: Δεν επιστράφηκε api_Key");

            DateTime expiration;
            if (!DateTime.TryParse(result["expiration_date"]?.ToString(), out expiration))
                expiration = DateTime.Now.AddMinutes(10);
            _apiKeyExpiration = expiration;
        }

        public async Task<JarvisCourierShipmentResult> CreateShipmentAsync(JarvisCourierShipmentRequest request)
        {
            try
            {
                await EnsureAuthenticatedAsync();
                var result = await PostAsync("CreateVoucher", new
                {
                    userCode = _config.UserId,
                    userPassword = _config.UserPassword,
                    pelCode = _config.PelCode,
                    pelType = 1,
                    pelSubCode = _config.SubCode ?? "",
                    ARSubCode = "",
                    voucher_numbers = new { voucher_number = "", voucher_child = new[] { new { child_number = "" } }, epitagi_number = "", ar_number = "" },
                    sender_details = new
                    {
                        cust_title = request.SenderName,
                        cust_address = request.SenderAddress,
                        cust_area = request.SenderCity,
                        cust_postal_code = request.SenderZipCode,
                        cust_telefon = request.SenderPhone,
                        cust_mobile = request.SenderPhone,
                        cust_mobileExoterikou = "",
                        cust_email = "",
                        cust_remarks = ""
                    },
                    receiver_details = new
                    {
                        cust_title = !string.IsNullOrEmpty(request.ReceiverContactName) ? request.ReceiverContactName : request.ReceiverName,
                        cust_address = request.ReceiverAddress,
                        cust_area = request.ReceiverCity,
                        cust_postal_code = request.ReceiverZipCode,
                        cust_telefon = request.ReceiverPhone,
                        cust_mobile = request.ReceiverPhone,
                        cust_mobileExoterikou = "",
                        cust_email = "",
                        cust_remarks = ""
                    },
                    voucher_details = new
                    {
                        voucher_service = !string.IsNullOrEmpty(request.ServiceType) ? request.ServiceType : "1",
                        voucher_charge = (int?)null,
                        voucher_packages = request.Pieces,
                        voucher_actual_weight = request.Weight,
                        voucher_insurance_amount = 0,
                        dim_x = 0,
                        dim_y = 0,
                        dim_z = 0,
                        additional_services = new string[] { },
                        pudo_code = "",
                        reference1 = request.DocumentNumber,
                        reference2 = request.DocumentRef,
                        reference3 = ""
                    },
                    cod_details = new
                    {
                        cod_type = request.IsCod ? (request.CodPaymentType == 1 ? 2 : 1) : 0,
                        cod_amount = request.IsCod ? request.CodAmount : 0,
                        cod_date = request.IsCod && request.CodPaymentType == 1 && request.CodChequeDate.HasValue
                            ? request.CodChequeDate.Value.ToString("yyyyMMdd") : ""
                    },
                    order_details = new { order_flag = 0, pickup_date = "", pickup_start_time = "", pickup_end_time = "" }
                }, true);

                if (HasError(result))
                    return new JarvisCourierShipmentResult { Success = false, ErrorMessage = GetErrorMessage(result) };

                string voucherNo = result["voucher_numbers"]?["voucher_number"]?.ToString();
                return new JarvisCourierShipmentResult { Success = true, ShipmentNumber = voucherNo, TrackingNumber = voucherNo };
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier][ELTA] CreateShipment error: " + ex);
                return new JarvisCourierShipmentResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<JarvisCourierCancelResult> CancelShipmentAsync(string shipmentNumber, string providerJobId = null)
        {
            try
            {
                await EnsureAuthenticatedAsync();
                var result = await PostAsync("CancelVoucher", new
                {
                    pelCode = _config.PelCode,
                    pelUserCode = _config.UserId,
                    flag1 = "0",
                    flag2 = "0",
                    type = "2",
                    voucher = shipmentNumber,
                    id = 0,
                    cancelComments = ""
                }, true);
                return new JarvisCourierCancelResult { Success = !HasError(result), ErrorMessage = GetErrorMessage(result) };
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier][ELTA] CancelShipment error: " + ex);
                return new JarvisCourierCancelResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private async Task<byte[]> PrintVoucherInternalAsync(string voucherNum)
        {
            var result = await PostAsync("PrintVoucher", new
            {
                userCode = _config.UserId,
                userPassword = _config.UserPassword,
                pelCode = _config.PelCode,
                voucherNum = voucherNum
            }, true);

            if (HasError(result))
                throw new Exception("ELTA voucher " + voucherNum + ": " + GetErrorMessage(result));

            var b64List = result["b64vouchersList"] as JArray;
            if (b64List == null || b64List.Count == 0)
                throw new Exception("ELTA voucher " + voucherNum + ": Δεν επιστράφηκε PDF");

            // Jarvis Courier v1 is single-voucher only. ELTA normally returns a
            // single PDF for the master; if multiple PDFs are returned for a
            // multi-piece shipment, return the first non-empty one until the
            // Jarvis-native PDF merger is introduced with batch support.
            foreach (var token in b64List)
            {
                string value = token?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    return Convert.FromBase64String(value);
            }
            throw new Exception("ELTA voucher " + voucherNum + ": κενά PDF στη λίστα");
        }

        public async Task<byte[]> GetVoucherAsync(string shipmentNumber)
        {
            await EnsureAuthenticatedAsync();
            return await PrintVoucherInternalAsync(shipmentNumber);
        }

        public Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options)
        {
            throw new NotSupportedException("Η μαζική εκτύπωση ELTA δεν ανήκει στο Jarvis Courier v1 scope.");
        }

        public async Task<JarvisCourierTrackingResult> TrackShipmentAsync(string trackingNumber)
        {
            try
            {
                await EnsureAuthenticatedAsync();
                var result = await PostAsync("TraceVoucher", new
                {
                    userCode = _config.UserId,
                    userPassword = _config.UserPassword,
                    pelCode = _config.PelCode,
                    voucher = trackingNumber,
                    dateFrom = "",
                    dateTo = "",
                    paralCode = "",
                    statusFlag = ""
                }, true);

                var output = new JarvisCourierTrackingResult { Success = !HasError(result) };
                if (!output.Success)
                {
                    output.ErrorMessage = GetErrorMessage(result);
                    return output;
                }

                var lines = result["trace_details"] as JArray;
                if (lines != null)
                {
                    foreach (var line in lines)
                    {
                        DateTime timestamp;
                        DateTime.TryParse((line["trace_date"]?.ToString() ?? "") + " " + (line["trace_time"]?.ToString() ?? ""), out timestamp);
                        output.Entries.Add(new JarvisCourierTrackingEntry
                        {
                            Timestamp = timestamp,
                            Status = line["trace_Status_gr"]?.ToString(),
                            Description = line["trace_remarks"]?.ToString(),
                            Location = line["trace_Station_gr"]?.ToString()
                        });
                    }
                }
                return output;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier][ELTA] TrackShipment error: " + ex);
                return new JarvisCourierTrackingResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public Task<int> GetDeliveryDaysAsync(string originZip, string destZip)
        {
            return Task.FromResult(1);
        }
    }
}
