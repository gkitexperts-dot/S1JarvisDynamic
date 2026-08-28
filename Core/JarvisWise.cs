using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;

namespace S1Jarvis.Core
{
    internal sealed class JarvisWiseCandidate
    {
        public string Scope { get; set; }
        public string Type { get; set; }
        public string Keywords { get; set; }
        public string Request { get; set; }
        public string Response { get; set; }
    }

    /// <summary>
    /// Jarvis Wise learned-knowledge layer.
    ///
    /// Design goals:
    /// - The active Soft1 login/session CompanyId is always authoritative.
    /// - Company knowledge never leaks into another company.
    /// - Only reusable knowledge becomes a candidate; one-off data/actions do not.
    /// - A candidate is not trusted knowledge until a human rates it.
    /// - 4-5 stars => VERIFIED, 3 => WEAK, 1-2 => REJECTED.
    /// - Retrieval is deterministic/local; no second AI call is used to decide what to remember.
    /// </summary>
    internal static class JarvisWise
    {
        private const string InternalPrefix = "[JARVIS_WISE_INTERNAL]";
        private const string InternalAck = "[JARVIS_WISE_ACK]";
        private const int KnowledgeParamCode = 500008;
        private const int MaxRetrievalRows = 100;
        private const int MaxInjectedMatches = 3;

