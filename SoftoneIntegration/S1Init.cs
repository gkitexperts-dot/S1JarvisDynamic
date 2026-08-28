using System;
using Softone;

namespace S1Jarvis.SoftoneIntegration
{
    // Soft1 may load the same S1Jarvis.dll more than once from different
    // deployment/cache locations. Static fields are isolated per loaded
    // assembly instance, while AppDomain data is shared inside the same
    // Soft1 process. Keep both paths so the normal single-load On-Premise
    // case stays fast and duplicate-load environments remain safe.
    public class S1Init : TXCode
    {
        public override void Initialize()
        {
            base.Initialize();
            JarvisCore.SetXSupport(XSupport);
        }
    }

    public static class JarvisCore
    {
        private const string XSupportAppDomainKey = "S1Jarvis.Shared.XSupport";

        public static XSupport XSupport { get; private set; }

        public static void SetXSupport(XSupport xSupport)
        {
            XSupport = xSupport;

            if (xSupport != null)
                AppDomain.CurrentDomain.SetData(XSupportAppDomainKey, xSupport);
        }

        public static XSupport GetXSupport()
        {
            if (XSupport != null)
                return XSupport;

            try
            {
                var shared = AppDomain.CurrentDomain.GetData(XSupportAppDomainKey) as XSupport;
                if (shared != null)
                {
                    XSupport = shared;
                    return shared;
                }
            }
            catch
            {
                // Caller reports missing XSupport through the normal startup diagnostics.
            }

            return null;
        }
    }
}
