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
    /// Deterministic DR historical resolver.
    ///
    /// Important v4 rule:
    /// Historical Soft1 line count is an OUTCOME to learn, never an input used
    /// to decide which historical document is similar. Otherwise a new invoice
    /// with four source lines would incorrectly prefer old four-line postings
    /// and could never learn that this supplier/document family is normally
    /// consolidated to one Soft1 line.
    /// </summary>
    internal static class DrDocumentPatternResolver
    {
        private const double ConsensusThreshold = 0.72;

        public static JObject Resolve(
            XSupport xSupport, int trdrId, string documentType,
            string documentSeries, int sourceLineCount)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (trdrId <= 0) return Failure("missing_trader");

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable raw = xSupport.GetSQLDataSet(
                "SELECT TOP 50 F.FINDOC,F.FINCODE,F.TRNDATE,F.SOSOURCE,F.SERIES,F.BUSUNITS," +
                " (SELECT COUNT(*) FROM MTRLINES L WHERE L.COMPANY=F.COMPANY AND L.FINDOC=F.FINDOC) AS LINECOUNT " +
                "FROM FINDOC F WHERE F.COMPANY=:1 AND F.TRDR=:2 " +
                "ORDER BY F.TRNDATE DESC,F.FINDOC DESC", company, trdrId);

            DataTable table = raw != null ? raw.CreateDataTable(true) : null;
            if (table == null || table.Rows.Count == 0)
                return Empty("no_historical_documents", sourceLineCount);

            string typeToken = Normalize(documentType);
            string seriesToken = Normalize(documentSeries);
            var rows = new List<Candidate>();

            foreach (DataRow r in table.Rows)
            {
                var c = new Candidate
                {
                    Findoc = ToInt(r["FINDOC"]),
                    Fincode = ToText(r["FINCODE"]),
                    Date = ToDate(r["TRNDATE"]),
                    Sosource = ToInt(r["SOSOURCE"]),
                    Series = ToInt(r["SERIES"]),
                    Busunits = ToInt(r["BUSUNITS"]),
                    LineCount = ToInt(r["LINECOUNT"])
                };
                if (c.Findoc <= 0) continue;
                c.Score = Similarity(c, typeToken, seriesToken);
                rows.Add(c);
            }

            if (rows.Count == 0) return Empty("historical_documents_unusable", sourceLineCount);

            var ranked = rows.OrderByDescending(x => x.Score).ThenByDescending(x => x.Date).ToList();
            double bestScore = ranked[0].Score;

            // Similarity is based on document family/header evidence only.
            // Posting line count is intentionally NOT used here.
            double floor = Math.Max(0.30, bestScore - 0.16);
            var similar = ranked.Where(x => x.Score >= floor).Take(15).ToList();
            if (similar.Count < 3) similar = ranked.Take(Math.Min(8, ranked.Count)).ToList();

            var sosource = Consensus(similar, x => x.Sosource);
            var series = Consensus(similar, x => x.Series);
            var busunits = Consensus(similar.Where(x => x.Busunits > 0).ToList(), x => x.Busunits);

            // NOW learn the actual posting outcome from the selected historical set.
            var lineShape = Consensus(similar.Where(x => x.LineCount > 0).ToList(), x => x.LineCount);

            string mode = "Unknown";
            if (lineShape.SampleSize > 0 && lineShape.Ratio >= ConsensusThreshold)
                mode = lineShape.Value == 1 ? "Consolidated" : "Detailed";

            double coordinateConfidence = new[]
            {
                sosource.Ratio,
                series.Ratio,
                busunits.SampleSize > 0 ? busunits.Ratio : 1.0,
                lineShape.SampleSize > 0 ? lineShape.Ratio : 0.0
            }.Average();
            double confidence = Math.Min(1.0, 0.30 * bestScore + 0.70 * coordinateConfidence);
            bool needsReview = confidence < ConsensusThreshold || mode == "Unknown" ||
                sosource.Ratio < ConsensusThreshold || series.Ratio < ConsensusThreshold;

            var evidence = new JArray();
            foreach (var c in ranked.Take(8))
            {
                evidence.Add(new JObject
                {
                    ["findocId"] = c.Findoc,
                    ["fincode"] = c.Fincode,
                    ["date"] = c.Date == DateTime.MinValue ? null : JToken.FromObject(c.Date),
                    ["sosource"] = c.Sosource,
                    ["series"] = c.Series,
                    ["busunits"] = c.Busunits,
                    ["bunit"] = c.Busunits,
                    ["postedLineCount"] = c.LineCount,
                    ["lineCount"] = c.LineCount,
                    ["similarity"] = Math.Round(c.Score, 4),
                    ["isStrongPrecedent"] = c == ranked[0] && bestScore >= ConsensusThreshold
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["resolver"] = "resolve_document_pattern",
                ["version"] = 4,
                ["mode"] = mode,
                ["needsReview"] = needsReview,
                ["confidence"] = Math.Round(confidence, 4),
                ["threshold"] = ConsensusThreshold,
                ["sourceLineCount"] = sourceLineCount,
                ["historicalPostedLineCount"] = lineShape.SampleSize > 0 && lineShape.Ratio >= ConsensusThreshold ? lineShape.Value : (int?)null,
                ["proposedTargetLineCount"] = mode == "Consolidated" ? 1 : (mode == "Detailed" ? sourceLineCount : (int?)null),
                ["sampleSize"] = rows.Count,
                ["similarSampleSize"] = similar.Count,
                ["precedentFindocId"] = bestScore >= ConsensusThreshold ? ranked[0].Findoc : (int?)null,
                ["precedentSimilarity"] = Math.Round(bestScore, 4),
                ["precedentStrong"] = bestScore >= ConsensusThreshold,
                ["resolvedSosource"] = sosource.Ratio >= ConsensusThreshold ? sosource.Value : (int?)null,
                ["resolvedSeries"] = series.Ratio >= ConsensusThreshold ? series.Value : (int?)null,
                ["resolvedBunit"] = busunits.SampleSize > 0 && busunits.Ratio >= ConsensusThreshold ? busunits.Value : (int?)null,
                ["resolvedBusunits"] = busunits.SampleSize > 0 && busunits.Ratio >= ConsensusThreshold ? busunits.Value : (int?)null,
                ["sosourceEvidence"] = Json(sosource),
                ["seriesEvidence"] = Json(series),
                ["bunitEvidence"] = Json(busunits),
                ["busunitsEvidence"] = Json(busunits),
                ["lineShapeEvidence"] = Json(lineShape),
                ["lineShapeIsOutcome"] = true,
                ["reason"] = bestScore >= ConsensusThreshold ? "similar_historical_precedent" : "historical_consensus",
                ["evidence"] = evidence
            };
        }

        private static double Similarity(Candidate c, string typeToken, string seriesToken)
        {
            double score = 0, weight = 0;
            string f = Normalize(c.Fincode);

            // Document family/type is the strongest available deterministic
            // similarity signal in v4. External series is next. Recency only
            // breaks otherwise similar candidates. Historical line count is
            // deliberately absent: it is the outcome we want to learn.
            if (!string.IsNullOrWhiteSpace(typeToken))
            {
                weight += 0.55;
                if (f.Contains(typeToken)) score += 0.55;
            }
            if (!string.IsNullOrWhiteSpace(seriesToken))
            {
                weight += 0.30;
                if (f.Contains(seriesToken)) score += 0.30;
            }
            weight += 0.15;
            if (c.Date != DateTime.MinValue)
            {
                double days = Math.Max(0, (DateTime.Today - c.Date.Date).TotalDays);
                score += 0.15 * (1.0 / (1.0 + days / 365.0));
            }
            return weight <= 0 ? 0 : Math.Min(1.0, score / weight);
        }

        private static ConsensusValue Consensus(List<Candidate> source, Func<Candidate,int> selector)
        {
            var vals = source.Select(selector).Where(x => x > 0).ToList();
            if (vals.Count == 0) return new ConsensusValue();
            var best = vals.GroupBy(x => x).Select(g => new { Value=g.Key, Count=g.Count() }).OrderByDescending(x => x.Count).ThenBy(x => x.Value).First();
            return new ConsensusValue { Value=best.Value, Count=best.Count, SampleSize=vals.Count, Ratio=best.Count/(double)vals.Count };
        }

        private static JObject Json(ConsensusValue x) => new JObject
        {
            ["value"] = x.SampleSize > 0 ? x.Value : (int?)null,
            ["matches"] = x.Count,
            ["sampleSize"] = x.SampleSize,
            ["ratio"] = Math.Round(x.Ratio,4)
        };

        private static JObject Empty(string reason, int lines) => new JObject
        {
            ["success"] = true,["resolver"]="resolve_document_pattern",["version"]=4,["mode"]="Unknown",
            ["needsReview"]=true,["confidence"]=0.0,["reason"]=reason,["sampleSize"]=0,["sourceLineCount"]=lines,["lineShapeIsOutcome"]=true,["evidence"]=new JArray()
        };
        private static JObject Failure(string reason) => new JObject
        {
            ["success"] = false,["resolver"]="resolve_document_pattern",["version"]=4,["mode"]="Unknown",
            ["needsReview"]=true,["reason"]=reason,["lineShapeIsOutcome"]=true,["evidence"]=new JArray()
        };
        private static string Normalize(string s){if(string.IsNullOrWhiteSpace(s))return string.Empty;return Regex.Replace(s.Trim().ToUpperInvariant(),"[^0-9A-ZΑ-Ω]",string.Empty);}
        private static int ToInt(object v){if(v==null||v==DBNull.Value)return 0;int x;return int.TryParse(Convert.ToString(v),out x)?x:0;}
        private static string ToText(object v)=>v==null||v==DBNull.Value?null:Convert.ToString(v);
        private static DateTime ToDate(object v){if(v==null||v==DBNull.Value)return DateTime.MinValue;DateTime d;return DateTime.TryParse(Convert.ToString(v),out d)?d:DateTime.MinValue;}

        private sealed class Candidate{public int Findoc,Sosource,Series,Busunits,LineCount;public string Fincode;public DateTime Date;public double Score;}
        private sealed class ConsensusValue{public int Value,Count,SampleSize;public double Ratio;}
    }
}
