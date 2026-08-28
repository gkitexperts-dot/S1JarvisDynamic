using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core.Courier
{
    internal sealed class JarvisAcsCourierProvider : IJarvisCourierProvider
    {
        private const int ApiHardLimit = 10;
        private readonly JarvisCourierProviderConfig _config;
        private readonly HttpClient _httpClient;

        public JarvisAcsCourierProvider(JarvisCourierProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClient = new HttpClient { BaseAddress = new Uri(_config.ApiUrl) };
            _httpClient.DefaultRequestHeaders.Add("AcsApiKey", _config.ApiKey);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public string ProviderName => _config.ProviderName;
        public int MaxVouchersPerBatch => (_config.MaxBatch > 0 && _config.MaxBatch < ApiHardLimit) ? _config.MaxBatch : ApiHardLimit;
        public bool SupportsCodChequeDate => false;
        public bool SupportsDeliveryTimeWindow => true;
        public bool SupportsDeliveryTimeRange => false;
        public bool SupportsSaturdayDelivery => true;
        public bool SupportsDeliveryDate => false;

        private async Task<JObject> PostAsync(string alias, object inputParameters)
        {
            var payload = new { ACSAlias = alias, ACSInputParameters = inputParameters };
            string json = JsonConvert.SerializeObject(payload);
            DebugLog.Log("[Jarvis ACS ->] " + alias + "\n" + MaskSecrets(json));

            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            {
                var response = await _httpClient.PostAsync("", content);
                string responseText = await response.Content.ReadAsStringAsync();
                DebugLog.Log("[Jarvis ACS <-] " + alias + "\n" + responseText);

                if (!response.IsSuccessStatusCode)
                    throw new Exception("ACS " + alias + ": HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase);

                return JObject.Parse(responseText);
            }
        }

        private static string MaskSecrets(string json)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                json,
                "(\"(?:Company_Password|User_Password|API_Key|AppKey)\"\\s*:\\s*\")[^\"]*(\")",
                "$1***$2");
        }

        private static bool HasError(JObject result)
        {
            return result["ACSExecution_HasError"]?.ToObject<bool>() == true;
        }

        private static string GetErrorMessage(JObject result)
        {
            return result["ACSExecutionErrorMessage"]?.ToString();
        }

        private static string BuildDeliveryProducts(JarvisCourierShipmentRequest request)
        {
            var products = new List<string>();
            if (request.IsCod) products.Add("COD");
            if (request.SaturdayDelivery) products.Add("SAT");
            return products.Count == 0 ? null : string.Join(",", products);
        }

        public async Task<JarvisCourierShipmentResult> CreateShipmentAsync(JarvisCourierShipmentRequest request)
        {
            try
            {
                var result = await PostAsync("ACS_Create_Voucher", new
                {
                    Company_ID = _config.CompanyId,
                    Company_Password = _config.CompanyPassword,
                    User_ID = _config.UserId,
                    User_Password = _config.UserPassword,
                    Pickup_Date = DateTime.Now.ToString("yyyy-MM-dd"),
                    Sender = request.SenderName,
                    Recipient_Name = !string.IsNullOrEmpty(request.ReceiverContactName) ? request.ReceiverContactName : request.ReceiverName,
                    Recipient_Company_Name = !string.IsNullOrEmpty(request.ReceiverContactName) ? request.ReceiverName : null,
                    Recipient_Address = request.ReceiverAddress,
                    Recipient_Address_Number = (string)null,
                    Recipient_Zipcode = request.ReceiverZipCode,
                    Recipient_Region = request.ReceiverCity,
                    Recipient_Phone = request.ReceiverPhone,
                    Recipient_Cell_Phone = request.ReceiverPhone,
                    Recipient_Floor = (string)null,
                    Recipient_Country = "GR",
                    Acs_Station_Destination = (string)null,
                    Acs_Station_Branch_Destination = 1,
                    Billing_Code = _config.BillingCode,
                    Charge_Type = 2,
                    Cost_Center_Code = (string)null,
                    Item_Quantity = request.Pieces,
                    Weight = request.Weight,
                    Cod_Ammount = request.IsCod ? request.CodAmount : (decimal?)null,
                    Cod_Payment_Way = request.IsCod ? request.CodPaymentType : (int?)null,
                    Acs_Delivery_Products = BuildDeliveryProducts(request),
                    Appointment_Until_Time = request.DeliveryTimeRequested && request.DeliveryTimeTo.HasValue
                        ? request.DeliveryTimeTo.Value.ToString(@"hh\:mm")
                        : null,
                    Reference_Key1 = request.DocumentNumber,
                    Reference_Key2 = request.DocumentRef,
                    Language = "GR"
                });

                if (HasError(result))
                    return new JarvisCourierShipmentResult { Success = false, ErrorMessage = GetErrorMessage(result) };

                var value = result["ACSOutputResponce"]?["ACSValueOutput"]?[0];
                string errorMessage = value?["Error_Message"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(errorMessage))
                    return new JarvisCourierShipmentResult { Success = false, ErrorMessage = errorMessage };

                string voucherNo = value?["Voucher_No"]?.ToString()?.Trim();
                return new JarvisCourierShipmentResult
                {
                    Success = true,
                    ShipmentNumber = voucherNo,
                    TrackingNumber = voucherNo
                };
            }
            catch (Exception ex)
            {
                DebugLog.Log("Jarvis ACS CreateShipment error: " + ex);
                return new JarvisCourierShipmentResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<JarvisCourierCancelResult> CancelShipmentAsync(string shipmentNumber, string providerJobId = null)
        {
            try
            {
                var result = await PostAsync("ACS_Delete_Voucher", new
                {
                    Company_ID = _config.CompanyId,
                    Company_Password = _config.CompanyPassword,
                    User_ID = _config.UserId,
                    User_Password = _config.UserPassword,
                    Language = (string)null,
                    Voucher_No = shipmentNumber
                });

                return new JarvisCourierCancelResult
                {
                    Success = !HasError(result),
                    ErrorMessage = GetErrorMessage(result)
                };
            }
            catch (Exception ex)
            {
                DebugLog.Log("Jarvis ACS CancelShipment error: " + ex);
                return new JarvisCourierCancelResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private int ResolvePrintType(string options)
        {
            int printType;
            if (!string.IsNullOrEmpty(options) && int.TryParse(options, out printType))
                return printType;
            return _config.PrintType == "thermal" ? 1 : 2;
        }

        private async Task<byte[]> PrintVoucherInternalAsync(string voucherString, int printType)
        {
            var result = await PostAsync("ACS_Print_Voucher", new
            {
                Company_ID = _config.CompanyId,
                Company_Password = _config.CompanyPassword,
                User_ID = _config.UserId,
                User_Password = _config.UserPassword,
                Voucher_No = voucherString,
                Print_Type = printType,
                Start_Position = 1
            });

            if (HasError(result))
                throw new Exception("ACS Error: " + (GetErrorMessage(result) ?? "Unknown error"));

            JToken output = result["ACSOutputResponce"]?["ACSValueOutput"]?[0]?["ACSObjectOutput"];
            if (output == null || output.Type == JTokenType.Null)
                throw new Exception("ACS: Δεν επιστράφηκε PDF");

            var pdfs = ExtractAllPdfBytes(output);
            if (pdfs.Count == 0)
                throw new Exception("ACS: Το ACSObjectOutput δεν περιείχε έγκυρο PDF.");

            return JarvisCourierPdfHelper.MergePdfs(pdfs);
        }

        public async Task<byte[]> GetVoucherAsync(string shipmentNumber)
        {
            int printType = ResolvePrintType(null);
            if (printType == 2)
            {
                var children = await GetMultipartVouchersAsync(shipmentNumber);
                if (children.Count > 0)
                    return await PrintVoucherInternalAsync(shipmentNumber + "," + string.Join(",", children), printType);
            }
            return await PrintVoucherInternalAsync(shipmentNumber, printType);
        }

        private async Task<List<string>> GetMultipartVouchersAsync(string mainVoucherNo)
        {
            var children = new List<string>();
            try
            {
                object mainVoucher = mainVoucherNo;
                long numericVoucher;
                if (long.TryParse(mainVoucherNo, out numericVoucher))
                    mainVoucher = numericVoucher;

                var result = await PostAsync("ACS_Get_Multipart_Vouchers", new
                {
                    Company_ID = _config.CompanyId,
                    Company_Password = _config.CompanyPassword,
                    User_ID = _config.UserId,
                    User_Password = _config.UserPassword,
                    Language = (string)null,
                    Main_Voucher_No = mainVoucher
                });

                if (HasError(result)) return children;
                var rows = result["ACSOutputResponce"]?["ACSTableOutput"]?["Table_Data"];
                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        string no = row["MultiPart_Voucher_No"]?.ToString();
                        if (!string.IsNullOrEmpty(no)) children.Add(no);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("Jarvis ACS GetMultipartVouchers error: " + ex);
            }
            return children;
        }

        public Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options)
        {
            throw new NotSupportedException("Η μαζική εκτύπωση ACS δεν ανήκει στο Jarvis Courier v1 scope.");
        }

        public async Task<JarvisCourierTrackingResult> TrackShipmentAsync(string trackingNumber)
        {
            try
            {
                var result = await PostAsync("ACS_TrackingDetails", new
                {
                    Company_ID = _config.CompanyId,
                    Company_Password = _config.CompanyPassword,
                    User_ID = _config.UserId,
                    User_Password = _config.UserPassword,
                    Language = "GR",
                    Voucher_No = trackingNumber
                });

                var output = new JarvisCourierTrackingResult { Success = !HasError(result) };
                if (!output.Success)
                {
                    output.ErrorMessage = GetErrorMessage(result);
                    return output;
                }

                var lines = result["ACSOutputResponce"]?["ACSTableOutput"]?["Table_Data"] as JArray;
                if (lines != null)
                {
                    foreach (var line in lines)
                    {
                        output.Entries.Add(new JarvisCourierTrackingEntry
                        {
                            Timestamp = line["checkpoint_date_time"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                            Status = line["checkpoint_action"]?.ToString(),
                            Description = line["checkpoint_notes"]?.ToString(),
                            Location = line["checkpoint_location"]?.ToString()
                        });
                    }
                }
                return output;
            }
            catch (Exception ex)
            {
                DebugLog.Log("Jarvis ACS TrackShipment error: " + ex);
                return new JarvisCourierTrackingResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public Task<int> GetDeliveryDaysAsync(string originZip, string destZip)
        {
            return Task.FromResult(1);
        }

        private static List<byte[]> ExtractAllPdfBytes(JToken token)
        {
            var found = new List<byte[]>();
            CollectPdfBytes(token, found);
            return found;
        }

        private static void CollectPdfBytes(JToken token, List<byte[]> found)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;

            if (token.Type == JTokenType.String)
            {
                byte[] bytes = ExtractPdfBytes(token);
                if (IsPdf(bytes)) found.Add(bytes);
                return;
            }

            if (token.Type == JTokenType.Array)
            {
                var array = token as JArray;
                if (array != null && array.All(x => x.Type == JTokenType.Integer))
                {
                    byte[] bytes = ExtractPdfBytes(token);
                    if (IsPdf(bytes)) found.Add(bytes);
                    return;
                }

                foreach (var child in token.Children())
                    CollectPdfBytes(child, found);
                return;
            }

            if (token.Type == JTokenType.Object)
            {
                foreach (var property in ((JObject)token).Properties())
                    CollectPdfBytes(property.Value, found);
            }
        }

        private static byte[] ExtractPdfBytes(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.String)
            {
                string value = token.ToString().Trim();
                if (string.IsNullOrEmpty(value)) return null;
                try
                {
                    return Convert.FromBase64String(value);
                }
                catch (FormatException)
                {
                    return value.StartsWith("%PDF") ? Encoding.UTF8.GetBytes(value) : null;
                }
            }

            if (token.Type == JTokenType.Array)
            {
                var array = token as JArray;
                if (array != null && array.All(x => x.Type == JTokenType.Integer))
                    return array.Select(x => (byte)x.Value<int>()).ToArray();
            }

            return null;
        }

        private static bool IsPdf(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 4 &&
                   bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46;
        }
    }
}
