using System;
using System.Collections.Generic;
using Softone;

namespace S1Jarvis.Core
{
    internal static class JarvisParameterAudit
    {
        internal sealed class Result
        {
            public bool TableAvailable { get; set; }
            public List<int> MissingRequired { get; } = new List<int>();
            public List<int> InvalidRequired { get; } = new List<int>();
        }

        private static readonly int[] RequiredNumeric = { 500008, 500012, 500017 };
        private static readonly int[] RuntimeRequiredString = { 500060, 500061 };
        private static readonly int[] FeatureScopedRequiredString = { 500019, 500020, 500021 };

        public static Result Run(XSupport xSupport)
        {
            var result = new Result();
            try
            {
                if (xSupport == null)
                {
                    DebugLog.Log("[PARAM-AUDIT] skipped: XSupport unavailable");
                    return result;
                }

                JarvisToolInventoryReconciler.RunAndLog();
                RunTaskRegistryAudit();

                XTable table;
                try
                {
                    table = xSupport.GetSQLDataSet("SELECT TOP 1 ParamCode FROM cccParams");
                    result.TableAvailable = table != null;
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[PARAM-AUDIT] cccParams unavailable: " + ex.GetType().Name + " - " + ex.Message);
                    return result;
                }

                foreach (int code in RequiredNumeric)
                    AuditNumeric(xSupport, code, true, result);

                foreach (int code in RuntimeRequiredString)
                    AuditString(xSupport, code, true, result);

                foreach (int code in FeatureScopedRequiredString)
                    AuditString(xSupport, code, false, result);

                DebugLog.Log(
                    "[PARAM-AUDIT] completed. table=" + result.TableAvailable +
                    " missingRequired=" + Join(result.MissingRequired) +
                    " invalidRequired=" + Join(result.InvalidRequired));
            }
            catch (Exception ex)
            {
                try { DebugLog.Log("[PARAM-AUDIT] unexpected audit failure: " + ex); } catch { }
            }

            return result;
        }

        private static void RunTaskRegistryAudit()
        {
            try
            {
                string[] issues = JarvisTaskRegistryAudit.Validate();
                if (issues == null || issues.Length == 0)
                {
                    DebugLog.Log("[TASK-REGISTRY] reconciliation OK tasks=" + JarvisTaskRegistry.AllTasks.Count);
                    return;
                }

                DebugLog.Log("[TASK-REGISTRY] reconciliation issues=" + issues.Length + " :: " + string.Join(" | ", issues));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[TASK-REGISTRY] audit failed: " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        private static void AuditNumeric(XSupport xSupport, int code, bool globallyRequired, Result result)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 ParamValue FROM cccParams WHERE ParamCode=:1", code);
                if (t == null || t.Count == 0 || t.Current["ParamValue"] == null || t.Current["ParamValue"] == DBNull.Value)
                {
                    if (globallyRequired) result.MissingRequired.Add(code);
                    DebugLog.Log("[PARAM-AUDIT] missing numeric param " + code +
                                 (globallyRequired ? " (required by feature)" : " (optional)"));
                    return;
                }

                int value;
                if (!int.TryParse(Convert.ToString(t.Current["ParamValue"]), out value))
                {
                    if (globallyRequired) result.InvalidRequired.Add(code);
                    DebugLog.Log("[PARAM-AUDIT] invalid numeric param " + code);
                }
            }
            catch (Exception ex)
            {
                if (globallyRequired) result.InvalidRequired.Add(code);
                DebugLog.Log("[PARAM-AUDIT] numeric param " + code + " read failed: " + ex.Message);
            }
        }

        private static void AuditString(XSupport xSupport, int code, bool globallyRequired, Result result)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT TOP 1 ParamValueString FROM cccParams WHERE ParamCode=:1", code);
                string value = t == null || t.Count == 0 || t.Current["ParamValueString"] == null ||
                               t.Current["ParamValueString"] == DBNull.Value
                    ? null
                    : Convert.ToString(t.Current["ParamValueString"]);

                if (string.IsNullOrWhiteSpace(value))
                {
                    if (globallyRequired) result.MissingRequired.Add(code);
                    DebugLog.Log("[PARAM-AUDIT] missing string param " + code +
                                 (globallyRequired
                                     ? " (required for Verilic runtime boot)"
                                     : " (feature-scoped; Jarvis boot continues)"));
                }
            }
            catch (Exception ex)
            {
                if (globallyRequired) result.InvalidRequired.Add(code);
                DebugLog.Log("[PARAM-AUDIT] string param " + code + " read failed: " + ex.Message);
            }
        }

        private static string Join(List<int> values)
        {
            return values == null || values.Count == 0 ? "none" : string.Join(",", values);
        }
    }
}
