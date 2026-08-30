using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Shell-scoped pending confirmation state for the controlled orchestration path.
    /// The payload shown/confirmed is frozen after upstream validation. Confirmation
    /// is accepted only while the coordinator still materializes the exact same
    /// payload. This class never dispatches an executor.
    /// </summary>
    internal sealed class JarvisPendingConfirmationSession
    {
        private readonly object _sync = new object();
        private JarvisExecutionCoordinator _coordinator;
        private string _objectId;
        private JObject _frozenPayload;
        private string _payloadHash;

        internal bool HasPending
        {
            get { lock (_sync) return _coordinator != null && !string.IsNullOrWhiteSpace(_objectId); }
        }

        internal string PendingObjectId
        {
            get { lock (_sync) return _objectId; }
        }

        internal string PayloadHash
        {
            get { lock (_sync) return _payloadHash; }
        }

        internal JObject FrozenPayload
        {
            get { lock (_sync) return _frozenPayload == null ? null : (JObject)_frozenPayload.DeepClone(); }
        }

        internal bool TryCapture(JarvisExecutionCoordinator coordinator, out string[] issues)
        {
            return TryCapture(coordinator, null, out issues);
        }

        internal bool TryCapture(JarvisExecutionCoordinator coordinator, string objectId, out string[] issues)
        {
            var localIssues = new System.Collections.Generic.List<string>();
            if (coordinator == null)
            {
                localIssues.Add("Cannot capture confirmation session without an execution coordinator.");
                issues = localIssues.ToArray();
                return false;
            }

            var waiting = coordinator.Inspect().Steps
                .Where(x => x != null && x.State == JarvisExecutionStepState.WaitingForConfirmation);

            JarvisExecutionStepSnapshot pending;
            if (string.IsNullOrWhiteSpace(objectId))
            {
                pending = waiting.OrderBy(x => x.Ordinal).FirstOrDefault();
            }
            else
            {
                pending = waiting.FirstOrDefault(x => string.Equals(x.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
            }

            if (pending == null)
            {
                localIssues.Add(string.IsNullOrWhiteSpace(objectId)
                    ? "No execution task is waiting for confirmation."
                    : "Requested execution task is not waiting for confirmation: " + objectId);
                issues = localIssues.ToArray();
                return false;
            }

            JObject payload;
            string[] inputIssues;
            if (!coordinator.TryGetDispatchInputs(pending.ObjectId, out payload, out inputIssues))
                localIssues.AddRange(inputIssues ?? new string[0]);

            if (localIssues.Count > 0)
            {
                issues = localIssues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return false;
            }

            JObject frozen = ToExternalActionPayload(payload);
            string hash = ComputePayloadHash(frozen);
            lock (_sync)
            {
                _coordinator = coordinator;
                _objectId = pending.ObjectId;
                _frozenPayload = frozen;
                _payloadHash = hash;
            }

            issues = new string[0];
            return true;
        }

        internal bool TryConfirm(string userText, out JarvisExecutionCoordinator coordinator, out string objectId, out string payloadHash, out string[] issues)
        {
            coordinator = null;
            objectId = null;
            payloadHash = null;
            var localIssues = new System.Collections.Generic.List<string>();

            if (!IsAffirmativeConfirmation(userText))
            {
                localIssues.Add("Message is not an affirmative confirmation.");
                issues = localIssues.ToArray();
                return false;
            }

            JarvisExecutionCoordinator current;
            string currentObjectId;
            JObject frozen;
            string frozenHash;
            lock (_sync)
            {
                current = _coordinator;
                currentObjectId = _objectId;
                frozen = _frozenPayload == null ? null : (JObject)_frozenPayload.DeepClone();
                frozenHash = _payloadHash;
            }

            if (current == null || string.IsNullOrWhiteSpace(currentObjectId) || frozen == null)
            {
                localIssues.Add("No pending confirmation session exists.");
                issues = localIssues.ToArray();
                return false;
            }

            JObject rematerialized;
            string[] materializationIssues;
            if (!current.TryGetDispatchInputs(currentObjectId, out rematerialized, out materializationIssues))
                localIssues.AddRange(materializationIssues ?? new string[0]);

            JObject currentExternalPayload = ToExternalActionPayload(rematerialized);
            string currentHash = ComputePayloadHash(currentExternalPayload);
            if (!string.Equals(currentHash, frozenHash, StringComparison.Ordinal))
                localIssues.Add("Confirmation payload changed after it was frozen; confirmation rejected.");

            if (localIssues.Count == 0)
            {
                string[] confirmationIssues;
                if (!current.GrantExplicitConfirmation(currentObjectId, out confirmationIssues))
                    localIssues.AddRange(confirmationIssues ?? new string[0]);
            }

            if (localIssues.Count > 0)
            {
                issues = localIssues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return false;
            }

            coordinator = current;
            objectId = currentObjectId;
            payloadHash = frozenHash;
            issues = new string[0];
            return true;
        }

        internal void Clear()
        {
            lock (_sync)
            {
                _coordinator = null;
                _objectId = null;
                _frozenPayload = null;
                _payloadHash = null;
            }
        }

        internal static bool IsAffirmativeConfirmation(string userText)
        {
            string value = (userText ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0)
                return false;

            return value == "ναι" || value == "yes" || value == "ok" || value == "οκ" ||
                   value.Contains("επιβεβαιωνω") || value.Contains("επιβεβαιώνω") ||
                   value.Contains("ναι στειλ") || value.Contains("ναι, στειλ") ||
                   value.Contains("ναι στείλ") || value.Contains("ναι, στείλ") ||
                   value.Contains("στειλτο") || value.Contains("στείλτο") ||
                   value.Contains("προχώρα") || value.Contains("προχωρα");
        }

        private static JObject ToExternalActionPayload(JObject payload)
        {
            JObject result = payload == null ? new JObject() : (JObject)payload.DeepClone();
            string[] internalNames = result.Properties()
                .Where(x => x.Name.StartsWith("__", StringComparison.Ordinal))
                .Select(x => x.Name)
                .ToArray();
            foreach (string name in internalNames)
                result.Remove(name);
            return result;
        }

        private static string ComputePayloadHash(JObject payload)
        {
            string canonical = (payload ?? new JObject()).ToString(Formatting.None);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                    builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
