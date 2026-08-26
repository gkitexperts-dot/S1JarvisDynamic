using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using S1Jarvis.Core.Courier;

// Internal compatibility surface for the existing JarvisCourier orchestration.
// These names intentionally live only inside S1Jarvis.dll; they are NOT the
// standalone S1Courier product. Concrete provider implementations are the
// Jarvis-prefixed classes under S1Jarvis.Core.Courier.
namespace S1Courier.Models
{
    internal sealed class CourierProviderConfig
    {
        public int ID { get; set; }
        public string ProviderName { get; set; }
        public string ProviderCode { get; set; }
        public string ApiUrl { get; set; }
        public string ApiKey { get; set; }
        public string UserAlias { get; set; }
        public string CredentialValue { get; set; }
        public string AccountCode { get; set; }
        public bool IsActive { get; set; }
        public string CompanyId { get; set; }
        public string CompanyPassword { get; set; }
        public string UserId { get; set; }
        public string UserPassword { get; set; }
        public string BillingCode { get; set; }
        public string CodPaywayCode { get; set; }
        public int Company { get; set; }
        public int Branch { get; set; }
        public bool IsDefault { get; set; }
        public string PrintType { get; set; }
        public string PrintTemplate { get; set; }
        public string SubCode { get; set; }
        public string CustCode { get; set; }
        public string CustUser { get; set; }
        public string CustPass { get; set; }
        public string PelCode { get; set; }
        public bool TwoStepAuth { get; set; }
        public int MaxBatch { get; set; }
        public DateTime? LastFinalized { get; set; }
    }

    internal sealed class ShipmentRequest
    {
        public string SenderName { get; set; }
        public string SenderAddress { get; set; }
        public string SenderCity { get; set; }
        public string SenderZipCode { get; set; }
        public string SenderPhone { get; set; }
        public string ReceiverContactName { get; set; }
        public string ReceiverName { get; set; }
        public string ReceiverAddress { get; set; }
        public string ReceiverCity { get; set; }
        public string ReceiverZipCode { get; set; }
        public string ReceiverPhone { get; set; }
        public int Pieces { get; set; } = 1;
        public double Weight { get; set; }
        public string Comments { get; set; }
        public bool IsCOD { get; set; }
        public decimal CODAmount { get; set; }
        public string DocumentRef { get; set; }
        public string DocumentNumber { get; set; }
        public string ExistingShipmentNumber { get; set; }
        public string ExistingProviderCode { get; set; }
        public string ExistingJobId { get; set; }
        public string PaymentCode { get; set; }
        public decimal DocumentAmount { get; set; }
        public string ServiceType { get; set; } = "1";
        public int CODPaymentType { get; set; }
        public DateTime? CODChequeDate { get; set; }
        public bool DeliveryTimeRequested { get; set; }
        public TimeSpan? DeliveryTimeFrom { get; set; }
        public TimeSpan? DeliveryTimeTo { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public bool SaturdayDelivery { get; set; }
    }

    internal sealed class ShipmentResult
    {
        public bool Success { get; set; }
        public string ShipmentNumber { get; set; }
        public string TrackingNumber { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public string ProviderJobId { get; set; }
    }

    internal sealed class CancelResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    internal sealed class TrackingResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public List<TrackingEntry> Entries { get; set; } = new List<TrackingEntry>();
    }

    internal sealed class TrackingEntry
    {
        public DateTime Timestamp { get; set; }
        public string Status { get; set; }
        public string Description { get; set; }
        public string Location { get; set; }
    }
}

namespace S1Courier.Core
{
    using S1Courier.Models;

    internal interface ICourierProvider
    {
        string ProviderName { get; }
        int MaxVouchersPerBatch { get; }
        bool SupportsCodChequeDate { get; }
        bool SupportsDeliveryTimeWindow { get; }
        bool SupportsDeliveryTimeRange { get; }
        bool SupportsSaturdayDelivery { get; }
        bool SupportsDeliveryDate { get; }
        Task<ShipmentResult> CreateShipmentAsync(ShipmentRequest request);
        Task<CancelResult> CancelShipmentAsync(string shipmentNumber, string providerJobId = null);
        Task<byte[]> GetVoucherAsync(string shipmentNumber);
        Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options);
        Task<TrackingResult> TrackShipmentAsync(string trackingNumber);
        Task<int> GetDeliveryDaysAsync(string originZip, string destZip);
    }

    internal static class CourierProviderFactory
    {
        public static ICourierProvider Create(CourierProviderConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return new CompatibilityProviderAdapter(
                JarvisCourierProviderFactory.Create(ToJarvisConfig(config)));
        }

