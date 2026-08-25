using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic DR supplier-code resolver.
    ///
    /// CCCMAPITEMS is the common mapping store on MTRL for:
    ///   SODTYPE 51 = Item
    ///   SODTYPE 52 = Service
    ///   SODTYPE 53 = Expense
    ///
    /// Token format: TRDR|SUPPLIERCODE;TRDR|SUPPLIERCODE;...
    /// Matching is always exact-token matching after normalization. We never
    /// use substring matching because codes such as 100 and 1002 must not collide.
    ///
    /// v1 is READ ONLY. It never writes/learns mappings. A future operator-
    /// confirmed learning action may append a token only after ambiguity checks.
    /// </summary>
    internal static class DrItemCodeResolver
    {
        public const int RecognitionStateSupplierCodeMapped = 30;

        public static JObject Resolve(XSupport xSupport, int trdrId, string supplierCode)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (trdrId <= 0) return Failure("missing_trader", trdrId, supplierCode);

            string code = NormalizeCode(supplierCode);
            if (string.IsNullOrWhiteSpace(code))
                return Failure("missing_supplier_code", trdrId, supplierCode);

            string token = BuildToken(trdrId, code);
            int company = xSupport.ConnectionInfo.CompanyId;

            // SQL only narrows the candidate set. Exact token parsing below is authoritative.
            // CCCMAPITEMS is varchar(max), therefore the LIKE is deliberately NOT considered a match.
            XTable raw = xSupport.GetSQLDataSet(
                "SELECT MTRL,CODE,NAME,SODTYPE,CCCMAPITEMS FROM MTRL " +
                "WHERE COMPANY=:1 AND SODTYPE IN (51,52,53) AND CCCMAPITEMS IS NOT NULL " +
                "AND CCCMAPITEMS LIKE :2",
                company, "%" + code + "%");

            DataTable table = raw != null ? raw.CreateDataTable(true) : null;
            var matches = new List<JObject>();

            if (table != null)
            {
                foreach (DataRow row in table.Rows)
                {
                    string mappings = ToText(row["CCCMAPITEMS"]);
                    if (!ContainsExactToken(mappings, token)) continue;

                    int sodtype = ToInt(row["SODTYPE"]);
                    matches.Add(new JObject
                    {
                        ["mtrlId"] = ToInt(row["MTRL"]),
                        ["code"] = ToText(row["CODE"]),
                        ["name"] = ToText(row["NAME"]),
                        ["sodtype"] = sodtype,
                        ["sodtypeName"] = SodtypeName(sodtype),
                        ["matchSource"] = "CCCMAPITEMS",
                        ["mappingToken"] = token
                    });
                }
            }

            bool ambiguous = matches.Count > 1;
            bool found = matches.Count == 1;
            JObject selected = found ? matches[0] : null;

            return new JObject
            {
                ["success"] = true,
                ["resolver"] = "resolve_supplier_code_mapping",
                ["version"] = 1,
                ["readOnly"] = true,
                ["trdrId"] = trdrId,
                ["supplierCode"] = supplierCode,
                ["normalizedSupplierCode"] = code,
                ["mappingToken"] = token,
                ["found"] = found,
                ["ambiguous"] = ambiguous,
                ["matchCount"] = matches.Count,
                ["recognitionState"] = found ? RecognitionStateSupplierCodeMapped : (int?)null,
                ["recognitionStateName"] = found ? "SupplierCodeMapped" : null,
                ["mtrlId"] = selected != null ? selected["mtrlId"] : null,
                ["mtrlCode"] = selected != null ? selected["code"] : null,
                ["mtrlName"] = selected != null ? selected["name"] : null,
                ["sodtype"] = selected != null ? selected["sodtype"] : null,
                ["sodtypeName"] = selected != null ? selected["sodtypeName"] : null,
                ["matchSource"] = found ? "CCCMAPITEMS" : null,
                ["reason"] = ambiguous ? "mapping_token_exists_on_multiple_mtrl" : found ? "exact_mapping_token" : "mapping_not_found",
                ["matches"] = new JArray(matches)
            };
        }

        internal static string BuildToken(int trdrId, string supplierCode)
        {
            return trdrId.ToString() + "|" + NormalizeCode(supplierCode);
        }

        internal static bool ContainsExactToken(string mappings, string expectedToken)
        {
            if (string.IsNullOrWhiteSpace(mappings) || string.IsNullOrWhiteSpace(expectedToken)) return false;
            string expected = NormalizeToken(expectedToken);
            return mappings.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeToken)
                .Any(x => string.Equals(x, expected, StringComparison.Ordinal));
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            int pipe = value.IndexOf('|');
            if (pipe < 1) return string.Empty;
            string trader = Regex.Replace(value.Substring(0, pipe).Trim(), "[^0-9]", string.Empty);
            string code = NormalizeCode(value.Substring(pipe + 1));
            return string.IsNullOrWhiteSpace(trader) || string.IsNullOrWhiteSpace(code)
                ? string.Empty
                : trader + "|" + code;
        }

        private static string NormalizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            // Preserve meaningful punctuation inside supplier codes; normalize only
            // case and whitespace so A-100 and A100 remain intentionally different.
            return Regex.Replace(value.Trim().ToUpperInvariant(), "\\s+", string.Empty);
        }

        private static string SodtypeName(int sodtype)
        {
            switch (sodtype)
            {
                case 51: return "Item";
                case 52: return "Service";
                case 53: return "Expense";
                default: return "Unknown";
            }
        }

        private static JObject Failure(string reason, int trdrId, string supplierCode)
        {
            return new JObject
            {
                ["success"] = false,
                ["resolver"] = "resolve_supplier_code_mapping",
                ["version"] = 1,
                ["readOnly"] = true,
                ["trdrId"] = trdrId,
                ["supplierCode"] = supplierCode,
                ["found"] = false,
                ["ambiguous"] = false,
                ["reason"] = reason,
                ["matches"] = new JArray()
            };
        }

        private static int ToInt(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            int result;
            return int.TryParse(Convert.ToString(value), out result) ? result : 0;
        }

        private static string ToText(object value)
        {
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }
    }
}
