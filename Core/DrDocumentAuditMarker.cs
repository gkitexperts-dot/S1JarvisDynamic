using System;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Applies deterministic Jarvis DR audit metadata to a FINDOC that has
    /// already been created through a Soft1 business object.
    ///
    /// This deliberately runs through the same Soft1 object returned by the
    /// registrar (ITEMDOC/LINSUPDOC/etc.) and never performs a raw SQL UPDATE.
    /// Audit failure must not invalidate an already-created accounting document;
    /// callers receive success/failure and can surface/log it separately.
    /// </summary>
    internal static class DrDocumentAuditMarker
    {
        public const string FlowVersion = "DR-1.0";

        public static bool TryMark(
            XSupport xSupport,
            JObject command,
            JObject registrationResult,
            out string error)
        {
            error = null;
            if (xSupport == null)
            {
                error = "Missing XSupport.";
                return false;
            }

            int findocId = (int?)registrationResult?["findocId"] ?? 0;
            string objectName = registrationResult?["objectName"]?.ToString();
            if (findocId <= 0 || string.IsNullOrWhiteSpace(objectName))
            {
                error = "Registration result does not contain findocId/objectName.";
                return false;
            }

            XModule module = null;
            XTable findoc = null;
            try
            {
                module = xSupport.CreateModule(objectName);
                module.LocateData(findocId);
                findoc = module.GetTable("FINDOC");

                findoc.Current["CCCJARVISDR"] = 1;
                findoc.Current["CCCJARVISVER"] = FlowVersion;

                double? confidence = ResolveConfidence(command, registrationResult);
                if (confidence.HasValue)
                    findoc.Current["CCCJARVISCONF"] = confidence.Value;

                string source = ResolveSource(command);
                if (!string.IsNullOrWhiteSpace(source))
                    findoc.Current["CCCJARVISSOUR"] = Truncate(source, 30);

                string auditRef = BuildAuditReference(command, registrationResult);
                if (!string.IsNullOrWhiteSpace(auditRef))
                    findoc.Current["CCCJARVISREF"] = Truncate(auditRef, 200);

                int posted = module.PostData();
                if (posted <= 0)
                    throw new Exception("Jarvis FINDOC audit PostData returned 0.");

                DebugLog.Log(
                    $"[dr-audit] marked FINDOC={findocId} object={objectName} " +
                    $"version={FlowVersion} confidence={(confidence.HasValue ? confidence.Value.ToString("0.####") : "null")} source={source}");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                DebugLog.Log($"[dr-audit] failed FINDOC={findocId} object={objectName}: " + ex);
                return false;
            }
            finally
            {
                if (findoc != null) findoc.Dispose();
                if (module != null) module.Dispose();
            }
        }

        private static double? ResolveConfidence(JObject command, JObject result)
        {
            JToken token =
                result?["patternConfidence"] ??
                result?["confidence"] ??
                command?["classificationConfidence"] ??
                command?["documentConfidence"] ??
                command?["patternConfidence"] ??
                command?["confidence"];

            if (token == null || token.Type == JTokenType.Null) return null;
            double value;
            if (!double.TryParse(token.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out value))
                return null;

            if (value > 1.0 && value <= 100.0) value /= 100.0;
            if (value < 0.0 || value > 1.0) return null;
            return value;
        }

        private static string ResolveSource(JObject command)
        {
            string source =
                command?["source"]?.ToString() ??
                command?["sourceType"]?.ToString() ??
                command?["documentSource"]?.ToString();

            return string.IsNullOrWhiteSpace(source)
                ? "UPLOAD"
                : source.Trim().ToUpperInvariant();
        }

        private static string BuildAuditReference(JObject command, JObject result)
        {
            string fileId = command?["fileId"]?.ToString();
            string mode = command?["mode"]?.ToString() ?? "auto";
            string strategy = result?["strategyUsed"]?.ToString();
            string precedent =
                command?["precedentFindocId"]?.ToString() ??
                command?["findocPrecedent"]?.ToString();

            var parts = new System.Collections.Generic.List<string>
            {
                FlowVersion,
                "mode=" + mode
            };
            if (!string.IsNullOrWhiteSpace(strategy)) parts.Add("strategy=" + strategy);
            if (!string.IsNullOrWhiteSpace(fileId)) parts.Add("file=" + fileId);
            if (!string.IsNullOrWhiteSpace(precedent)) parts.Add("precedent=" + precedent);
            return string.Join(";", parts);
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value;
            return value.Substring(0, maxLength);
        }
    }
}
