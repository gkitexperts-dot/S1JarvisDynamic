using System;
using Softone;

namespace S1Jarvis.Core
{
    internal sealed class JarvisCompanyContext
    {
        public int CompanyId { get; private set; }
        public int BranchId { get; private set; }
        public string CompanyName { get; private set; }
        public string WiseContext { get; private set; }

        public static JarvisCompanyContext Resolve(XSupport xSupport)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null)
                throw new ArgumentNullException("xSupport", "Soft1 connection context is unavailable.");

            int companyId = xSupport.ConnectionInfo.CompanyId;
            int branchId = xSupport.ConnectionInfo.BranchId;
            string companyName = null;
            string wiseContext = null;

            try
            {
                // The active Soft1 login/session is authoritative. Never infer the
                // current company from TOP 1 or from another COMPANY row.
                XTable company = xSupport.GetSQLDataSet(
                    "SELECT COMPANY, NAME FROM COMPANY WHERE COMPANY=:1",
                    companyId);

                if (company != null && company.Count > 0)
                    companyName = SafeString(company.Current["NAME"]);
            }
            catch (Exception ex)
            {
                // Company identity enrichment is fail-open. CompanyId from the
                // current Soft1 session remains authoritative even if COMPANY
                // metadata cannot be read.
                DebugLog.Log("[COMPANY-CONTEXT] identity lookup failed; companyId=" +
                    companyId + " error=" + ex.Message);
            }

            try
            {
                // Optional Jarvis Wise curated company context. This field is
                // intentionally optional during rollout; older installations must
                // continue working until the Designer field has been deployed.
                XTable wise = xSupport.GetSQLDataSet(
                    "SELECT cccJWContext FROM COMPANY WHERE COMPANY=:1",
                    companyId);

                if (wise != null && wise.Count > 0)
                    wiseContext = SafeString(wise.Current["cccJWContext"]);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[COMPANY-CONTEXT] cccJWContext unavailable; companyId=" +
                    companyId + " error=" + ex.Message);
            }

            var result = new JarvisCompanyContext
            {
                CompanyId = companyId,
                BranchId = branchId,
                CompanyName = companyName,
                WiseContext = wiseContext
            };

            DebugLog.Log("[COMPANY-CONTEXT] companyId=" + companyId +
                " branchId=" + branchId +
                " name=" + (companyName ?? "?") +
                " wiseContext=" + (string.IsNullOrWhiteSpace(wiseContext) ? "none" : "present"));

            return result;
        }

        private static string SafeString(object value)
        {
            if (value == null || value == DBNull.Value)
                return null;

            string text = Convert.ToString(value);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }
}
