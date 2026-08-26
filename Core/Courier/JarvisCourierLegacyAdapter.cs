using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using S1Courier.Core;
using S1Courier.Models;

namespace S1Jarvis.Core.Courier
{
    // Transitional bridge kept only until JarvisCourier.cs is switched to
    // Jarvis-owned contracts. All four concrete providers are now native.
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
            return new JarvisCourierCancelResult { Success = result.Success, ErrorMessage = result.ErrorMessage };
        }

        public Task<byte[]> GetVoucherAsync(string shipmentNumber) => _inner.GetVoucherAsync(shipmentNumber);
        public Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options) => _inner.GetBatchVoucherAsync(shipmentNumbers, options);

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

        public Task<int> GetDeliveryDaysAsync(string originZip, string destZip) => _inner.GetDeliveryDaysAsync(originZip, destZip);

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
        static JarvisCourierProviderFactory()
        {
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 |
                System.Net.SecurityProtocolType.Tls11 |
                System.Net.SecurityProtocolType.Tls;
        }

        public static IJarvisCourierProvider Create(JarvisCourierProviderConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            string code = (config.ProviderCode ?? string.Empty).Trim().ToUpperInvariant();
            if (code == "COURIER CENTER") return new JarvisCourierCenterProvider(config);
            if (code == "ELTA COURIER") return new JarvisEltaCourierProvider(config);
            if (code == "ACS COURIER") return new JarvisAcsCourierProvider(config);
            if (code == "GENIKI TAXYDROMIKI") return new JarvisGenikiCourierProvider(config);

            throw new NotSupportedException("Μη υποστηριζόμενος courier provider: " + config.ProviderCode);
        }
    }
}
