using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using S1Jarvis.Access;
using Softone;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // DrBatchEngine — ΙΔΙΑ 10 βήματα με S1DocReader.ExtractionJobEngine.
    // Αυτόνομο αντίγραφο για τον Jarvis DR (WebView2 host).
    //
    //   1. QR read (JS το έχει ήδη κάνει → qrLink στο input)
    //   2. ΑΑΔΕ fetch
    //   3. ΑΦΜ detection (agent)
    //   4. Profile load/create (Cosmos via proof)
    //   5. Trader history (mode SOSOURCE/SERIES από FINDOC)
    //   6. Extraction (agent — με profile hint αν υπάρχει)
    //   7. Soft1 lookup (CCCMAPITEMS) + duplicate
    //   8. Confidence
    //   9. AutoCommit → ExecuteRegisterDrDocument
    //  10. Learn + history + meta
    //
    // Progress: callback ανά βήμα (fileId, step, message, partial result)
    // ══════════════════════════════════════════════════════════════════════
    public sealed class DrBatchEngine
    {
        public sealed class FileInput
        {
            public string FileId, FileName, Base64, MimeType, QrLink;
        }

        public sealed class FileResult
        {
            public string FileId, FileName, ErrorMessage;
            public JObject Detection, Extraction, Trader, AadeData, HistoryProfile, Profile;
            public DrConfidenceResult Confidence;
            public DrDecision Decision;
            public int FindocId;
            public JObject ToJson() => new JObject
            {
                ["fileId"] = FileId, ["fileName"] = FileName, ["decision"] = Decision.ToString(),
                ["findocId"] = FindocId, ["errorMessage"] = ErrorMessage,
                ["detection"] = Detection, ["extraction"] = Extraction, ["trader"] = Trader,
                ["historyProfile"] = HistoryProfile, ["confidence"] = Confidence?.ToJson(),
                ["aade"] = AadeData
            };
        }

        public sealed class Progress
        {
            public string FileId, FileName, Step, Message; public int Index, Total; public double Score; public string Decision;
            public JObject ToJson() => new JObject { ["type"] = "dr_batch_progress", ["fileId"] = FileId, ["fileName"] = FileName, ["index"] = Index, ["total"] = Total, ["step"] = Step, ["message"] = Message, ["score"] = Score, ["decision"] = Decision };
        }

        private readonly XSupport _x;
        private readonly JarvisAgentClient _agent;
        private readonly string _agentAccountRef;
        private readonly Action<Progress> _onProgress;

        public DrBatchEngine(XSupport x, JarvisAgentClient agent, string agentAccountRef, Action<Progress> onProgress)
        {
            _x = x; _agent = agent; _agentAccountRef = agentAccountRef; _onProgress = onProgress;
        }

        public async Task<List<FileResult>> RunAsync(IList<FileInput> files, CancellationToken ct)
        {
            var results = new List<FileResult>();
            string companyAfm = await Task.Run(() => JarvisTools.GetCompanyAfm(_x));
            string userName = _x.ConnectionInfo?.UserName ?? "jarvis";
            for (int i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                results.Add(await ProcessAsync(files[i], i + 1, files.Count, companyAfm, userName, ct));
            }
            DebugLog.Log($"[dr-batch] complete total={results.Count} auto={results.Count(r => r.Decision == DrDecision.AutoCommit)} review={results.Count(r => r.Decision == DrDecision.NeedsReview)} blocked={results.Count(r => r.Decision == DrDecision.Blocked)}");
            return results;
        }

        private async Task<FileResult> ProcessAsync(FileInput f, int idx, int total, string companyAfm, string userName, CancellationToken ct)
        {
            var r = new FileResult { FileId = f.FileId, FileName = f.FileName };
            void Report(string step, string msg, double score = 0, string decision = null) =>
                _onProgress?.Invoke(new Progress { FileId = f.FileId, FileName = f.FileName, Index = idx, Total = total, Step = step, Message = msg, Score = score, Decision = decision });

            try
            {
                // 2. ΑΑΔΕ
                AadeInvoiceData aade = new AadeInvoiceData();
                if (!string.IsNullOrEmpty(f.QrLink))
                {
                    Report("aade", "Επαλήθευση ΑΑΔΕ...");
                    aade = await DrAadeVerifier.FetchAsync(f.QrLink);
                }
                r.AadeData = JObject.FromObject(aade);

                // 3. ΑΦΜ
                Report("detect", "Αναγνώριση ΑΦΜ...");
                r.Detection = await _agent.DetectDocumentIssuerAsync(_agentAccountRef, f.Base64, f.MimeType);
                if (r.Detection["success"]?.Value<bool>() != true)
                {
                    r.ErrorMessage = r.Detection["errorMessage"]?.ToString() ?? "Δεν αναγνωρίστηκε ΑΦΜ.";
                    r.Decision = DrDecision.Blocked;
                    r.Confidence = new DrConfidenceResult { Decision = DrDecision.Blocked, Reasons = { r.ErrorMessage }, Breakdown = "BLOCKED: " + r.ErrorMessage };
                    Report("done", r.ErrorMessage, 0, "Blocked");
                    return r;
                }
                string afm = new string((r.Detection["issuerAfm"]?.ToString() ?? "").Where(char.IsDigit).ToArray());
                string issuerName = r.Detection["issuerName"]?.ToString() ?? "";
                string docType = r.Detection["docType"]?.ToString() ?? "ΤΙΜ";
                bool afmValid = JarvisTools.IsValidGreekAfm(afm);

                // 4. Profile
                Report("profile", "Φόρτωση profile...");
                var (profile, isNew) = await DrProfileService.LoadOrCreateAsync(_x, afm, issuerName, userName);
                r.Profile = profile;

                // 5. Trader + history
                Report("trader", "Αναζήτηση συναλλασσόμενου...");
                r.Trader = JObject.Parse(await Task.Run(() => JarvisTools.ExecuteFindTraderByAfm(_x, afm)));
                bool traderFound = r.Trader["found"]?.Value<bool>() == true;
                int trdrId = r.Trader["trdrId"]?.Value<int>() ?? 0;
                int histSample = 0;
                if (traderFound)
                {
                    r.HistoryProfile = JObject.Parse(await Task.Run(() => JarvisTools.ExecuteTraderHistoryProfile(_x, trdrId)));
                    histSample = r.HistoryProfile["sampleSize"]?.Value<int>() ?? 0;
                }

                // 6. Extraction
                Report("extract", isNew ? "Γενική εξαγωγή..." : "Στοχευμένη εξαγωγή...");
                r.Extraction = await _agent.ExtractDocumentLinesAsync(_agentAccountRef, f.Base64, f.MimeType, companyAfm);
                if (r.Extraction["success"]?.Value<bool>() != true)
                {
                    r.ErrorMessage = r.Extraction["errorMessage"]?.ToString() ?? "Αποτυχία εξαγωγής.";
                    r.Decision = DrDecision.Blocked;
                    r.Confidence = new DrConfidenceResult { Decision = DrDecision.Blocked, Reasons = { r.ErrorMessage }, Breakdown = "BLOCKED: " + r.ErrorMessage };
                    Report("done", r.ErrorMessage, 0, "Blocked");
                    return r;
                }

                // 7. Match + duplicate
                Report("match", "Αντιστοίχιση Soft1...");
                bool allMatched = false, dup = false;
                JArray lineItems = r.Extraction["line_items"] as JArray ?? new JArray();
                if (traderFound)
                {
                    var matched = JObject.Parse(await Task.Run(() => JarvisTools.ExecuteMatchExtractedItems(_x, trdrId, lineItems)));
                    r.Extraction["line_items"] = matched["results"];
                    var results = matched["results"] as JArray ?? new JArray();
                    allMatched = results.Count > 0 && results.All(l => l["matched"]?.Value<bool>() == true);
                    string docNumber = r.Extraction["document_info"]?["number"]?.ToString();
                    string docDate = r.Extraction["document_info"]?["date"]?.ToString();
                    var dupCheck = JObject.Parse(await Task.Run(() => JarvisTools.ExecuteCheckDuplicateDocument(_x, trdrId, docNumber, docDate)));
                    dup = dupCheck["found"]?.Value<bool>() == true;
                }

                // 8. Confidence
                double aiConf = r.Extraction["confidence"]?.Value<double>() ?? r.Detection["confidence"]?.Value<double>() ?? 0.85;
                var aadeScore = DrAadeVerifier.Compare(aade, r.Extraction, afm);
                r.Confidence = DrConfidenceCalculator.Calculate(aiConf, aadeScore, histSample, profile, docType,
                    r.Extraction["detected_labels"] as JObject, traderFound, dup, allMatched, afmValid);
                r.Decision = r.Confidence.Decision;
                Report("confidence", r.Confidence.Breakdown, r.Confidence.FinalScore, r.Decision.ToString());

                // 9. AutoCommit
                if (r.Decision == DrDecision.AutoCommit && traderFound && r.HistoryProfile != null)
                {
                    Report("commit", "Αυτόματη καταχώρηση...");
                    var input = new JObject
                    {
                        ["trdrId"] = trdrId,
                        ["sosource"] = r.HistoryProfile["sosource"],
                        ["series"] = r.HistoryProfile["series"],
                        ["docType"] = docType,
                        ["docNumber"] = r.Extraction["document_info"]?["number"],
                        ["docDate"] = r.Extraction["document_info"]?["date"],
                        ["lineItems"] = r.Extraction["line_items"],
                        ["mode"] = "auto"
                    };
                    var reg = JObject.Parse(await Task.Run(() => JarvisTools.ExecuteRegisterDrDocument(_x, input)));
                    if (reg["success"]?.Value<bool>() == true)
                        r.FindocId = reg["findocId"]?.Value<int>() ?? 0;
                    else
                    {
                        r.Decision = DrDecision.NeedsReview;
                        r.ErrorMessage = "AutoCommit απέτυχε: " + reg["error"]?.ToString();
                        r.Confidence.Reasons.Add(r.ErrorMessage);
                    }
                }

                // 10. Learn + history
                if (r.Decision == DrDecision.AutoCommit && DrProfileService.Learn(profile, docType, r.Extraction["detected_labels"] as JObject))
                    await DrProfileService.SaveAsync(_x, profile, userName);
                await DrProfileService.RecordHistoryAsync(_x, afm, userName, issuerName, docType,
                    r.Extraction["document_info"]?["series"]?.ToString(), r.Extraction["document_info"]?["number"]?.ToString(),
                    r.Extraction["document_info"]?["date"]?.ToString(), aiConf, r.Extraction.ToString(Newtonsoft.Json.Formatting.None));
                await DrProfileService.UpdateMetaAsync(_x, afm, docType, aiConf, false);

                Report("done", r.Confidence.Breakdown, r.Confidence.FinalScore, r.Decision.ToString());
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr-batch] " + f.FileName + " EXCEPTION: " + ex);
                r.ErrorMessage = ex.Message; r.Decision = DrDecision.Blocked;
                r.Confidence = r.Confidence ?? new DrConfidenceResult { Decision = DrDecision.Blocked, Reasons = { "Σφάλμα: " + ex.Message }, Breakdown = "ERROR: " + ex.Message };
                Report("done", ex.Message, 0, "Blocked");
            }
            return r;
        }
    }
}
