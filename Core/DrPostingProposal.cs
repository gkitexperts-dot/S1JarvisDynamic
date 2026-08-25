using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    /// <summary>
    /// Deterministic, read-only DR historical classifier.
    ///
    /// Precedent-first algorithm:
    ///   1. Scope strictly to the resolved Soft1 trader.
    ///   2. Load recent real FINDOC records for that trader.
    ///   3. Score each historical document against the current extracted document.
    ///   4. Prefer a strong similar precedent; otherwise use weighted consensus.
    ///   5. Return evidence only. No Soft1 write is performed here.
    ///
    /// The AI may explain ambiguity, but SOSOURCE/SERIES/BUNIT/posting shape in this
    /// result come only from Soft1 history.
    /// </summary>
    internal static class DrPostingProposal
    {
        private const double StrongPrecedentThreshold = 0.72;
        private const double ConsensusThreshold = 0.72;
        private const int MaxHistoryRows = 50;
        private const int MaxEvidenceRows = 8;

        public static JObject ResolveDocumentPattern(
            XSupport xSupport,
            int trdrId,
            int seriesHint,
            int sosourceHint,
            string documentType,
            string documentSeries,
            string documentNumber,
            int sourceLineCount)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (trdrId <= 0)
                return Failure("missing_trader");

            int company = xSupport.ConnectionInfo.CompanyId;
            XTable raw = xSupport.GetSQLDataSet(
                "SELECT TOP 50 F.FINDOC,F.FINCODE,F.TRNDATE,F.SOSOURCE,F.SERIES,F.BUNIT," +
                " (SELECT COUNT(*) FROM MTRLINES L WHERE L.COMPANY=F.COMPANY AND L.FINDOC=F.FINDOC) AS LINECOUNT " +
                "FROM FINDOC F WHERE F.COMPANY=:1 AND F.TRDR=:2 " +
                "ORDER BY F.TRNDATE DESC,F.FINDOC DESC",
                company, trdrId);

            DataTable table = raw != null ? raw.CreateDataTable(true) : null;
            if (table == null || table.Rows.Count == 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["resolver"] = "resolve_document_pattern",
                    ["version"] = 2,
                    ["mode"] = "Unknown",
                    ["needsReview"] = true,
                    ["confidence"] = 0.0,
                    ["reason"] = "no_historical_documents",
                    ["sampleSize"] = 0,
                    ["sourceLineCount"] = sourceLineCount,
                    ["evidence"] = new JArray()
                };
            }

            string typeToken = NormalizeToken(documentType);
            string seriesToken = NormalizeToken(documentSeries);
            string numberToken = NormalizeToken(documentNumber);
            DateTime now = DateTime.Today;

            var candidates = new List<Candidate>();
            foreach (DataRow row in table.Rows)
            {
                var c = new Candidate
                {
                    Findoc = ToInt(row["FINDOC"]),
                    Fincode = Convert.ToString(row["FINCODE"]),
                    Date = ToDate(row["TRNDATE"]),
                    Sosource = ToInt(row["SOSOURCE"]),
                    Series = ToInt(row["SERIES"]),
                    Bunit = ToInt(row["BUNIT"]),
                    LineCount = ToInt(row["LINECOUNT"])
                };
                if (c.Findoc <= 0) continue;
                c.Score = Score(c, seriesHint, sosourceHint, typeToken, seriesToken, numberToken, sourceLineCount, now);
                candidates.Add(c);
            }

            if (candidates.Count == 0)
                return Failure("historical_documents_unusable");

            var ranked = candidates.OrderByDescending(x => x.Score).ThenByDescending(x => x.Date).ToList();
            Candidate best = ranked[0];
            bool strongPrecedent = best.Score >= StrongPrecedentThreshold;

            // Consensus is calculated over the most relevant historical documents, not blindly over all history.
            // Keep records reasonably close to the best score; fall back to top 8 if the band is too small.
            double bandFloor = Math.Max(0.35, best.Score - 0.18);
            List<Candidate> similar = ranked.Where(x => x.Score >= bandFloor).Take(15).ToList();
            if (similar.Count < 3) similar = ranked.Take(Math.Min(8, ranked.Count)).ToList();

            Consensus sosourceConsensus = BuildConsensus(similar, x => x.Sosource);
            Consensus seriesConsensus = BuildConsensus(similar, x => x.Series);
            Consensus bunitConsensus = BuildConsensus(similar.Where(x => x.Bunit > 0).ToList(), x => x.Bunit);
            Consensus lineCountConsensus = BuildConsensus(similar.Where(x => x.LineCount > 0).ToList(), x => x.LineCount);

            double weightedConsensus = new[]
            {
                sosourceConsensus.Ratio,
                seriesConsensus.Ratio,
                bunitConsensus.SampleSize > 0 ? bunitConsensus.Ratio : 1.0,
                lineCountConsensus.SampleSize > 0 ? lineCountConsensus.Ratio : 0.0
            }.Average();

            double confidence = strongPrecedent
                ? Math.Min(1.0, 0.65 * best.Score + 0.35 * weightedConsensus)
                : Math.Min(1.0, 0.45 * best.Score + 0.55 * weightedConsensus);

            bool deterministicCoordinates =
                sosourceConsensus.SampleSize > 0 && sosourceConsensus.Ratio >= ConsensusThreshold &&
                seriesConsensus.SampleSize > 0 && seriesConsensus.Ratio >= ConsensusThreshold &&
                (bunitConsensus.SampleSize == 0 || bunitConsensus.Ratio >= ConsensusThreshold);

            string mode = "Unknown";
            if (lineCountConsensus.SampleSize > 0 && lineCountConsensus.Ratio >= ConsensusThreshold)
                mode = lineCountConsensus.Value == 1 ? "Consolidated" : "Detailed";
            else if (strongPrecedent && best.LineCount > 0)
                mode = best.LineCount == 1 ? "Consolidated" : "Detailed";

            bool needsReview = !deterministicCoordinates || mode == "Unknown" || confidence < ConsensusThreshold;

            var evidence = new JArray();
            foreach (Candidate c in ranked.Take(MaxEvidenceRows))
            {
                evidence.Add(new JObject
                {
                    ["findocId"] = c.Findoc,
                    ["fincode"] = c.Fincode,
                    ["date"] = c.Date == DateTime.MinValue ? null : JToken.FromObject(c.Date),
                    ["sosource"] = c.Sosource,
                    ["series"] = c.Series,
                    ["bunit"] = c.Bunit,
                    ["lineCount"] = c.LineCount,
                    ["similarity"] = Math.Round(c.Score, 4),
                    ["isStrongPrecedent"] = c == best && strongPrecedent
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["resolver"] = "resolve_document_pattern",
                ["version"] = 2,
                ["mode"] = mode,
                ["needsReview"] = needsReview,
                ["confidence"] = Math.Round(confidence, 4),
                ["threshold"] = ConsensusThreshold,
                ["sourceLineCount"] = sourceLineCount,
                ["proposedTargetLineCount"] = mode == "Consolidated" ? 1 : (mode == "Detailed" ? sourceLineCount : (int?)null),
                ["sampleSize"] = candidates.Count,
                ["similarSampleSize"] = similar.Count,
                ["precedentFindocId"] = strongPrecedent ? best.Findoc : (int?)null,
                ["precedentSimilarity"] = Math.Round(best.Score, 4),
                ["precedentStrong"] = strongPrecedent,
                ["resolvedSosource"] = sosourceConsensus.Ratio >= ConsensusThreshold ? sosourceConsensus.Value : (int?)null,
                ["resolvedSeries"] = seriesConsensus.Ratio >= ConsensusThreshold ? seriesConsensus.Value : (int?)null,
                ["resolvedBunit"] = bunitConsensus.SampleSize > 0 && bunitConsensus.Ratio >= ConsensusThreshold ? bunitConsensus.Value : (int?)null,
                ["sosourceEvidence"] = ConsensusJson(sosourceConsensus),
                ["seriesEvidence"] = ConsensusJson(seriesConsensus),
                ["bunitEvidence"] = ConsensusJson(bunitConsensus),
                ["lineShapeEvidence"] = ConsensusJson(lineCountConsensus),
                ["classification"] = mode == "Consolidated" ? "ConsolidatedCandidate" : (mode == "Detailed" ? "DetailedCandidate" : "Ambiguous"),
                ["reason"] = strongPrecedent ? "similar_historical_precedent" : "historical_consensus",
                ["evidence"] = evidence
            };
        }

        // Backward-compatible wrapper while older UI/runtime calls are being removed.
        public static JObject Analyze(XSupport xSupport, int trdrId, int series, int sosource, int sourceLineCount)
        {
            return ResolveDocumentPattern(xSupport, trdrId, series, sosource, null, null, null, sourceLineCount);
        }

        private static double Score(
            Candidate c,
            int seriesHint,
            int sosourceHint,
            string typeToken,
            string seriesToken,
            string numberToken,
            int sourceLineCount,
            DateTime now)
        {
            double score = 0.0;
            double weight = 0.0;

            if (seriesHint > 0)
            {
                weight += 0.28;
                if (c.Series == seriesHint) score += 0.28;
            }
            if (sosourceHint > 0)
            {
                weight += 0.28;
                if (c.Sosource == sosourceHint) score += 0.28;
            }
            if (sourceLineCount > 0 && c.LineCount > 0)
            {
                weight += 0.20;
                int diff = Math.Abs(sourceLineCount - c.LineCount);
                score += 0.20 * (diff == 0 ? 1.0 : diff == 1 ? 0.65 : diff <= 3 ? 0.35 : 0.10);
            }

            string fincode = NormalizeToken(c.Fincode);
            if (!string.IsNullOrWhiteSpace(typeToken))
            {
                weight += 0.08;
                if (fincode.Contains(typeToken)) score += 0.08;
            }
            if (!string.IsNullOrWhiteSpace(seriesToken))
            {
                weight += 0.08;
                if (fincode.Contains(seriesToken)) score += 0.08;
            }

            // Document number is intentionally not rewarded as an exact match: an exact number would
            // more likely indicate a duplicate. A shared stable prefix can still be weak format evidence.
            if (!string.IsNullOrWhiteSpace(numberToken) && numberToken.Length >= 4)
            {
                weight += 0.03;
                string prefix = numberToken.Substring(0, Math.Min(4, numberToken.Length));
                if (fincode.Contains(prefix)) score += 0.03;
            }

            weight += 0.05;
            if (c.Date != DateTime.MinValue)
            {
                double days = Math.Max(0, (now - c.Date.Date).TotalDays);
                score += 0.05 * (1.0 / (1.0 + days / 365.0));
            }

            return weight <= 0 ? 0 : Math.Min(1.0, score / weight);
        }

        private static Consensus BuildConsensus(List<Candidate> rows, Func<Candidate, int> selector)
        {
            if (rows == null || rows.Count == 0) return new Consensus();
            var usable = rows.Select(selector).Where(x => x > 0).ToList();
            if (usable.Count == 0) return new Consensus();
            var best = usable.GroupBy(x => x).Select(g => new { Value = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Value).First();
            return new Consensus
            {
                Value = best.Value,
                Count = best.Count,
                SampleSize = usable.Count,
                Ratio = best.Count / (double)usable.Count
            };
        }

        private static JObject ConsensusJson(Consensus c)
        {
            return new JObject
            {
                ["value"] = c.SampleSize > 0 ? c.Value : (int?)null,
                ["matches"] = c.Count,
                ["sampleSize"] = c.SampleSize,
                ["ratio"] = Math.Round(c.Ratio, 4)
            };
        }

        private static JObject Failure(string reason)
        {
            return new JObject
            {
                ["success"] = false,
                ["resolver"] = "resolve_document_pattern",
                ["version"] = 2,
                ["mode"] = "Unknown",
                ["needsReview"] = true,
                ["reason"] = reason,
                ["evidence"] = new JArray()
            };
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            string s = value.Trim().ToUpperInvariant();
            s = Regex.Replace(s, "[^0-9A-ZΑ-Ω]", string.Empty);
            return s;
        }

        private static int ToInt(object value)
        {
            if (value == null || value == DBNull.Value) return 0;
            int parsed;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : 0;
        }

        private static DateTime ToDate(object value)
        {
            if (value == null || value == DBNull.Value) return DateTime.MinValue;
            DateTime parsed;
            return DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out parsed) ? parsed : DateTime.MinValue;
        }

        private sealed class Candidate
        {
            public int Findoc;
            public string Fincode;
            public DateTime Date;
            public int Sosource;
            public int Series;
            public int Bunit;
            public int LineCount;
            public double Score;
        }

        private sealed class Consensus
        {
            public int Value;
            public int Count;
            public int SampleSize;
            public double Ratio;
        }
    }
}
