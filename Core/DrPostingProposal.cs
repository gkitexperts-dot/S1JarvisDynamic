using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Read-only preview of the historical posting shape for the current DR document.
    /// This does NOT create/update Soft1 data and does NOT replace the final
    /// ExecuteRegisterDrDocument/AnalyzeTraderPattern authority.
    /// </summary>
    internal static class DrPostingProposal
    {
        private const double ConfidenceThreshold = 0.55;
        private const int MaxHistoryRows = 20;

        public static JObject Analyze(
            XSupport xSupport,
            int trdrId,
            int series,
            int sosource,
            int sourceLineCount)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (trdrId <= 0 || series <= 0 || sosource <= 0)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["mode"] = "Unknown",
                    ["needsReview"] = true,
                    ["reason"] = "missing_posting_coordinates"
                };
            }

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable raw = xSupport.GetSQLDataSet(
                "SELECT TOP 20 F.FINDOC,F.FINCODE,F.TRNDATE," +
                " (SELECT COUNT(*) FROM MTRLINES L WHERE L.COMPANY=F.COMPANY AND L.FINDOC=F.FINDOC) AS LINECOUNT " +
                "FROM FINDOC F WHERE F.COMPANY=:1 AND F.TRDR=:2 AND F.SERIES=:3 AND F.SOSOURCE=:4 " +
                "ORDER BY F.TRNDATE DESC,F.FINDOC DESC",
                company, trdrId, series, sosource);

            DataTable table = raw != null ? raw.CreateDataTable(true) : null;
            var counts = new List<int>();
            var evidence = new JArray();

            if (table != null)
            {
                foreach (DataRow row in table.Rows)
                {
                    int lineCount = ToInt(row["LINECOUNT"]);
                    if (lineCount <= 0) continue;
                    counts.Add(lineCount);
                    if (evidence.Count < 8)
                    {
                        evidence.Add(new JObject
                        {
                            ["findocId"] = ToInt(row["FINDOC"]),
                            ["fincode"] = Convert.ToString(row["FINCODE"]),
                            ["date"] = row["TRNDATE"] == DBNull.Value ? null : JToken.FromObject(row["TRNDATE"]),
                            ["lineCount"] = lineCount
                        });
                    }
                }
            }

            if (counts.Count == 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["mode"] = "Unknown",
                    ["needsReview"] = true,
                    ["confidence"] = 0.0,
                    ["threshold"] = ConfidenceThreshold,
                    ["sampleSize"] = 0,
                    ["sourceLineCount"] = sourceLineCount,
                    ["reason"] = "no_historical_documents",
                    ["evidence"] = evidence
                };
            }

            var dominant = counts
                .GroupBy(x => x)
                .Select(g => new { LineCount = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.LineCount)
                .First();

            double consistency = dominant.Count / (double)counts.Count;
            double sampleAdequacy = Math.Min(1.0, counts.Count / 3.0);
            double confidence = 0.7 * consistency + 0.3 * sampleAdequacy;
            bool confident = confidence >= ConfidenceThreshold;

            string mode = "Unknown";
            if (confident)
                mode = dominant.LineCount == 1 ? "Consolidated" : "Detailed";

            return new JObject
            {
                ["success"] = true,
                ["mode"] = mode,
                ["needsReview"] = !confident,
                ["confidence"] = Math.Round(confidence, 4),
                ["threshold"] = ConfidenceThreshold,
                ["sampleSize"] = counts.Count,
                ["singleLineSampleSize"] = counts.Count(x => x == 1),
                ["dominantHistoricalLineCount"] = dominant.LineCount,
                ["dominantSampleSize"] = dominant.Count,
                ["sourceLineCount"] = sourceLineCount,
                ["proposedTargetLineCount"] = confident ? dominant.LineCount : (int?)null,
                ["classification"] = confident && dominant.LineCount == 1 ? "ExpenseCandidate" : "DocumentCandidate",
                ["reason"] = confident ? "historical_line_count_pattern" : "historical_pattern_not_confident",
                ["evidence"] = evidence
            };
        }

        private static int ToInt(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
        }
    }
}
