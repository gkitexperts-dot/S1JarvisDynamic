namespace S1Jarvis.Access
{
    /// <summary>
    /// Compatibility constants for legacy UI/bridge call sites that have not yet
    /// been renamed. This class carries no licence credential and no client key.
    /// Product authorization is handled exclusively through JarvisProducts and
    /// the NativeS1 /api/licensing/v1/verify flow.
    /// </summary>
    internal static class AccessConfig
    {
        public const string CourierToolName = JarvisProducts.JarvisCourier;
        public const string DocReaderToolName = JarvisProducts.JarvisDocReader;

        // JarvisAgentClient's legacy /agent/vision call is intercepted locally by
        // VerilicDrVisionBridgeHandler. Using the local virtual origin keeps this
        // path fail-closed if the bridge is not installed; no legacy remote proxy
        // credential is restored.
        public const string ServiceUrl = "https://s1jarvis.local";
        public const string ClientKey = "";
    }
}
