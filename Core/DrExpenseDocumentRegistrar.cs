using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Expense-aware final DR posting path.
    ///
    /// The legacy JarvisTools.ExecuteRegisterDrDocument remains the source of
    /// truth for every non-expense registration. This class takes over every
    /// resolved SODTYPE 53 expense registration, including auto-resolved lines
    /// learned from previous imports / CCCMAPITEMS.
    ///
    /// Rules:
    /// - consolidated expense: one LINLINES row, LINEVAL = sum(qty * unitPrice)
    /// - per-line expense: LINEVAL = qty * unitPrice on each expense row
    /// - expense rows do NOT write QTY1 or PRICE; Soft1 receives value only
    /// - SODTYPE 52 service rows never receive LINEVAL from this rule
    /// - all writes go through Soft1 XModule/XTable; no raw SQL UPDATE
    /// </summary>
    internal static class DrExpenseDocumentRegistrar
    {
        private const int SodTypeService = 52;
        private const int SodTypeExpense = 53;

        private sealed class LineRow
        {
            public int MtrlId;
            public int SodType;
            public double Qty;
            public double Price;
            public double? LineVal;
            public Dictionary<string, object> Extra = new Dictionary<string, object>();
        }

        private enum FincodeMode
        {
            AutoFull,
            AutoPrefixOnly,
            Manual
        }

        public static string Register(XSupport xSupport, JObject input)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (input == null) throw new ArgumentNullException(nameof(input));

            string mode = input["mode"]?.ToString() ?? "auto";
            int company = xSupport.ConnectionInfo.CompanyId;
            int trdrId = (int?)input["trdrId"] ?? 0;
            int sosource = (int?)input["sosource"] ?? 0;
            int series = (int?)input["series"] ?? 0;
            string docDateRaw = input["docDate"]?.ToString();
            string docNumber = input["docNumber"]?.ToString();
            string docType = input["docType"]?.ToString();
            JArray lineItems = input["lineItems"] as JArray ?? new JArray();

            if (trdrId <= 0 || series <= 0)
                throw new Exception("Λείπει trdrId ή series για καταχώρηση παραστατικού.");

            var lines = new List<LineRow>();
            var pendingLines = new JArray();
            bool containsExpense = false;

            if (mode == "manualConsolidate")
            {
                int targetMtrlId = (int?)input["consolidateMtrlId"] ?? 0;
                if (targetMtrlId <= 0)
                    throw new Exception("Λείπει consolidateMtrlId για συγκεντρωτική καταχώρηση.");

                int sodType = GetMtrlSodType(xSupport, company, targetMtrlId);
                if (sodType != SodTypeExpense)
                    return JarvisTools.ExecuteRegisterDrDocument(xSupport, input);

                containsExpense = true;
                double totalQty = 0;
                double totalValue = 0;
                foreach (JToken token in lineItems)
                {
                    double qty = ParseNumber(token?["quantity"]);
                    double price = ParseNumber(token?["unit_price"]);
                    totalQty += qty;
                    totalValue += qty * price;
                }

                var row = new LineRow
                {
                    MtrlId = targetMtrlId,
                    SodType = sodType,
                    Qty = totalQty,
                    Price = totalQty != 0 ? totalValue / totalQty : 0,
                    LineVal = totalValue
                };
                CopyHistoryProfile(xSupport, company, trdrId, targetMtrlId, docNumber, row.Extra);
                lines.Add(row);
            }
            else
            {
                // auto and manualPerLine both arrive here. In auto mode the
                // supplier codes may already be resolved from CCCMAPITEMS due
                // to a previous import. Do not throw that result back to the
                // legacy registrar simply because the string mode is "auto".
                foreach (JToken token in lineItems)
                {
                    JObject line = token as JObject ?? new JObject();
                    JObject matched = line["matched"] as JObject;
                    int? mtrlId = matched != null && matched["mtrlId"] != null
                        ? matched["mtrlId"].Value<int>()
                        : (int?)line["manualMtrlId"];

                    if (!mtrlId.HasValue || mtrlId.Value <= 0)
                    {
                        pendingLines.Add(line);
                        continue;
                    }

                    int sodType = GetMtrlSodType(xSupport, company, mtrlId.Value);
                    double qty = ParseNumber(line["quantity"]);
                    double price = ParseNumber(line["unit_price"]);
                    var row = new LineRow
                    {
                        MtrlId = mtrlId.Value,
                        SodType = sodType,
                        Qty = qty,
                        Price = price,
                        LineVal = sodType == SodTypeExpense ? (double?)(qty * price) : null
                    };
                    containsExpense |= sodType == SodTypeExpense;
                    CopyHistoryProfile(xSupport, company, trdrId, mtrlId.Value, docNumber, row.Extra);
                    lines.Add(row);
                }

                if (!containsExpense)
                    return JarvisTools.ExecuteRegisterDrDocument(xSupport, input);

                // For auto-resolved expenses, unresolved source lines are a
                // blocker. Never silently create a partial/zero-value expense.
                if (pendingLines.Count > 0)
                    throw new Exception(
                        $"Η αυτόματη καταχώρηση δαπάνης μπλοκαρίστηκε: {pendingLines.Count} γραμμές δεν έχουν resolved MTRL.");

                if (mode == "auto")
                {
                    // If every resolved PDF line points to the same expense
                    // MTRL, this is the learned equivalent of the approved
                    // single-line precedent consolidation. Collapse it to one
                    // LINLINES row and write only the summed LINEVAL.
                    var distinctTargets = lines.Select(l => l.MtrlId).Distinct().ToList();
                    bool allExpense = lines.Count > 0 && lines.All(l => l.SodType == SodTypeExpense);
                    if (allExpense && distinctTargets.Count == 1)
                    {
                        int targetMtrlId = distinctTargets[0];
                        double totalQty = lines.Sum(l => l.Qty);
                        double totalValue = lines.Sum(l => l.LineVal ?? 0);
                        var consolidated = new LineRow
                        {
                            MtrlId = targetMtrlId,
                            SodType = SodTypeExpense,
                            Qty = totalQty,
                            Price = totalQty != 0 ? totalValue / totalQty : 0,
                            LineVal = totalValue
                        };
                        CopyHistoryProfile(xSupport, company, trdrId, targetMtrlId, docNumber, consolidated.Extra);
                        lines.Clear();
                        lines.Add(consolidated);
                        mode = "autoResolvedConsolidate";
                    }
                    else
                    {
                        // Multiple resolved targets: keep one Soft1 row per
                        // source line. Expense rows still write LINEVAL only.
                        mode = "autoResolvedPerLine";
                    }
                }
            }

            if (lines.Count == 0)
                throw new Exception("Καμία γραμμή δεν είναι έτοιμη για καταχώρηση.");

            if (!TryGetLinearDocumentObject(sosource, out string objectName))
            {
                throw new Exception(
                    $"Βρέθηκε δαπάνη SODTYPE 53 αλλά το sosource={sosource} δεν χρησιμοποιεί επιβεβαιωμένο LINLINES document object.");
            }

            return PostLinearDocument(
                xSupport, company, trdrId, sosource, series, objectName,
                docDateRaw, docNumber, docType, mode, lines, pendingLines);
        }

        private static string PostLinearDocument(
            XSupport xSupport,
            int company,
            int trdrId,
            int sosource,
            int series,
            string objectName,
            string docDateRaw,
            string docNumber,
            string docType,
            string mode,
            List<LineRow> lines,
            JArray pendingLines)
        {
            XModule module = xSupport.CreateModule(objectName);
            XTable findoc = module.GetTable("FINDOC");
            XTable lineTable = module.GetTable("LINLINES");
            try
            {
                module.InsertData();
                findoc.Current["TRDR"] = trdrId;
                findoc.Current["SERIES"] = series;

                DateTime? docDate = ParseDate(docDateRaw);
                if (docDate.HasValue)
                    findoc.Current["TRNDATE"] = docDate.Value;

                string fullDocIdentifier = string.Join(" ",
                    new[] { docType, docNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrWhiteSpace(fullDocIdentifier))
                {
                    string remarks = "Jarvis DR - πηγή παραστατικό: " + fullDocIdentifier;
                    findoc.Current["REMARKS"] = ToSoft1GreekAnsi(remarks);
                }

                string manualFincodeHint = null;
                if (!string.IsNullOrWhiteSpace(docNumber))
                {
                    var fincodeMode = GetFincodeMode(xSupport, company, series, sosource);
                    switch (fincodeMode)
                    {
                        case FincodeMode.AutoFull:
                            findoc.Current["COMMENTS"] = ToSoft1GreekAnsi(fullDocIdentifier);
                            break;
                        case FincodeMode.AutoPrefixOnly:
                            manualFincodeHint = docNumber;
                            break;
                        default:
                            findoc.Current["FINCODE"] = ToSoft1GreekAnsi(fullDocIdentifier);
                            break;
                    }
                }

                foreach (LineRow line in lines)
                {
                    lineTable.Add();
                    lineTable.Current["MTRL"] = line.MtrlId;

                    if (line.SodType == SodTypeExpense)
                    {
                        if (!line.LineVal.HasValue)
                            throw new Exception($"Λείπει LINEVAL για δαπάνη MTRL={line.MtrlId}.");
                        lineTable.Current["LINEVAL"] = line.LineVal.Value;
                    }
                    else
                    {
                        lineTable.Current["QTY1"] = line.Qty;
                        lineTable.Current["PRICE"] = line.Price;
                    }

                    foreach (var kv in line.Extra)
                    {
                        if (kv.Value != null)
                            lineTable.Current[kv.Key] = NormalizeNumeric(kv.Value);
                    }
                    lineTable.Current.Post();
                }

                int findocId = module.PostData();
                if (findocId <= 0)
                    throw new Exception("Αποτυχία καταχώρησης παραστατικού (PostData επέστρεψε 0).");

                double expenseLineVal = lines
                    .Where(l => l.SodType == SodTypeExpense && l.LineVal.HasValue)
                    .Sum(l => l.LineVal.Value);

                DebugLog.Log(
                    $"[dr-expense-register] OK findoc={findocId} mode={mode} lines={lines.Count} " +
                    $"expenseLineVal={expenseLineVal.ToString(CultureInfo.InvariantCulture)} valueOnlyExpenses=true");

                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    findocId,
                    sosource,
                    objectName,
                    strategyUsed = mode,
                    linesWritten = lines.Count,
                    pendingLines,
                    manualFincodeHint,
                    expenseLineValApplied = true,
                    expenseValueOnly = true,
                    expenseLineVal
                });
            }
            finally
            {
                lineTable.Dispose();
                findoc.Dispose();
                module.Dispose();
            }
        }

        private static bool TryGetLinearDocumentObject(int sosource, out string objectName)
        {
            switch (sosource)
            {
                case 1253:
                    objectName = "LINSUPDOC";
                    return true;
                case 1353:
                    objectName = "LINCUSDOC";
                    return true;
                default:
                    objectName = null;
                    return false;
            }
        }

        private static int GetMtrlSodType(XSupport xSupport, int company, int mtrlId)
        {
            XTable t = xSupport.GetSQLDataSet(
                "SELECT TOP 1 SODTYPE FROM MTRL WHERE COMPANY=:1 AND MTRL=:2",
                company, mtrlId);
            if (t == null || t.Count == 0 || t.Current["SODTYPE"] == DBNull.Value)
                return 0;
            return Convert.ToInt32(t.Current["SODTYPE"]);
        }

        private static void CopyHistoryProfile(
            XSupport xSupport,
            int company,
            int trdrId,
            int mtrlId,
            string currentDocNumber,
            Dictionary<string, object> destination)
        {
            const string fields = "L.INST,L.PRJC,L.CNTR,L.BUSUNITS";
            XTable t;
            try
            {
                t = xSupport.GetSQLDataSet(
                    "SELECT TOP 10 " + fields + ",F.FINCODE,F.TRNDATE " +
                    "FROM MTRLINES L INNER JOIN FINDOC F ON F.COMPANY=L.COMPANY AND F.FINDOC=L.FINDOC " +
                    "WHERE L.COMPANY=:1 AND F.TRDR=:2 AND L.MTRL=:3 AND F.ISCANCEL=0 " +
                    "ORDER BY F.TRNDATE DESC",
                    company, trdrId, mtrlId);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[dr-expense-register] history profile query failed mtrl={mtrlId}: " + ex);
                return;
            }

            if (t == null || t.Count == 0) return;
            DataTable dt = t.CreateDataTable(true);
            DataRow best = null;
            double bestScore = -1;
            foreach (DataRow row in dt.Rows)
            {
                string fincode = row["FINCODE"] == DBNull.Value ? "" : row["FINCODE"].ToString();
                double score = ScoreFormat(fincode, currentDocNumber);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = row;
                }
            }

            if (best == null || bestScore < 0.3) return;
            foreach (string field in new[] { "INST", "PRJC", "CNTR", "BUSUNITS" })
            {
                if (best[field] != DBNull.Value)
                    destination[field] = best[field];
            }
        }

        private static double ScoreFormat(string candidateFincode, string currentDocNumber)
        {
            if (string.IsNullOrWhiteSpace(candidateFincode) || string.IsNullOrWhiteSpace(currentDocNumber))
                return 0;
            if (FincodeSkeleton(candidateFincode) == FincodeSkeleton(currentDocNumber))
                return 1;
            string a = LeadingPrefix(candidateFincode);
            string b = LeadingPrefix(currentDocNumber);
            return !string.IsNullOrEmpty(a) && a == b ? 0.5 : 0;
        }

        private static string FincodeSkeleton(string value)
        {
            return string.IsNullOrEmpty(value)
                ? ""
                : Regex.Replace(value, "[0-9]+", "#").ToUpperInvariant();
        }

        private static string LeadingPrefix(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Regex.Match(value, @"^[^\d]*").Value.ToUpperInvariant();
        }

        private static FincodeMode GetFincodeMode(
            XSupport xSupport,
            int company,
            int series,
            int sosource)
        {
            try
            {
                XTable t = xSupport.GetSQLDataSet(
                    "SELECT AUTONUMBER,FINCODEGENERATE FROM SERIES WHERE COMPANY=:1 AND SERIES=:2 AND SOSOURCE=:3",
                    company, series, sosource);
                if (t == null || t.Count == 0) return FincodeMode.Manual;
                bool autonumber = Convert.ToInt32(t.Current["AUTONUMBER"]) != 0;
                bool fincodeGenerate = Convert.ToInt32(t.Current["FINCODEGENERATE"]) != 0;
                if (autonumber && fincodeGenerate) return FincodeMode.AutoFull;
                if (autonumber) return FincodeMode.AutoPrefixOnly;
                return FincodeMode.Manual;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-expense-register] GetFincodeMode failed: " + ex);
                return FincodeMode.Manual;
            }
        }

        private static DateTime? ParseDate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] formats =
            {
                "dd/MM/yyyy", "d/M/yyyy", "dd/M/yyyy", "d/MM/yyyy",
                "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "d.M.yyyy"
            };
            if (DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime exact))
                return exact;
            if (DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime parsed))
                return parsed;
            return null;
        }

        private static double ParseNumber(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;

            // JSON numeric tokens are already culture-independent and should
            // never be reparsed through a formatted string.
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return token.Value<double>();

            string value = token.ToString().Trim()
                .Replace("\u00A0", "")
                .Replace(" ", "");
            if (string.IsNullOrEmpty(value)) return 0;

            int lastComma = value.LastIndexOf(',');
            int lastDot = value.LastIndexOf('.');

            // Both separators present: the right-most one is the decimal
            // separator and all earlier occurrences are grouping separators.
            // Examples: 5.186,00 -> 5186.00, 5,186.00 -> 5186.00.
            if (lastComma >= 0 && lastDot >= 0)
            {
                char decimalSeparator = lastComma > lastDot ? ',' : '.';
                char groupingSeparator = decimalSeparator == ',' ? '.' : ',';
                value = value.Replace(groupingSeparator.ToString(), "");
                if (decimalSeparator == ',') value = value.Replace(',', '.');
            }
            else if (lastComma >= 0)
            {
                // A lone comma is treated as decimal separator. This covers
                // Greek values such as 0,03690 and 122,00.
                value = value.Replace(',', '.');
            }
            else if (lastDot >= 0)
            {
                // A lone dot is ambiguous. If it forms a classic grouping
                // pattern (1.234 / 12.345 / 1.234.567), treat it as thousands;
                // otherwise keep it as the invariant decimal separator.
                if (Regex.IsMatch(value, @"^[+-]?\d{1,3}(\.\d{3})+$"))
                    value = value.Replace(".", "");
            }

            return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 0;
        }

        /// <summary>
        /// Soft1's XDll/XTable string setter crosses an ANSI boundary in this
        /// installation. A normal Unicode Greek string is converted through a
        /// Western code page and Greek characters become '?'. Preserve the
        /// Windows-1253 bytes by carrying them through Windows-1252 characters;
        /// the Soft1 side then receives the exact Greek ANSI byte sequence.
        /// ASCII characters are unchanged by this conversion.
        /// </summary>
        private static string ToSoft1GreekAnsi(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            try
            {
                byte[] greekBytes = System.Text.Encoding.GetEncoding(1253).GetBytes(value);
                return System.Text.Encoding.GetEncoding(1252).GetString(greekBytes);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-expense-register] ToSoft1GreekAnsi failed: " + ex);
                return value;
            }
        }

        private static object NormalizeNumeric(object value)
        {
            if (value is short || value is byte || value is sbyte || value is long)
                return Convert.ToInt32(value);
            return value;
        }
    }
}