        private static readonly Regex WiseMarkerRegex = new Regex(
            @"\[\[JARVIS_WISE\]\]\s*(?<json>\{.*?\})\s*\[\[/JARVIS_WISE\]\]",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> AllowedScopes = new HashSet<string>(
            new[] { "GLOBAL", "COMPANY", "USER" }, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> AllowedTypes = new HashSet<string>(
            new[] { "PROCEDURE", "PROBLEM_SOLUTION", "SCHEMA", "TOOL_FLOW", "BUSINESS_RULE" },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> StopWords = new HashSet<string>(
            new[]
            {
                "και", "να", "το", "τη", "την", "του", "της", "των", "σε", "με", "για", "απο", "από",
                "που", "πως", "πώς", "τι", "ο", "η", "οι", "τα", "ένα", "μια", "μου", "σου", "είναι",
                "ειναι", "στο", "στη", "στην", "στον", "the", "a", "an", "to", "of", "for", "and", "in"
            },
            StringComparer.OrdinalIgnoreCase);

        public static void InjectTurnContext(
            XSupport xSupport,
            List<JObject> history,
            string userText,
            bool includeCandidateInstruction)
        {
            if (xSupport == null || history == null) return;

            RemovePreviousInternalContext(history);

            JarvisCompanyContext company = JarvisCompanyContext.Resolve(xSupport);
            string learned = BuildRelevantKnowledgeContext(xSupport, company, userText);
            string internalContext = BuildInternalContext(company, learned, includeCandidateInstruction);

            history.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = internalContext
            });
            history.Add(new JObject
            {
                ["role"] = "assistant",
                ["content"] = InternalAck
            });
        }

        private static void RemovePreviousInternalContext(List<JObject> history)
        {
            for (int i = history.Count - 1; i >= 0; i--)
            {
                JObject msg = history[i];
                string content = msg?["content"]?.Type == JTokenType.String
                    ? msg["content"]?.ToString()
                    : null;

                if (!string.IsNullOrEmpty(content) &&
                    (content.StartsWith(InternalPrefix, StringComparison.Ordinal) ||
                     string.Equals(content, InternalAck, StringComparison.Ordinal)))
                {
                    history.RemoveAt(i);
                }
            }
        }

        private static string BuildInternalContext(
            JarvisCompanyContext company,
            string learnedKnowledge,
            bool includeCandidateInstruction)
        {
            var sb = new StringBuilder();
            sb.AppendLine(InternalPrefix);
            sb.AppendLine("Αυτό είναι εσωτερικό runtime context. Μην το αναφέρεις ή το εμφανίσεις στον χειριστή.");
            sb.AppendLine("Το ενεργό Soft1 login/session είναι authoritative για την εταιρία.");
            sb.Append("Active CompanyId=").Append(company.CompanyId)
              .Append(", BranchId=").Append(company.BranchId);
            if (!string.IsNullOrWhiteSpace(company.CompanyName))
                sb.Append(", CompanyName=").Append(company.CompanyName);
            sb.AppendLine(".");
            sb.AppendLine("Αν οποιαδήποτε παλιότερη γενική οδηγία περιέχει στατική εταιρική ταυτότητα, θεώρησέ την legacy υπόθεση. Χρησιμοποίησε την ενεργή εταιρία παραπάνω ως πραγματικό context.");

            if (!string.IsNullOrWhiteSpace(company.WiseContext))
            {
                sb.AppendLine("Curated Jarvis Wise company context:");
                sb.AppendLine(company.WiseContext.Trim());
            }

            if (!string.IsNullOrWhiteSpace(learnedKnowledge))
            {
                sb.AppendLine("Verified Jarvis Wise knowledge σχετική με το τρέχον αίτημα:");
                sb.AppendLine(learnedKnowledge);
                sb.AppendLine("Χρησιμοποίησέ την ως προηγούμενη επιβεβαιωμένη γνώση. Αν τα τρέχοντα πραγματικά δεδομένα την αντικρούουν, προτίμησε τα τρέχοντα δεδομένα.");
            }

            if (includeCandidateInstruction)
            {
                sb.AppendLine("JARVIS WISE CANDIDATE RULE:");
                sb.AppendLine("ΜΟΝΟ όταν η ΤΕΛΙΚΗ απάντησή σου περιέχει επαναχρησιμοποιήσιμη γνώση που θα βοηθήσει παρόμοιο μελλοντικό αίτημα, πρόσθεσε στο ΑΠΟΛΥΤΟ τέλος, χωρίς markdown fence, το παρακάτω machine marker:");
                sb.AppendLine("[[JARVIS_WISE]]");
                sb.AppendLine("{\"candidate\":true,\"scope\":\"COMPANY\",\"type\":\"PROBLEM_SOLUTION\",\"keywords\":\"...\",\"request\":\"σύντομη normalized περίληψη αιτήματος\",\"response\":\"σύντομη reusable λύση/γνώση\"}");
                sb.AppendLine("[[/JARVIS_WISE]]");
                sb.AppendLine("Επιτρεπτά type: PROCEDURE, PROBLEM_SOLUTION, SCHEMA, TOOL_FLOW, BUSINESS_RULE.");
                sb.AppendLine("Scope=COMPANY όταν η γνώση εξαρτάται από την τρέχουσα εταιρία/παραμετροποίηση. Scope=GLOBAL μόνο για πραγματικά γενική Soft1/Jarvis γνώση χωρίς company dependency.");
                sb.AppendLine("ΜΗΝ δημιουργήσεις candidate για greeting, απλό lookup τρεχόντων δεδομένων, λίστα/report, συγκεκριμένη one-off πράξη, συγκεκριμένο voucher/email/παραστατικό, ή απάντηση χωρίς reusable διαδικασία/διάγνωση/κανόνα/schema/tool-flow.");
                sb.AppendLine("Το marker είναι machine metadata και δεν αποτελεί μέρος της ορατής απάντησης.");
            }

            return sb.ToString();
        }

        public static bool TryExtractCandidate(
            string assistantText,
            string fallbackRequest,
            out JarvisWiseCandidate candidate,
            out string visibleText)
        {
            candidate = null;
            visibleText = assistantText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(assistantText)) return false;

            Match match = WiseMarkerRegex.Match(assistantText);
            if (!match.Success) return false;

            visibleText = WiseMarkerRegex.Replace(assistantText, string.Empty).Trim();
            try
            {
                JObject obj = JObject.Parse(match.Groups["json"].Value);
                bool isCandidate = obj["candidate"] == null || obj.Value<bool?>("candidate") == true;
                if (!isCandidate) return false;

                string scope = NormalizeScope(obj.Value<string>("scope"));
                string type = NormalizeType(obj.Value<string>("type"));
                string keywords = CleanText(obj.Value<string>("keywords"), 4000);
                string request = CleanText(obj.Value<string>("request"), 12000);
                string response = CleanText(obj.Value<string>("response"), 20000);

                if (string.IsNullOrWhiteSpace(request))
                    request = CleanText(fallbackRequest, 12000);
                if (string.IsNullOrWhiteSpace(response))
                    response = CleanText(visibleText, 20000);

                if (string.IsNullOrWhiteSpace(request) || string.IsNullOrWhiteSpace(response))
                    return false;

                candidate = new JarvisWiseCandidate
                {
                    Scope = scope,
                    Type = type,
                    Keywords = keywords,
                    Request = request,
                    Response = response
                };
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[JARVIS-WISE] candidate marker parse failed: " + ex.Message);
                return false;
            }
        }

