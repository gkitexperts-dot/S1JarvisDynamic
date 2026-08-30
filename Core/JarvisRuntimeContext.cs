using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Authenticated runtime facts from the active Soft1 session. These facts are
    /// not inferred by the model and must never be requested again from the user
    /// when they are already available here.
    /// </summary>
    internal sealed class JarvisRuntimeContext
    {
        internal int CurrentUserId { get; private set; }
        internal int CurrentCompanyId { get; private set; }
        internal string CurrentUserDisplayName { get; private set; }
        internal DateTime LocalNow { get; private set; }

        internal static JarvisRuntimeContext Capture(XSupport xSupport)
        {
            var context = new JarvisRuntimeContext { LocalNow = DateTime.Now };
            if (xSupport == null || xSupport.ConnectionInfo == null) return context;

            context.CurrentUserId = xSupport.ConnectionInfo.UserId;
            context.CurrentCompanyId = xSupport.ConnectionInfo.CompanyId;
            try { context.CurrentUserDisplayName = JarvisTools.GetCurrentUserDisplayName(xSupport); }
            catch { context.CurrentUserDisplayName = string.Empty; }
            return context;
        }

        internal JObject ToJson()
        {
            return new JObject
            {
                ["source"] = "authenticated_soft1_session",
                ["currentUserId"] = CurrentUserId,
                ["currentUserDisplayName"] = CurrentUserDisplayName ?? string.Empty,
                ["currentCompanyId"] = CurrentCompanyId,
                ["localDateTime"] = LocalNow.ToString("o")
            };
        }

        internal string BuildEnvelope()
        {
            return "[JARVIS_RUNTIME_CONTEXT]\n" + ToJson().ToString(Formatting.None);
        }
    }
}
