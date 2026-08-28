using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Newtonsoft.Json;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// WelcomeStores-specific inventory/configuration read model.
    ///
    /// Configuration (see PARAMS.md):
    /// 500060 / ParamValueString = comma separated participating COMPANY ids
    /// 500061 / ParamValue       = master catalogue COMPANY id
    ///
    /// Purchase order series is an explicit installation rule: SERIES=1021
    /// for PURDOC SOSOURCE=1251 in every participating company.
    ///
    /// This service remains read-only. Supplier/order writes are delegated to
    /// the already-audited JarvisTools Soft1 object flows.
    /// </summary>
    internal static class WelcomeStoresInventoryService
    {
        internal const int StockCompaniesParamCode = 500060;
        internal const int MasterCompanyParamCode = 500061;
        internal const int PurchaseOrderSosource = 1251;
        internal const int PurchaseOrderSeries = 1021;

        internal sealed class Config
        {
            public int MasterCompany { get; set; }
            public int[] StockCompanies { get; set; }
        }

        internal sealed class ItemSearchRow
        {
            public int Mtrl { get; set; }
            public string Code { get; set; }
            public string Name { get; set; }
        }

        internal sealed class StockRow
        {
            public int StoreCompany { get; set; }
            public string StoreName { get; set; }
            public string Afm { get; set; }
            public string Phone { get; set; }
            public int Whouse { get; set; }
            public string WarehouseName { get; set; }
            public string ItemCode { get; set; }
            public string ItemName { get; set; }
            public decimal Available { get; set; }
            public bool IsCurrentStore { get; set; }
            public bool SupplierExists { get; set; }
            public int? SupplierTrdr { get; set; }
        }

        internal static Config GetConfig(XSupport xSupport)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));

            string companyList = ReadRequiredParamString(xSupport, StockCompaniesParamCode);
            int[] companies = ParseCompanyIds(companyList);
            if (companies.Length == 0)
                throw new Exception("Η παράμετρος " + StockCompaniesParamCode + " (WelcomeStores Stock Companies) δεν περιέχει έγκυρες εταιρίες.");

            int masterCompany = ReadRequiredParamInt(xSupport, MasterCompanyParamCode);
            if (masterCompany <= 0)
                throw new Exception("Η παράμετρος " + MasterCompanyParamCode + " (WelcomeStores Master Item Company) δεν περιέχει έγκυρη εταιρία.");

            return new Config
            {
                MasterCompany = masterCompany,
                StockCompanies = companies
            };
        }

        internal static IReadOnlyList<ItemSearchRow> SearchMasterItems(
            XSupport xSupport,
            string searchText,
            int maxRows = 30)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(searchText))
                return new ItemSearchRow[0];

            Config config = GetConfig(xSupport);
            if (maxRows <= 0) maxRows = 30;
            if (maxRows > 100) maxRows = 100;

            string sql =
                "DECLARE @MasterCompany INT=:1; " +
                "DECLARE @Contains VARCHAR(250)=:2; " +
                "DECLARE @Exact VARCHAR(250)=:3; " +
                "DECLARE @Starts VARCHAR(250)=:4; " +
                "SELECT TOP " + maxRows + " " +
                "M.MTRL, M.CODE, M.NAME " +
                "FROM MTRL M " +
                "LEFT JOIN MTREXTRA X ON X.MTRL=M.MTRL " +
                "WHERE M.COMPANY=@MasterCompany " +
                "AND M.ISACTIVE=1 " +
                "AND (X.BOOL02=1 OR X.BOOL02 IS NULL) " +
                "AND (M.CODE LIKE @Contains OR M.NAME LIKE @Contains) " +
                "ORDER BY CASE WHEN M.CODE=@Exact THEN 0 WHEN M.CODE LIKE @Starts THEN 1 ELSE 2 END, M.CODE";

            string normalized = searchText.Trim();
            XTable table = xSupport.GetSQLDataSet(
                sql,
                config.MasterCompany,
                "%" + normalized + "%",
                normalized,
                normalized + "%");

            return ReadRows(table, row => new ItemSearchRow
            {
                Mtrl = ToInt(row, "MTRL"),
                Code = ToStringValue(row, "CODE"),
                Name = ToStringValue(row, "NAME")
            });
        }

        internal static IReadOnlyList<StockRow> GetStoreAvailability(
            XSupport xSupport,
            string itemCode)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(itemCode))
                return new StockRow[0];

            Config config = GetConfig(xSupport);
            int currentCompany = xSupport.ConnectionInfo.CompanyId;
            string companiesSql = string.Join(",", config.StockCompanies.Select(x => x.ToString()).ToArray());

            string sql =
                "DECLARE @CurrentCompany INT=:1; " +
                "DECLARE @ItemCode VARCHAR(100)=:2; " +
                "SELECT " +
                "A.COMP AS StoreCompany, " +
                "COMPANY.NAME AS StoreName, " +
                "COMPANY.AFM AS AFM, " +
                "CAST(NULL AS VARCHAR(100)) AS Phone, " +
                "A.WHOUSE, WHOUSE.NAME AS WarehouseName, " +
                "A.CODE AS ItemCode, A.NAME AS ItemName, " +
                "CAST(ISNULL(A.REMAIN,0)-ISNULL(A.SoReserved,0) AS DECIMAL(18,4)) AS Available, " +
                "CASE WHEN A.COMP=@CurrentCompany THEN 1 ELSE 0 END AS IsCurrentStore, " +
                "CASE WHEN SUP.TRDR IS NULL THEN 0 ELSE 1 END AS SupplierExists, " +
                "SUP.TRDR AS SupplierTRDR " +
                "FROM CCCVIEWMTRDATA A " +
                "LEFT JOIN MTRL B ON A.MTRL=B.MTRL " +
                "LEFT JOIN MTREXTRA C ON B.MTRL=C.MTRL " +
                "INNER JOIN COMPANY ON COMPANY.COMPANY=A.COMP " +
                "INNER JOIN WHOUSE ON WHOUSE.COMPANY=A.COMP AND WHOUSE.WHOUSE=A.WHOUSE " +
                "OUTER APPLY (" +
                "  SELECT TOP 1 T.TRDR " +
                "  FROM TRDR T " +
                "  WHERE T.COMPANY=@CurrentCompany " +
                "    AND T.SODTYPE=12 " +
                "    AND T.AFM=COMPANY.AFM " +
                "  ORDER BY T.TRDR" +
                ") SUP " +
                "WHERE A.COMP IN (" + companiesSql + ") " +
                "AND A.FISCPRD=YEAR(GETDATE()) " +
                "AND B.ISACTIVE=1 " +
                "AND C.BOOL02=1 " +
                "AND A.CODE=@ItemCode " +
                "AND (ISNULL(A.REMAIN,0)-ISNULL(A.SoReserved,0))>2 " +
                "ORDER BY A.COMP, A.WHOUSE, A.CODE";

            XTable table = xSupport.GetSQLDataSet(sql, currentCompany, itemCode.Trim());
            return ReadRows(table, row => new StockRow
            {
                StoreCompany = ToInt(row, "StoreCompany"),
                StoreName = ToStringValue(row, "StoreName"),
                Afm = ToStringValue(row, "AFM"),
                Phone = ToStringValue(row, "Phone"),
                Whouse = ToInt(row, "WHOUSE"),
                WarehouseName = ToStringValue(row, "WarehouseName"),
                ItemCode = ToStringValue(row, "ItemCode"),
                ItemName = ToStringValue(row, "ItemName"),
                Available = ToDecimal(row, "Available"),
                IsCurrentStore = ToInt(row, "IsCurrentStore") == 1,
                SupplierExists = ToInt(row, "SupplierExists") == 1,
                SupplierTrdr = ToNullableInt(row, "SupplierTRDR")
            });
        }

        internal static int ResolveCurrentCompanyMtrl(XSupport xSupport, string itemCode)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (string.IsNullOrWhiteSpace(itemCode))
                throw new Exception("Λείπει ο κωδικός είδους για την παραγγελία.");

            XTable table = xSupport.GetSQLDataSet(
                "SELECT TOP 2 MTRL FROM MTRL WHERE COMPANY=:1 AND CODE=:2 AND ISACTIVE=1 ORDER BY MTRL",
                xSupport.ConnectionInfo.CompanyId,
                itemCode.Trim());

            if (table == null || table.Count == 0)
                throw new Exception(
                    "Το είδος " + itemCode.Trim() + " δεν είναι ανοιγμένο στην τρέχουσα εταιρία. " +
                    "Δεν μπορεί να δημιουργηθεί PURDOC μέχρι να υπάρχει τοπικό MTRL.");

            DataTable data = table.CreateDataTable(true);
            if (data.Rows.Count > 1)
                throw new Exception(
                    "Βρέθηκαν περισσότερα από ένα ενεργά είδη με κωδικό " + itemCode.Trim() +
                    " στην τρέχουσα εταιρία. Η παραγγελία σταμάτησε για αποφυγή λάθους MTRL.");

            return Convert.ToInt32(data.Rows[0]["MTRL"]);
        }

        internal static int ResolvePurchaseOrderSeries(XSupport xSupport)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable table = xSupport.GetSQLDataSet(
                "SELECT TOP 1 SERIES FROM SERIES WHERE COMPANY=:1 AND SERIES=:2 AND SOSOURCE=:3",
                company,
                PurchaseOrderSeries,
                PurchaseOrderSosource);

            if (table == null || table.Count == 0)
                throw new Exception(
                    "Η ρητά ορισμένη σειρά " + PurchaseOrderSeries + " της εταιρίας " + company +
                    " δεν ανήκει στο PURDOC SOSOURCE=" + PurchaseOrderSosource + ".");

            return PurchaseOrderSeries;
        }

        internal static string SearchMasterItemsJson(XSupport xSupport, string searchText)
        {
            return JsonConvert.SerializeObject(new
            {
                success = true,
                items = SearchMasterItems(xSupport, searchText)
            });
        }

        internal static string GetStoreAvailabilityJson(XSupport xSupport, string itemCode)
        {
            return JsonConvert.SerializeObject(new
            {
                success = true,
                currentCompany = xSupport.ConnectionInfo.CompanyId,
                rows = GetStoreAvailability(xSupport, itemCode)
            });
        }

        private static string ReadRequiredParamString(XSupport xSupport, int paramCode)
        {
            XTable table = xSupport.GetSQLDataSet(
                "SELECT TOP 1 LTRIM(RTRIM(ParamValueString)) AS V " +
                "FROM cccParams " +
                "WHERE ParamCode=:1 AND (paramsIsActive=1 OR paramsIsActive IS NULL) " +
                "ORDER BY cccParams DESC",
                paramCode);

            if (table == null || table.Count == 0 || table.Current["V"] == DBNull.Value ||
                string.IsNullOrWhiteSpace(Convert.ToString(table.Current["V"])))
                throw new Exception("Δεν βρέθηκε ενεργή WelcomeStores παράμετρος " + paramCode + ".");

            return Convert.ToString(table.Current["V"]).Trim();
        }

        private static int ReadRequiredParamInt(XSupport xSupport, int paramCode)
        {
            XTable table = xSupport.GetSQLDataSet(
                "SELECT TOP 1 ParamValue AS V " +
                "FROM cccParams " +
                "WHERE ParamCode=:1 AND (paramsIsActive=1 OR paramsIsActive IS NULL) " +
                "ORDER BY cccParams DESC",
                paramCode);

            if (table == null || table.Count == 0 || table.Current["V"] == DBNull.Value)
                throw new Exception("Δεν βρέθηκε ενεργή WelcomeStores παράμετρος " + paramCode + ".");

            int value;
            if (!int.TryParse(Convert.ToString(table.Current["V"]), out value))
                throw new Exception("Η WelcomeStores παράμετρος " + paramCode + " δεν είναι έγκυρος ακέραιος.");

            return value;
        }

        private static int[] ParseCompanyIds(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new int[0];

            var values = new List<int>();
            foreach (string token in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int company;
                if (int.TryParse(token.Trim(), out company) && company > 0 && !values.Contains(company))
                    values.Add(company);
            }
            return values.ToArray();
        }

        private static List<T> ReadRows<T>(XTable table, Func<DataRow, T> map)
        {
            var result = new List<T>();
            if (table == null || table.Count == 0) return result;

            DataTable data = table.CreateDataTable(true);
            foreach (DataRow row in data.Rows)
                result.Add(map(row));
            return result;
        }

        private static string ToStringValue(DataRow row, string column)
        {
            object value = row[column];
            return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value);
        }

        private static int ToInt(DataRow row, string column)
        {
            object value = row[column];
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt32(value);
        }

        private static int? ToNullableInt(DataRow row, string column)
        {
            object value = row[column];
            return value == null || value == DBNull.Value ? (int?)null : Convert.ToInt32(value);
        }

        private static decimal ToDecimal(DataRow row, string column)
        {
            object value = row[column];
            return value == null || value == DBNull.Value ? 0m : Convert.ToDecimal(value);
        }
    }
}
