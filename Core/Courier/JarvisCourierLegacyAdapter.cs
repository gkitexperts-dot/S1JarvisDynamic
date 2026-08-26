using System;

namespace S1Jarvis.Core.Courier
{
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
            switch (code)
            {
                case "COURIER CENTER":
                    return new JarvisCourierCenterProvider(config);
                case "ELTA COURIER":
                    return new JarvisEltaCourierProvider(config);
                case "ACS COURIER":
                    return new JarvisAcsCourierProvider(config);
                case "GENIKI TAXYDROMIKI":
                    return new JarvisGenikiCourierProvider(config);
                default:
                    throw new NotSupportedException("Ο provider '" + config.ProviderCode + "' δεν υποστηρίζεται από το Jarvis Courier.");
            }
        }
    }
}
