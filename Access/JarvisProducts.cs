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
}
