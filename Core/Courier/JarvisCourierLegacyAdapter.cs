using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using S1Courier.Core;
using S1Courier.Models;

namespace S1Jarvis.Core.Courier
{
    // Transitional bridge while the concrete providers are moved into Jarvis.
    // It lets Jarvis-owned contracts become the stable boundary first, without
    // changing voucher behavior in the same step. Delete this file when all
    // providers are native and the S1Courier assembly reference is removed.
    internal sealed class JarvisCourierLegacyAdapter : IJarvisCourierProvider
    {
        private readonly ICourierProvider _inner;

        public JarvisCourierLegacyAdapter(ICourierProvider inner)
        {
            _inner = inner;
        }

        public string ProviderName => _inner.ProviderName;
        public int MaxVouchersPerBatch => _inner.MaxVouchersPerBatch;
        public bool SupportsCodChequeDate => _inner.SupportsCodChequeDate;
        public bool SupportsDeliveryTimeWindow => _inner.SupportsDeliveryTimeWindow;
        public bool SupportsDeliveryTimeRange => _inner.SupportsDeliveryTimeRange;
        public bool SupportsSaturdayDelivery => _inner.SupportsSaturdayDelivery;
        public bool SupportsDeliveryDate => _inner.SupportsDeliveryDate;

        public async Task<JarvisCourierShipmentResult> CreateShipmentAsync(JarvisCourierShipmentRequest request)
        {
            ShipmentResult result = await _inner.CreateShipmentAsync(ToLegacyRequest(request));
            return new JarvisCourierShipmentResult
            {
                Success = result.Success,
                ShipmentNumber = result.ShipmentNumber,
                TrackingNumber = result.TrackingNumber,
                ErrorMessage = result.ErrorMessage,
                Errors = result.Errors == null ? new List<string>() : result.Errors.ToList(),
                ProviderJobId = result.ProviderJobId
            };
        }

        public async Task<JarvisCourierCancelResult> CancelShipmentAsync(string shipmentNumber, string providerJobId = null)
        {
            CancelResult result = await _inner.CancelShipmentAsync(shipmentNumber, providerJobId);
            return new JarvisCourierCancelResult
            {
                Success = result.Success,
                ErrorMessage = result.ErrorMessage
            };
        }

        public Task<byte[]> GetVoucherAsync(string shipmentNumber)
        {
            return _inner.GetVoucherAsync(shipmentNumber);
        }

        public Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options)
        {
            return _inner.GetBatchVoucherAsync(shipmentNumbers, options);
        }

        public async Task<JarvisCourierTrackingResult> TrackShipmentAsync(string trackingNumber)
        {
            TrackingResult result = await _inner.TrackShipmentAsync(trackingNumber);
            return new JarvisCourierTrackingResult
            {
                Success = result.Success,
                ErrorMessage = result.ErrorMessage,
                Entries = result.Entries == null
                    ? new List<JarvisCourierTrackingEntry>()
                    : result.Entries.Select(x => new JarvisCourierTrackingEntry
                    {
                        Timestamp = x.Timestamp,
                        Status = x.Status,
                        Description = x.Description,
                        Location = x.Location
                    }).ToList()
            };
        }

        public Task<int> GetDeliveryDaysAsync(string originZip, string destZip)
        {
            return _inner.GetDeliveryDaysAsync(originZip, destZip);
        }

        private static ShipmentRequest ToLegacyRequest(JarvisCourierShipmentRequest request)
        {
            return new ShipmentRequest
            {
                SenderName = request.SenderName,
                SenderAddress = request.SenderAddress,
                SenderCity = request.SenderCity,
                SenderZipCode = request.SenderZipCode,
                SenderPhone = request.SenderPhone,
                ReceiverContactName = request.ReceiverContactName,
                ReceiverName = request.ReceiverName,
                ReceiverAddress = request.ReceiverAddress,
                ReceiverCity = request.ReceiverCity,
                ReceiverZipCode = request.ReceiverZipCode,
                ReceiverPhone = request.ReceiverPhone,
                Pieces = request.Pieces,
                Weight = request.Weight,
                Comments = request.Comments,
                IsCOD = request.IsCod,
                CODAmount = request.CodAmount,
                DocumentRef = request.DocumentRef,
                DocumentNumber = request.DocumentNumber,
                ExistingShipmentNumber = request.ExistingShipmentNumber,
                ExistingProviderCode = request.ExistingProviderCode,
                ExistingJobId = request.ExistingJobId,
                PaymentCode = request.PaymentCode,
                DocumentAmount = request.DocumentAmount,
                ServiceType = request.ServiceType,
                CODPaymentType = request.CodPaymentType,
                CODChequeDate = request.CodChequeDate,
                DeliveryTimeRequested = request.DeliveryTimeRequested,
                DeliveryTimeFrom = request.DeliveryTimeFrom,
                DeliveryTimeTo = request.DeliveryTimeTo,
                DeliveryDate = request.DeliveryDate,
                SaturdayDelivery = request.SaturdayDelivery
            };
        }
    }

    internal static class JarvisCourierProviderFactory
    {
        public static IJarvisCourierProvider Create(JarvisCourierProviderConfig config)
        {
            var legacy = new CourierProviderConfig
            {
                ID = config.Id,
                ProviderName = config.ProviderName,
                ProviderCode = config.ProviderCode,
                ApiUrl = config.ApiUrl,
                ApiKey = config.ApiKey,
                UserAlias = config.UserAlias,
                CredentialValue = config.CredentialValue,
                AccountCode = config.AccountCode,
                IsActive = config.IsActive,
                CompanyId = config.CompanyId,
                CompanyPassword = config.CompanyPassword,
                UserId = config.UserId,
                UserPassword = config.UserPassword,
                BillingCode = config.BillingCode,
                CodPaywayCode = config.CodPaywayCode,
                Company = config.Company,
                Branch = config.Branch,
                IsDefault = config.IsDefault,
                PrintType = config.PrintType,
                PrintTemplate = config.PrintTemplate,
                SubCode = config.SubCode,
                CustCode = config.CustCode,
                CustUser = config.CustUser,
                CustPass = config.CustPass,
                PelCode = config.PelCode,
                TwoStepAuth = config.TwoStepAuth,
                MaxBatch = config.MaxBatch,
                LastFinalized = config.LastFinalized
            };

            return new JarvisCourierLegacyAdapter(CourierProviderFactory.Create(legacy));
        }
    }
}
