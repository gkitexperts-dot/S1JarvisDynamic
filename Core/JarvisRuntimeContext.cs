using System;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Authoritative identity/runtime facts for one active Soft1/Jarvis session.
    /// The context is registered once when the Jarvis shell is activated and then
    /// reused by decomposition, orchestration and executors. It is never inferred
    /// by a model and must not be re-requested from the operator.
    /// </summary>
    internal sealed class JarvisRuntimeContext
    {
        private static readonly ConditionalWeakTable<XSupport, JarvisRuntimeContext> Sessions =
            new ConditionalWeakTable<XSupport, JarvisRuntimeContext>();
        private static readonly object Sync = new object();

        internal int CurrentUserId { get; private set; }
        internal int CurrentCompanyId { get; private set; }
        internal string CurrentUserDisplayName { get; private set; }
        internal int CurrentInterlocutorUserId { get; private set; }
        internal DateTime ActivatedAtLocal { get; private set; }
        internal DateTime LocalNow { get { return DateTime.Now; } }

        internal static JarvisRuntimeContext StartSession(XSupport xSupport)
        {
            if (xSupport == null) return Create(null);
            lock (Sync)
            {
                JarvisRuntimeContext existing;
                if (Sessions.TryGetValue(xSupport, out existing)) return existing;
                JarvisRuntimeContext created = Create(xSupport);
                Sessions.Add(xSupport, created);
                DebugLog.Log("[JARVIS-SESSION] activated currentUserId=" + created.CurrentUserId +
                    " currentCompanyId=" + created.CurrentCompanyId +
                    " displayName=" + (created.CurrentUserDisplayName ?? string.Empty));
                return created;
            }
        }

        internal static JarvisRuntimeContext Capture(XSupport xSupport)
        {
            if (xSupport == null) return Create(null);
            lock (Sync)
            {
                JarvisRuntimeContext existing;
                if (Sessions.TryGetValue(xSupport, out existing)) return existing;
            }
            // Compatibility fallback for a non-shell call path. Normal Main Chat
            // always registers the session explicitly from JarvisShell.Loaded.
            return StartSession(xSupport);
        }

        private static JarvisRuntimeContext Create(XSupport xSupport)
        {
            var context = new JarvisRuntimeContext
            {
                ActivatedAtLocal = DateTime.Now,
                CurrentUserDisplayName = string.Empty
            };
            if (xSupport == null || xSupport.ConnectionInfo == null) return context;

            context.CurrentUserId = xSupport.ConnectionInfo.UserId;
            context.CurrentInterlocutorUserId = context.CurrentUserId;
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
                ["currentInterlocutorUserId"] = CurrentInterlocutorUserId,
                ["currentUserDisplayName"] = CurrentUserDisplayName ?? string.Empty,
                ["currentCompanyId"] = CurrentCompanyId,
                ["activatedAtLocal"] = ActivatedAtLocal.ToString("o"),
                ["localDateTime"] = LocalNow.ToString("o")
            };
        }

        internal string BuildEnvelope()
        {
            return "[JARVIS_RUNTIME_CONTEXT]\n" + ToJson().ToString(Formatting.None);
        }
    }
}
