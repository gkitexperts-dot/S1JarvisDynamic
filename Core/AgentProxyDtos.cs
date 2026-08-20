using Newtonsoft.Json;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // Ταυτόσημο σχήμα με S1DocReader.Soft1.Access.VisionProxyRequest/Response
    // (Nexus repo) - ο client στέλνει το ΕΤΟΙΜΟ Anthropic request body σαν
    // raw JSON string + agentAccountRef, ο proxy βάζει το πραγματικό key.
    // ══════════════════════════════════════════════════════════════════════
    public class AgentProxyRequest
    {
        [JsonProperty("agentAccountRef")]
        public string AgentAccountRef { get; set; }

        [JsonProperty("anthropicRequestJson")]
        public string AnthropicRequestJson { get; set; }
    }

    public class AgentProxyResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("responseText")]
        public string ResponseText { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }

        [JsonProperty("creditsExhausted")]
        public bool CreditsExhausted { get; set; }

        [JsonProperty("usageInputTokens")]
        public int UsageInputTokens { get; set; }

        [JsonProperty("usageOutputTokens")]
        public int UsageOutputTokens { get; set; }

        // Το ΩΜΟ Anthropic response JSON - εδώ διαβάζουμε tool_use blocks,
        // stop_reason, κλπ. Κενό string αν η κλήση απέτυχε πριν φτάσει σε
        // valid response.
        [JsonProperty("rawResponseJson")]
        public string RawResponseJson { get; set; } = "";
    }
}
