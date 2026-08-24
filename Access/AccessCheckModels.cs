using Newtonsoft.Json;

namespace S1Jarvis.Access
{
    // ══════════════════════════════════════════════════════════════════════
    // AccessCheckRequest / AccessCheckResponse
    //
    // Ταυτόσημο σχήμα με S1Courier.Access / S1DocReader.Soft1.Access - ίδιο
    // Nexus API εξυπηρετεί όλα τα tools, το ToolName είναι αυτό που αλλάζει.
    // ══════════════════════════════════════════════════════════════════════
    public class AccessCheckRequest
    {
        [JsonProperty("serial")]
        public string Serial { get; set; }

        [JsonProperty("companyCode")]
        public string CompanyCode { get; set; }

        [JsonProperty("branchCode")]
        public string BranchCode { get; set; }

        [JsonProperty("soft1UserId")]
        public string Soft1UserId { get; set; }

        [JsonProperty("toolName")]
        public string ToolName { get; set; }
    }

    public class AccessCheckResponse
    {
        [JsonProperty("allowed")]
        public bool Allowed { get; set; }

        [JsonProperty("toolName")]
        public string ToolName { get; set; }

        // Opaque δείκτης προς το agent account - ΠΟΤΕ key. Μόνο όταν Allowed.
        [JsonProperty("agentAccountRef")]
        public string AgentAccountRef { get; set; }

        // Non-secret model id selected by the authoritative Jarvis AI config.
        [JsonProperty("aiModel")]
        public string AiModel { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("validUntil")]
        public string ValidUntil { get; set; }

        public static AccessCheckResponse Deny(string toolName, string message = null)
            => new AccessCheckResponse { Allowed = false, ToolName = toolName, Message = message };
    }
}