        private static JarvisCourierProviderConfig ToJarvisConfig(CourierProviderConfig c)
        {
            return new JarvisCourierProviderConfig
            {
                Id = c.ID,
                ProviderName = c.ProviderName,
                ProviderCode = c.ProviderCode,
                ApiUrl = c.ApiUrl,
                ApiKey = c.ApiKey,
                UserAlias = c.UserAlias,
                CredentialValue = c.CredentialValue,
                AccountCode = c.AccountCode,
                IsActive = c.IsActive,
                CompanyId = c.CompanyId,
                CompanyPassword = c.CompanyPassword,
                UserId = c.UserId,
                UserPassword = c.UserPassword,
                BillingCode = c.BillingCode,
                CodPaywayCode = c.CodPaywayCode,
                Company = c.Company,
                Branch = c.Branch,
                IsDefault = c.IsDefault,
                PrintType = c.PrintType,
                PrintTemplate = c.PrintTemplate,
                SubCode = c.SubCode,
                CustCode = c.CustCode,
                CustUser = c.CustUser,
                CustPass = c.CustPass,
                PelCode = c.PelCode,
                TwoStepAuth = c.TwoStepAuth,
                MaxBatch = c.MaxBatch,
                LastFinalized = c.LastFinalized
            };
        }
    }

    internal sealed class CompatibilityProviderAdapter : ICourierProvider
    {
        private readonly IJarvisCourierProvider _inner;

        public CompatibilityProviderAdapter(IJarvisCourierProvider inner) { _inner = inner; }
        public string ProviderName => _inner.ProviderName;
        public int MaxVouchersPerBatch => _inner.MaxVouchersPerBatch;
        public bool SupportsCodChequeDate => _inner.SupportsCodChequeDate;
        public bool SupportsDeliveryTimeWindow => _inner.SupportsDeliveryTimeWindow;
        public bool SupportsDeliveryTimeRange => _inner.SupportsDeliveryTimeRange;
        public bool SupportsSaturdayDelivery => _inner.SupportsSaturdayDelivery;
        public bool SupportsDeliveryDate => _inner.SupportsDeliveryDate;

        public async Task<ShipmentResult> CreateShipmentAsync(ShipmentRequest r)
        {
            var result = await _inner.CreateShipmentAsync(new JarvisCourierShipmentRequest
            {
                SenderName = r.SenderName,
                SenderAddress = r.SenderAddress,
                SenderCity = r.SenderCity,
                SenderZipCode = r.SenderZipCode,
                SenderPhone = r.SenderPhone,
                ReceiverContactName = r.ReceiverContactName,
                ReceiverName = r.ReceiverName,
                ReceiverAddress = r.ReceiverAddress,
                ReceiverCity = r.ReceiverCity,
                ReceiverZipCode = r.ReceiverZipCode,
                ReceiverPhone = r.ReceiverPhone,
                Pieces = r.Pieces,
                Weight = r.Weight,
                Comments = r.Comments,
                IsCod = r.IsCOD,
                CodAmount = r.CODAmount,
                DocumentRef = r.DocumentRef,
                DocumentNumber = r.DocumentNumber,
                ExistingShipmentNumber = r.ExistingShipmentNumber,
                ExistingProviderCode = r.ExistingProviderCode,
                ExistingJobId = r.ExistingJobId,
                PaymentCode = r.PaymentCode,
                DocumentAmount = r.DocumentAmount,
                ServiceType = r.ServiceType,
                CodPaymentType = r.CODPaymentType,
                CodChequeDate = r.CODChequeDate,
                DeliveryTimeRequested = r.DeliveryTimeRequested,
                DeliveryTimeFrom = r.DeliveryTimeFrom,
                DeliveryTimeTo = r.DeliveryTimeTo,
                DeliveryDate = r.DeliveryDate,
                SaturdayDelivery = r.SaturdayDelivery
            });

            return new ShipmentResult
            {
                Success = result.Success,
                ShipmentNumber = result.ShipmentNumber,
                TrackingNumber = result.TrackingNumber,
                ErrorMessage = result.ErrorMessage,
                Errors = result.Errors == null ? new List<string>() : result.Errors.ToList(),
                ProviderJobId = result.ProviderJobId
            };
        }

        public async Task<CancelResult> CancelShipmentAsync(string shipmentNumber, string providerJobId = null)
        {
            var result = await _inner.CancelShipmentAsync(shipmentNumber, providerJobId);
            return new CancelResult { Success = result.Success, ErrorMessage = result.ErrorMessage };
        }

        public Task<byte[]> GetVoucherAsync(string shipmentNumber) => _inner.GetVoucherAsync(shipmentNumber);
        public Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options) => _inner.GetBatchVoucherAsync(shipmentNumbers, options);

        public async Task<TrackingResult> TrackShipmentAsync(string trackingNumber)
        {
            var result = await _inner.TrackShipmentAsync(trackingNumber);
            return new TrackingResult
            {
                Success = result.Success,
                ErrorMessage = result.ErrorMessage,
                Entries = result.Entries == null ? new List<TrackingEntry>() : result.Entries.Select(x => new TrackingEntry
                {
                    Timestamp = x.Timestamp,
                    Status = x.Status,
                    Description = x.Description,
                    Location = x.Location
                }).ToList()
            };
        }

        public Task<int> GetDeliveryDaysAsync(string originZip, string destZip) => _inner.GetDeliveryDaysAsync(originZip, destZip);
    }
}
