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
    /// Deterministic DR supplier-code resolver and operator-confirmed learning writer.
    /// CCCMAPITEMS is the common mapping store on MTRL for SODTYPE 51/52/53.
    /// Token format: TRDR|SUPPLIERCODE;TRDR|SUPPLIERCODE;...
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
                ["version"] = 2,
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

        public static JObject LearnMappings(XSupport xSupport, int trdrId, int targetMtrlId, JArray requestedMappings)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (trdrId <= 0 || targetMtrlId <= 0) throw new ArgumentException("Missing trader or target MTRL.");
            if (requestedMappings == null || requestedMappings.Count == 0) throw new ArgumentException("No supplier codes supplied.");

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable targetRaw = xSupport.GetSQLDataSet(
                "SELECT TOP 1 MTRL,CODE,NAME,SODTYPE,CCCMAPITEMS FROM MTRL WHERE COMPANY=:1 AND MTRL=:2 AND SODTYPE IN (51,52,53)",
                company, targetMtrlId);
            DataTable targetTable = targetRaw != null ? targetRaw.CreateDataTable(true) : null;
            if (targetTable == null || targetTable.Rows.Count == 0) throw new Exception("Target MTRL not found or unsupported SODTYPE.");

            DataRow target = targetTable.Rows[0];
            int sodtype = ToInt(target["SODTYPE"]);
            var requestedTokens = new List<string>();
            foreach (JToken raw in requestedMappings)
            {
                JObject mapping = raw as JObject;
                string supplierCode = mapping != null ? mapping["supplierCode"]?.ToString() : raw?.ToString();
                string token = BuildToken(trdrId, supplierCode);
                if (string.IsNullOrWhiteSpace(token) || token.EndsWith("|", StringComparison.Ordinal)) continue;
                if (!requestedTokens.Contains(token, StringComparer.Ordinal)) requestedTokens.Add(token);
            }
            if (requestedTokens.Count == 0) throw new Exception("No valid supplier codes supplied.");

            var conflicts = new JArray();
            var alreadyPresent = new JArray();
            var toAppend = new List<string>();
            foreach (string token in requestedTokens)
            {
                string supplierCode = token.Substring(token.IndexOf('|') + 1);
                JObject current = Resolve(xSupport, trdrId, supplierCode);
                bool found = (bool?)current["found"] == true;
                bool ambiguous = (bool?)current["ambiguous"] == true;
                int currentMtrl = (int?)current["mtrlId"] ?? 0;
                if (ambiguous || (found && currentMtrl != targetMtrlId))
                {
                    conflicts.Add(new JObject
                    {
                        ["mappingToken"] = token,
                        ["existingMtrlId"] = currentMtrl,
                        ["matches"] = current["matches"]?.DeepClone()
                    });
                    continue;
                }
                if (found && currentMtrl == targetMtrlId) alreadyPresent.Add(token);
                else toAppend.Add(token);
            }
            if (conflicts.Count > 0)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["resolver"] = "learn_supplier_code_mapping",
                    ["version"] = 1,
                    ["reason"] = "mapping_conflict",
                    ["targetMtrlId"] = targetMtrlId,
                    ["conflicts"] = conflicts,
                    ["alreadyPresent"] = alreadyPresent
                };
            }

            string existing = ToText(target["CCCMAPITEMS"]) ?? string.Empty;
            var existingTokens = existing.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeToken).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            foreach (string token in toAppend)
                if (!existingTokens.Contains(token, StringComparer.Ordinal)) existingTokens.Add(token);
            string updated = string.Join(";", existingTokens);

            if (toAppend.Count > 0)
            {
                string objectName = ObjectNameForSodtype(sodtype);
                XModule module = xSupport.CreateModule(objectName);
                module.LocateData(targetMtrlId);
                XTable mtrl = module.GetTable("MTRL");
                mtrl.Current["CCCMAPITEMS"] = updated;
                int posted = module.PostData();
                if (posted <= 0) throw new Exception("CCCMAPITEMS update failed (PostData returned 0).");
            }

            XTable verifyRaw = xSupport.GetSQLDataSet(
                "SELECT TOP 1 CCCMAPITEMS FROM MTRL WHERE COMPANY=:1 AND MTRL=:2", company, targetMtrlId);
            DataTable verifyTable = verifyRaw != null ? verifyRaw.CreateDataTable(true) : null;
            string verifiedText = verifyTable != null && verifyTable.Rows.Count > 0 ? ToText(verifyTable.Rows[0]["CCCMAPITEMS"]) : null;
            var learned = new JArray();
            foreach (string token in requestedTokens)
            {
                if (!ContainsExactToken(verifiedText, token)) throw new Exception("CCCMAPITEMS verification failed for token " + token + ".");
                learned.Add(token);
            }

            return new JObject
            {
                ["success"] = true,
                ["resolver"] = "learn_supplier_code_mapping",
                ["version"] = 1,
                ["verified"] = true,
                ["recognitionState"] = RecognitionStateSupplierCodeMapped,
                ["recognitionStateName"] = "SupplierCodeMapped",
                ["targetMtrlId"] = targetMtrlId,
                ["mtrlCode"] = ToText(target["CODE"]),
                ["mtrlName"] = ToText(target["NAME"]),
                ["sodtype"] = sodtype,
                ["sodtypeName"] = SodtypeName(sodtype),
                ["learnedTokens"] = learned,
                ["alreadyPresent"] = alreadyPresent,
                ["cccmMapItems"] = verifiedText
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
                .Select(NormalizeToken).Any(x => string.Equals(x, expected, StringComparison.Ordinal));
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            int pipe = value.IndexOf('|');
            if (pipe < 1) return string.Empty;
            string trader = Regex.Replace(value.Substring(0, pipe).Trim(), "[^0-9]", string.Empty);
            string code = NormalizeCode(value.Substring(pipe + 1));
            return string.IsNullOrWhiteSpace(trader) || string.IsNullOrWhiteSpace(code) ? string.Empty : trader + "|" + code;
        }

        private static string NormalizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return Regex.Replace(value.Trim().ToUpperInvariant(), "\\s+", string.Empty);
        }

        private static string ObjectNameForSodtype(int sodtype)
        {
            switch (sodtype)
            {
                case 51: return "ITEM";
                case 52: return "SERVICE";
                case 53: return "LINEITEM";
                default: throw new Exception("Unsupported MTRL SODTYPE " + sodtype + ".");
            }
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
                ["version"] = 2,
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
