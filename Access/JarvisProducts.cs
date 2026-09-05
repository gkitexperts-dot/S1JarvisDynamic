namespace S1Jarvis.Access
{
    /// <summary>
    /// Stable commercial product codes used by the Jarvis host when mapping
    /// runtime capabilities to Verilic products. These are product identities,
    /// not secrets, credentials, licence IDs or installation IDs.
    /// </summary>
    internal static class JarvisProducts
    {
        public const string Jarvis = "S1JARVIS";
        public const string JarvisCourier = "S1JARVISCOURIER";
        public const string JarvisDocReader = "S1JARVISDOCREADER";

        public static bool RequiresJarvisParent(string productCode)
        {
            return string.Equals(productCode, JarvisCourier, System.StringComparison.Ordinal) ||
                   string.Equals(productCode, JarvisDocReader, System.StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Compatibility constants for legacy UI/bridge call sites that have not yet
    /// been renamed. This carries no licence credential and no client key.
    /// Product authorization remains exclusively on NativeS1 /verify.
    /// Kept in this already-established compilation unit so old-style MSBuild
    /// project evaluation cannot miss it after a branch update.
    /// </summary>
    internal static class AccessConfig
    {
        public const string CourierToolName = JarvisProducts.JarvisCourier;
        public const string DocReaderToolName = JarvisProducts.JarvisDocReader;
        public const string ServiceUrl = "https://s1jarvis.local";
        public const string ClientKey = "";
    }
}
