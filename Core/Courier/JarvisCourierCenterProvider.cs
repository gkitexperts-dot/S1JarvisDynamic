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
    internal sealed class JarvisCourierCenterProvider : IJarvisCourierProvider
    {
        private const int ApiHardLimit = 60;
        private readonly JarvisCourierProviderConfig _config;
        private readonly HttpClient _httpClient;

        public JarvisCourierCenterProvider(JarvisCourierProviderConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _httpClient = new HttpClient();
            _httpClient.BaseAddress = new Uri(_config.ApiUrl);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public string ProviderName => _config.ProviderName;
        public int MaxVouchersPerBatch => (_config.MaxBatch > 0 && _config.MaxBatch < ApiHardLimit) ? _config.MaxBatch : ApiHardLimit;
        public bool SupportsCodChequeDate => true;
        public bool SupportsDeliveryTimeWindow => true;
        public bool SupportsDeliveryTimeRange => true;
        public bool SupportsSaturdayDelivery => false;
        public bool SupportsDeliveryDate => true;

        private object BuildContext()
        {
            return new
            {
                UserAlias = _config.UserAlias,
                CredentialValue = _config.CredentialValue,
                ApiKey = _config.ApiKey
            };
        }

        private async Task<JObject> PostAsync(string endpoint, object payload)
        {
            string json = JsonConvert.SerializeObject(payload,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await _httpClient.PostAsync(endpoint, content))
            {
                string responseText = await response.Content.ReadAsStringAsync();
                return JObject.Parse(responseText);
            }
        }

        private static string FirstErrorMessage(JObject result)
        {
            return result?["Errors"]?[0]?["Message"]?.ToString();
        }

        public async Task<JarvisCourierShipmentResult> CreateShipmentAsync(JarvisCourierShipmentRequest request)
        {
            try
            {
                object payload = new
                {
                    Context = BuildContext(),
                    ShipmentDate = DateTime.Now.ToString("yyyy-MM-dd"),
                    Comments = request.Comments,
                    IsMandatoryPickup = true,
                    GenerateShipmentAWB = true,
                    GenerateReturnAWB = false,
                    Requestor = new { CarrierBillingAccount = _config.AccountCode },
                    Shipper = new { CarrierBillingAccount = _config.AccountCode },
                    Consignee = new
                    {
                        CompanyName = request.ReceiverName,
                        ContactName = request.ReceiverContactName,
                        Reference = request.ReceiverContactName,
                        Address = request.ReceiverAddress,
                        City = request.ReceiverCity,
                        Area = request.ReceiverCity,
                        ZipCode = request.ReceiverZipCode,
                        Country = "GR",
                        Mobile1 = request.ReceiverPhone,
                        Phone1 = request.ReceiverPhone
                    },
                    BillTo = "Requestor",
                    CustomerReference = request.DocumentNumber,
                    Reference1 = request.DocumentRef,
                    CODs = request.IsCod ? new[]
                    {
                        new
                        {
                            Type = request.CodPaymentType == 1 ? "check" : "cash",
                            Amount = new { Value = request.CodAmount, Currency = "EUR" },
                            Date = request.CodPaymentType == 1 && request.CodChequeDate.HasValue
                                ? request.CodChequeDate.Value.ToString("yyyy-MM-dd")
                                : null
                        }
                    } : null,
                    CODReturnTo = request.IsCod ? _config.AccountCode : null,
                    DeliveryInstructions = (request.DeliveryTimeRequested || request.DeliveryDate.HasValue) ? new
                    {
                        Date = request.DeliveryDate.HasValue ? request.DeliveryDate.Value.ToString("yyyy-MM-dd") : null,
                        TimeFrom = request.DeliveryTimeFrom.HasValue ? request.DeliveryTimeFrom.Value.ToString(@"hh\:mm") : null,
                        TimeTo = request.DeliveryTimeTo.HasValue ? request.DeliveryTimeTo.Value.ToString(@"hh\:mm") : null
                    } : null,
                    Items = BuildItems(request)
                };

                JObject result = await PostAsync("Shipment", payload);
                string status = result["Result"]?.ToString();
                if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "PartialSuccess", StringComparison.OrdinalIgnoreCase))
                {
                    return new JarvisCourierShipmentResult
                    {
                        Success = true,
                        ShipmentNumber = result["ShipmentNumber"]?.ToString(),
                        TrackingNumber = result["TrackingNumbers"]?[0]?.ToString()
                    };
                }

                return new JarvisCourierShipmentResult
                {
                    Success = false,
                    ErrorMessage = FirstErrorMessage(result)
                };
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier-center] CreateShipment exception: " + ex);
                return new JarvisCourierShipmentResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private object[] BuildItems(JarvisCourierShipmentRequest request)
        {
            int pieces = request.Pieces > 0 ? request.Pieces : 1;
            double totalWeight = request.Weight;
            double perPiece = pieces > 1 ? Math.Round(totalWeight / pieces, 3) : totalWeight;

            return Enumerable.Range(1, pieces)
                .Select(i => new
                {
                    GoodsType = "NoDocs",
                    Content = "ΔΕΜΑΤΑ",
                    IsDangerousGoods = false,
                    IsDryIce = false,
                    IsFragile = false,
                    Weight = new
                    {
                        Unit = "kg",
                        Value = i == pieces ? totalWeight - perPiece * (pieces - 1) : perPiece
                    }
                })
                .Cast<object>()
                .ToArray();
        }

        public async Task<JarvisCourierCancelResult> CancelShipmentAsync(string shipmentNumber, string providerJobId = null)
        {
            try
            {
                JObject result = await PostAsync("Shipment/Void", new
                {
                    Context = BuildContext(),
                    ShipmentNumber = shipmentNumber
                });

                return new JarvisCourierCancelResult
                {
                    Success = string.Equals(result["Result"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase),
                    ErrorMessage = FirstErrorMessage(result)
                };
            }
            catch (Exception ex)
            {
                return new JarvisCourierCancelResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<byte[]> GetVoucherAsync(string shipmentNumber)
        {
            try
            {
                return await GetVoucherInternalAsync(shipmentNumber,
                    string.IsNullOrWhiteSpace(_config.PrintTemplate) ? "singlepdf" : _config.PrintTemplate);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier-center] GetVoucher exception: " + ex);
                return null;
            }
        }

        private async Task<byte[]> GetVoucherInternalAsync(string shipmentNumbers, string template)
        {
            JObject result = await PostAsync("Voucher", new
            {
                Context = BuildContext(),
                ShipmentNumber = shipmentNumbers,
                VoucherFormat = "PDF",
                Template = template
            });

            if (!string.Equals(result["Result"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase))
                throw new Exception(FirstErrorMessage(result) ?? "Courier Center: voucher generation failed.");

            string base64 = result["Voucher"]?.ToString();
            if (string.IsNullOrWhiteSpace(base64))
                throw new Exception("Courier Center: empty voucher PDF response.");

            return Convert.FromBase64String(base64);
        }

        public Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options)
        {
            throw new NotSupportedException("Jarvis Courier v1 does not expose batch voucher printing.");
        }

        public async Task<JarvisCourierTrackingResult> TrackShipmentAsync(string trackingNumber)
        {
            try
            {
                JObject result = await PostAsync("Tracking", new { Identifier = trackingNumber });
                var output = new JarvisCourierTrackingResult
                {
                    Success = string.Equals(result["Result"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase)
                };

                var list = result["TrackingList"] as JArray;
                if (list != null)
                {
                    foreach (JToken entry in list)
                    {
                        output.Entries.Add(new JarvisCourierTrackingEntry
                        {
                            Timestamp = entry["ExecutedOn"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                            Status = entry["Type"]?.ToString(),
                            Description = entry["Note"]?.ToString(),
                            Location = entry["StationName"]?.ToString()
                        });
                    }
                }

                return output;
            }
            catch (Exception ex)
            {
                return new JarvisCourierTrackingResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        public async Task<int> GetDeliveryDaysAsync(string originZip, string destZip)
        {
            try
            {
                JObject result = await PostAsync("Station/GetDeliveryDays", new
                {
                    Context = BuildContext(),
                    OriginZipCode = originZip,
                    OriginCountry = "GR",
                    DestinationZipcode = destZip,
                    DestinationCountry = "GR"
                });

                return string.Equals(result["Result"]?.ToString(), "Success", StringComparison.OrdinalIgnoreCase)
                    ? (result["Days"]?.ToObject<int>() ?? -1)
                    : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
