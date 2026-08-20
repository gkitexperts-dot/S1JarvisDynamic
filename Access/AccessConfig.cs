namespace S1Jarvis.Access
{
    // Ίδιο Nexus endpoint/client key με το S1Courier (S1Courier.Access.AccessConfig)
    // - μοιράζεται μεταξύ όλων των δικών μας tools, μόνο το ToolName αλλάζει.
    internal static class AccessConfig
    {
        public const string ServiceUrl = "https://nexus-itexperts-api.azurewebsites.net";

        public const string ClientKey = "1762d861cd25537eaf0bb97798291c13384b4a55b9eaca159008db9142dece28";

        // Πρέπει να ταιριάζει ΑΚΡΙΒΩΣ με το Entitlement.ToolName στο Nexus.
        public const string ToolName = "S1JARVIS";

        // ΝΕΟ 15/08 - ξεχωριστό entitlement για το DR feature (βλ. README
        // Roadmap #6). ΔΙΟΡΘΩΘΗΚΕ 15/08: ΟΧΙ "DOCREADER" (αυτό είναι το
        // toolName του standalone S1DocReader WPF προϊόντος - ξεχωριστό
        // εμπορικό προϊόν, δικός του agent, δικό του entitlement). Το
        // "JARVISDOCREADER" είναι καθαρό feature-gate (επιτρέπεται/όχι) -
        // ΔΕΝ χρειάζεται δικό του AI agent στο Nexus, μιας και οι AI
        // κλήσεις δρομολογούνται μέσω του ΗΔΗ υπάρχοντος agent account του
        // Jarvis (_agentAccountRef, από το S1JARVIS entitlement) - ο ίδιος
        // ο Jarvis ΕΙΝΑΙ ο agent.
        public const string DocReaderToolName = "JARVISDOCREADER";

        // ΝΕΟ 17/08, ρητό αίτημα χρήστη - φέρνει τον S1Courier (courier
        // vouchers ACS/ΕΛΤΑ/Γενική/Courier Center) μέσα στον Jarvis, σαν
        // ΞΕΧΩΡΙΣΤΟ entitlement από το standalone προϊόν S1COURIER (βλ.
        // S1Courier.Access.AccessConfig.ToolName - ΔΙΑΦΟΡΕΤΙΚΟ SKU, ίδιο
        // σκεπτικό με JARVISDOCREADER vs DOCREADER πιο πάνω). Reuse των
        // ΙΔΙΩΝ provider classes (ACSProvider κ.λπ., βλ. S1Courier.dll
        // reference στο csproj) - ΜΟΝΟ το entitlement/UI είναι ξεχωριστό.
        public const string CourierToolName = "JARVISCOURIER";
    }
}