        public static string StripMarker(string assistantText)
        {
            return string.IsNullOrWhiteSpace(assistantText)
                ? assistantText
                : WiseMarkerRegex.Replace(assistantText, string.Empty).Trim();
        }

        public static void CleanMarkerFromHistory(List<JObject> history)
        {
            if (history == null) return;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                JObject msg = history[i];
                if (!string.Equals(msg?["role"]?.ToString(), "assistant", StringComparison.OrdinalIgnoreCase))
                    continue;

                JToken content = msg["content"];
                if (content is JArray blocks)
                {
                    foreach (JObject block in blocks.OfType<JObject>())
                    {
                        if (!string.Equals(block["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string text = block["text"]?.ToString();
                        if (text != null && WiseMarkerRegex.IsMatch(text))
                            block["text"] = StripMarker(text);
                    }
                }
                else if (content?.Type == JTokenType.String)
                {
                    string text = content.ToString();
                    if (WiseMarkerRegex.IsMatch(text))
                        msg["content"] = StripMarker(text);
                }
                return;
            }
        }

        public static int CreateCandidateSoAction(XSupport xSupport, JarvisWiseCandidate candidate)
        {
            if (xSupport == null) throw new ArgumentNullException(nameof(xSupport));
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));

            JarvisCompanyContext company = JarvisCompanyContext.Resolve(xSupport);
            int series = GetKnowledgeSeries(xSupport);

            XModule module = xSupport.CreateModule("SOTASK");
            XTable soaction = module.GetTable("SOACTION");
            try
            {
                module.InsertData();
                soaction.Current["SERIES"] = series;
                soaction.Current["COMMENTS"] = "Jarvis Wise";
                soaction.Current["REMARKS"] = CleanText(candidate.Keywords, 2000);

                TrySet(soaction, "cccInitRequest", candidate.Request);
                TrySet(soaction, "cccFinalResp", candidate.Response);

                soaction.Current["cccJWCompany"] = company.CompanyId;
                soaction.Current["cccJWBranch"] = company.BranchId;
                soaction.Current["cccJWScope"] = NormalizeScope(candidate.Scope);
                soaction.Current["cccJWType"] = NormalizeType(candidate.Type);
                soaction.Current["cccJWStatus"] = "CANDIDATE";
                soaction.Current["cccJWKeywords"] = candidate.Keywords ?? string.Empty;
                soaction.Current["cccJWRequest"] = candidate.Request ?? string.Empty;
                soaction.Current["cccJWResponse"] = candidate.Response ?? string.Empty;
                soaction.Current["cccJWRating"] = 0;
                soaction.Current["cccJWSourceUser"] = xSupport.ConnectionInfo.UserId;
                soaction.Current["cccJWCreatedAt"] = DateTime.Now;
                soaction.Current["cccJWVersion"] = 1;

                soaction.Current["ACTOR"] = xSupport.ConnectionInfo.UserId;
                soaction.Current["ORDEREDBY"] = xSupport.ConnectionInfo.UserId;
                soaction.Current["ACTSTATUS"] = 3;

                int id = module.PostData();
                DebugLog.Log("[JARVIS-WISE] candidate stored; soactionId=" + id +
                    " company=" + company.CompanyId +
                    " scope=" + candidate.Scope +
                    " type=" + candidate.Type);
                return id;
            }
            finally
            {
                soaction.Dispose();
                module.Dispose();
            }
        }

