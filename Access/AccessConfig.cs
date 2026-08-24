namespace S1Jarvis.Access
{
    // Legacy Nexus endpoint/client key retained only for the legacy access mode.
    // In Verilic mode the runtime product identities below must match the stable
    // commercial Jarvis product codes used by JarvisProducts and the local
    // Verilic configuration. Keeping a second set of literal SKU names here
    // caused Courier/DocReader runtime verification to ask for obsolete
    // JARVISCOURIER/JARVISDOCREADER names after activation had correctly used
    // S1JARVISCOURIER/S1JARVISDOCREADER.
    internal static class AccessConfig
    {
        public const string ServiceUrl = "https://nexus-itexperts-api.azurewebsites.net";

        public const string ClientKey = "1762d861cd25537eaf0bb97798291c13384b4a55b9eaca159008db9142dece28";

        // One source of truth for the Jarvis product family. These constants are
        // also the keys used by VerilicRuntimeConfiguration and the protected
        // installation state store.
        public const string ToolName = JarvisProducts.Jarvis;
        public const string DocReaderToolName = JarvisProducts.JarvisDocReader;
        public const string CourierToolName = JarvisProducts.JarvisCourier;
    }
}
