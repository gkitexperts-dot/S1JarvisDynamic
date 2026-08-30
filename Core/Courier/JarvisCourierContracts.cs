using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace S1Jarvis.Core.Courier
{
    internal interface IJarvisCourierProvider
    {
        string ProviderName { get; }
        int MaxVouchersPerBatch { get; }
        bool SupportsCodChequeDate { get; }
        bool SupportsDeliveryTimeWindow { get; }
        bool SupportsDeliveryTimeRange { get; }
        bool SupportsSaturdayDelivery { get; }
        bool SupportsDeliveryDate { get; }

        Task<JarvisCourierShipmentResult> CreateShipmentAsync(JarvisCourierShipmentRequest request);
        Task<JarvisCourierCancelResult> CancelShipmentAsync(string shipmentNumber, string providerJobId = null);
        Task<byte[]> GetVoucherAsync(string shipmentNumber);
        Task<byte[]> GetBatchVoucherAsync(List<string> shipmentNumbers, string options);
        Task<JarvisCourierTrackingResult> TrackShipmentAsync(string trackingNumber);
        Task<int> GetDeliveryDaysAsync(string originZip, string destZip);
    }

    internal sealed class JarvisCourierProviderConfig
    {
        public int Id { get; set; }
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

    internal sealed class JarvisCourierShipmentRequest
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
        public bool IsCod { get; set; }
        public decimal CodAmount { get; set; }
        public string DocumentRef { get; set; }
        public string DocumentNumber { get; set; }
        public string ExistingShipmentNumber { get; set; }
        public string ExistingProviderCode { get; set; }
        public string ExistingJobId { get; set; }
        public string PaymentCode { get; set; }
        public decimal DocumentAmount { get; set; }
        public string ServiceType { get; set; } = "1";
        public int CodPaymentType { get; set; }
        public DateTime? CodChequeDate { get; set; }
        public bool DeliveryTimeRequested { get; set; }
        public TimeSpan? DeliveryTimeFrom { get; set; }
        public TimeSpan? DeliveryTimeTo { get; set; }
        public DateTime? DeliveryDate { get; set; }
        public bool SaturdayDelivery { get; set; }
    }

    internal sealed class JarvisCourierShipmentResult
    {
        public bool Success { get; set; }
        public string ShipmentNumber { get; set; }
        public string TrackingNumber { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public string ProviderJobId { get; set; }
    }

    internal sealed class JarvisCourierCancelResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
    }

    internal sealed class JarvisCourierTrackingResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public List<JarvisCourierTrackingEntry> Entries { get; set; } = new List<JarvisCourierTrackingEntry>();
    }

    internal sealed class JarvisCourierTrackingEntry
    {
        public DateTime Timestamp { get; set; }
        public string Status { get; set; }
        public string Description { get; set; }
        public string Location { get; set; }
    }
}