        public static void PromoteHelpRecord(
            XSupport xSupport,
            int soactionId,
            string fallbackRequest,
            string fallbackResponse)
        {
            if (soactionId <= 0) return;
            JarvisCompanyContext company = JarvisCompanyContext.Resolve(xSupport);

            XModule module = xSupport.CreateModule("SOTASK");
            XTable soaction = module.GetTable("SOACTION");
            try
            {
                module.LocateData(soactionId);
                soaction.Current.Edit(soactionId);

                string request = SafeCurrentString(soaction, "cccInitRequest") ?? fallbackRequest;
                string response = SafeCurrentString(soaction, "cccFinalResp") ?? fallbackResponse;
                string keywords = SafeCurrentString(soaction, "REMARKS");

                soaction.Current["cccJWCompany"] = company.CompanyId;
                soaction.Current["cccJWBranch"] = company.BranchId;
                soaction.Current["cccJWScope"] = "COMPANY";
                soaction.Current["cccJWType"] = "PROBLEM_SOLUTION";
                soaction.Current["cccJWStatus"] = "CANDIDATE";
                soaction.Current["cccJWKeywords"] = keywords ?? string.Empty;
                soaction.Current["cccJWRequest"] = request ?? string.Empty;
                soaction.Current["cccJWResponse"] = response ?? string.Empty;
                soaction.Current["cccJWRating"] = 0;
                soaction.Current["cccJWSourceUser"] = xSupport.ConnectionInfo.UserId;
                soaction.Current["cccJWCreatedAt"] = DateTime.Now;
                soaction.Current["cccJWVersion"] = 1;

                module.PostData();
                DebugLog.Log("[JARVIS-WISE] Help record promoted; soactionId=" + soactionId +
                    " company=" + company.CompanyId);
            }
            finally
            {
                soaction.Dispose();
                module.Dispose();
            }
        }

        public static void ApplyRating(XSupport xSupport, int soactionId, int rating)
        {
            if (soactionId <= 0 || rating < 1 || rating > 5) return;

            XModule module = xSupport.CreateModule("SOTASK");
            XTable soaction = module.GetTable("SOACTION");
            try
            {
                module.LocateData(soactionId);
                soaction.Current.Edit(soactionId);

                string currentStatus = SafeCurrentString(soaction, "cccJWStatus");
                if (string.IsNullOrWhiteSpace(currentStatus))
                    return;

                string status = rating >= 4 ? "VERIFIED" : rating == 3 ? "WEAK" : "REJECTED";
                soaction.Current["cccJWRating"] = rating;
                soaction.Current["cccJWStatus"] = status;
                soaction.Current["SOSMALLINT"] = rating;
                if (rating >= 4)
                    soaction.Current["cccJWVerifiedAt"] = DateTime.Now;

                module.PostData();
                DebugLog.Log("[JARVIS-WISE] rating applied; soactionId=" + soactionId +
                    " rating=" + rating + " status=" + status);
            }
            finally
            {
                soaction.Dispose();
                module.Dispose();
            }
        }

        private static string BuildRelevantKnowledgeContext(
            XSupport xSupport,
            JarvisCompanyContext company,
            string userText)
        {
            if (string.IsNullOrWhiteSpace(userText)) return null;

            try
            {
                int series = GetKnowledgeSeries(xSupport);
                XTable table = xSupport.GetSQLDataSet(
                    "SELECT TOP 100 SOACTION, cccJWCompany, cccJWScope, cccJWType, " +
                    "cccJWKeywords, cccJWRequest, cccJWResponse, cccJWRating " +
                    "FROM SOACTION WHERE SERIES=:1 AND cccJWStatus='VERIFIED' " +
                    "AND (cccJWScope='GLOBAL' OR (cccJWScope='COMPANY' AND cccJWCompany=:2)) " +
                    "ORDER BY SOACTION DESC",
                    series, company.CompanyId);

                if (table == null || table.Count == 0) return null;
                DataTable dt = table.CreateDataTable(true);
                HashSet<string> queryTokens = Tokenize(userText);

                var ranked = new List<Tuple<int, DataRow>>();
                foreach (DataRow row in dt.Rows)
                {
                    int score = Score(queryTokens,
                        SafeRowString(row, "cccJWKeywords"),
                        SafeRowString(row, "cccJWRequest"),
                        SafeRowString(row, "cccJWResponse"));
                    if (score > 0)
                        ranked.Add(Tuple.Create(score, row));
                }

                var best = ranked
                    .OrderByDescending(x => x.Item1)
                    .ThenByDescending(x => Convert.ToInt32(x.Item2["SOACTION"]))
                    .Take(MaxInjectedMatches)
                    .ToList();

                if (best.Count == 0) return null;

                var sb = new StringBuilder();
                foreach (var item in best)
                {
                    DataRow row = item.Item2;
                    sb.Append("- [")
                      .Append(SafeRowString(row, "cccJWScope") ?? "COMPANY")
                      .Append("/")
                      .Append(SafeRowString(row, "cccJWType") ?? "KNOWLEDGE")
                      .Append("] ")
                      .Append(SafeRowString(row, "cccJWResponse") ?? string.Empty)
                      .AppendLine();
                }

                DebugLog.Log("[JARVIS-WISE] retrieval company=" + company.CompanyId +
                    " candidates=" + dt.Rows.Count + " matches=" + best.Count);
                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[JARVIS-WISE] retrieval unavailable; " + ex.Message);
                return null;
            }
        }

        private static int Score(HashSet<string> queryTokens, string keywords, string request, string response)
        {
            if (queryTokens.Count == 0) return 0;
            int score = 0;
            score += Overlap(queryTokens, Tokenize(keywords)) * 4;
            score += Overlap(queryTokens, Tokenize(request)) * 2;
            score += Overlap(queryTokens, Tokenize(response));
            return score;
        }

        private static int Overlap(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int count = 0;
            foreach (string token in a)
                if (b.Contains(token)) count++;
            return count;
        }

        private static HashSet<string> Tokenize(string text)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (Match match in Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{Nd}_]{2,}"))
            {
                string token = match.Value;
                if (!StopWords.Contains(token)) result.Add(token);
            }
            return result;
        }

        private static int GetKnowledgeSeries(XSupport xSupport)
        {
            XTable t = xSupport.GetSQLDataSet(
                "SELECT ParamValue FROM cccParams WHERE ParamCode=:1",
                KnowledgeParamCode);
            if (t == null || t.Count == 0)
                throw new Exception("Δεν βρέθηκε η παράμετρος 500008 (Σειρά Knowledge Base) στο cccParams.");
            return Convert.ToInt32(t.Current["ParamValue"]);
        }

        private static string NormalizeScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope) || !AllowedScopes.Contains(scope))
                return "COMPANY";
            return scope.Trim().ToUpperInvariant();
        }

        private static string NormalizeType(string type)
        {
            if (string.IsNullOrWhiteSpace(type) || !AllowedTypes.Contains(type))
                return "PROBLEM_SOLUTION";
            return type.Trim().ToUpperInvariant();
        }

        private static string CleanText(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string text = value.Trim();
            return text.Length <= maxLength ? text : text.Substring(0, maxLength);
        }

        private static string SafeRowString(DataRow row, string column)
        {
            if (row == null || !row.Table.Columns.Contains(column)) return null;
            object value = row[column];
            return value == null || value == DBNull.Value ? null : Convert.ToString(value);
        }

        private static string SafeCurrentString(XTable table, string field)
        {
            try
            {
                object value = table.Current[field];
                return value == null || value == DBNull.Value ? null : Convert.ToString(value);
            }
            catch
            {
                return null;
            }
        }

        private static void TrySet(XTable table, string field, object value)
        {
            try { table.Current[field] = value; }
            catch { }
        }
    }
}
