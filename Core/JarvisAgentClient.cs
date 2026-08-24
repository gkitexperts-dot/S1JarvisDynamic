using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;
using S1Jarvis.Access;

namespace S1Jarvis.Core
{
    // ══════════════════════════════════════════════════════════════════════
    // JarvisAgentClient
    //
    // Ίδια λογική με S1DocReader's ProxyAgentClient (CallVisionApi): φτιάχνει
    // το ΕΤΟΙΜΟ Anthropic Messages API request body, το στέλνει στον Nexus
    // proxy (/agent/vision) με agentAccountRef αντί για key. Το key ΠΟΤΕ δεν
    // ζει στον client.
    //
    // Διαφορά από το DocReader: εδώ τρέχουμε το ΔΙΚΟ ΜΑΣ multi-turn tool-use
    // loop (DocReader κάνει μονο-γύρες κλήσεις vision/extraction, όχι tools).
    // ══════════════════════════════════════════════════════════════════════
    public class JarvisAgentClient
    {
        // Claude Opus 5, thinking ON BY DEFAULT (δεν το απενεργοποιούμε -
        // βλ. σχόλιο στο AskAsync για τους λόγους).
        private const string Model = "claude-opus-5";
        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - 8000 ήταν πολύ σφιχτό για tool calls με μεγάλο
        // περιεχόμενο (π.χ. send_email attachmentContent με 148 γραμμές
        // πελατών) - ζωντανό bug: η απάντηση έκοβε στη μέση
        // (stop_reason=="max_tokens"), βλ. σχόλιο πιο κάτω στο AskAsync
        // ("ΜΟΝΙΜΑ 400"). Ανέβηκε σε 16000 - μειώνει τη συχνότητα, ΔΕΝ
        // λύνει την κατηγορία bug μόνο του (πάντα μπορεί να υπάρξει αρκετά
        // μεγάλο αίτημα να ξαναχτυπήσει το όριο - γι' αυτό υπάρχει ΚΑΙ το
        // δομικό fix στο AskAsync).
        private const int MaxTokens = 16000;
        // 6 ήταν πολύ σφιχτό, ανέβηκε σε 10 - ΑΚΟΜΑ όχι αρκετό για ερωτήματα
        // λογαριασμού/κινήσεων χωρίς γνωστό schema hint (βλ. BuildSystemPrompt
        // "ΓΝΩΣΤΟ SCHEMA" παρακάτω): επιβεβαιωμένο bug #2 - query για
        // "στοιχεία και κινήσεις" ενός TRDR χρειάστηκε 8 iterations ΜΟΝΟ για
        // schema discovery (TRDR→FINDOC→λάθος FINTRD→TRDBALSHEET→SERIES...)
        // πριν βρει τα σωστά δεδομένα στο iteration 8, μετά έκανε ΑΚΟΜΑ ένα
        // διερευνητικό query στο 9 αντί να απαντήσει, και χτύπησε το όριο.
        // Τα schema hints έπρεπε να κόψουν τα iterations 0-7, αλλά κρατάμε
        // και μεγαλύτερο buffer για ό,τι δεν καλύπτουν τα hints.
        private const int MaxIterations = 14;

        private static readonly HttpClient _http = new HttpClient();

        // Το "Stop" button του UI ακυρώνει το ΤΡΕΧΟΝ in-flight HTTP call προς
        // τον Nexus μέσω αυτού - βλ. CancelCurrent()/JarvisShell stop sentinel.
        // Ένα μόνο AskAsync τρέχει τη φορά σε αυτό το UI (το composer δεν
        // επιτρέπει δεύτερο send όσο περιμένει), άρα ένα static field αρκεί.
        private CancellationTokenSource _cts;

        static JarvisAgentClient()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
        }

        // Καλείται από το JarvisShell όταν λάβει το sentinel μήνυμα "Stop"
        // από το JS UI, ενώ ένα AskAsync είναι ήδη σε εξέλιξη (ξεχωριστό,
        // παράλληλο WebMessageReceived event - βλ. σχόλιο εκεί). Ακυρώνει
        // το token· το AskAsync το πιάνει στο catch παρακάτω και επιστρέφει
        // φιλικό μήνυμα αντί να σκάσει.
        public void CancelCurrent()
        {
            try { _cts?.Cancel(); }
            catch (ObjectDisposedException) { /* already done, αγνόησε */ }
        }

        // attachmentBase64/attachmentMimeType: προαιρετική εικόνα/PDF
        // (base64, βλ. index.html attachBtn/paste) - ίδιο πνεύμα με το
        // ProxyAgentClient.CallVisionApi του S1DocReader (content block
        // "image"/"document" πριν το "text", βλ. Anthropic vision API).
        // Χωρίς attachment, content = απλό string όπως πάντα.
        // onProgress: προαιρετικό callback, καλείται ΜΙΑ φορά ανά iteration
        // που συνεχίζει το loop (δηλαδή stop_reason=="tool_use", ΟΧΙ στο
        // τελικό iteration - βλ. σχόλιο πριν το onProgress?.Invoke παρακάτω)
        // με ένα ΣΥΝΤΟΜΟ, φιλικό caption για το τι κάνει ο Jarvis αυτή τη
        // στιγμή - βλ. index.html #orbCaption ("Legend" κάτω από το orb).
        // helpMode: true ΜΟΝΟ για τις κλήσεις μέσα από τον καμβό του Help
        // mode (ξεχωριστό conversation history από το κανονικό chat, βλ.
        // JarvisShell._helpConversation) - αλλάζει το system prompt (βλ.
        // BuildSystemPrompt) ώστε ο Jarvis να ρωτάει για το πρόβλημα και,
        // όταν έχει λύση, να κλείνει με το marker block (README "Mapping
        // πεδίων" - ΛΕΞΕΙΣ-ΚΛΕΙΔΙΑ/ΠΕΡΙΛΗΨΗ ΑΙΤΗΜΑΤΟΣ/ΛΥΣΗ).
        // browserMode/onNavigate/onReadPage: για τον καμβό του Browser mode
        // (βλ. README) - ξεχωριστό conversation history
        // (JarvisShell._browserConversation), εκθέτει τα `open_url`/
        // `read_page_content` tools. onNavigate κάνει το πραγματικό
        // navigate, onReadPage διαβάζει το ορατό κείμενο της τρέχουσας
        // σελίδας (ExecuteScriptAsync, βλ. JarvisShell.browserView) - ΝΕΟ
        // 15/08, πριν ο Jarvis μπορούσε μόνο να ανοίγει σελίδες, όχι να
        // βλέπει τι δείχνουν.
        // emailMode: ΝΕΟ 17/08, ρητό αίτημα χρήστη - για τον καμβό της
        // κουρτίνας "Email" (README Roadmap #1, index.html #emailCurtain,
        // 2 tabs Email/Calendar) - ξεχωριστό conversation history
        // (JarvisShell._emailConversation), δικό του tool subset (email +
        // task creation, ΟΧΙ browser-specific open_url/read_page_content).
        // onFilterEmailInbox/onFilterCalendar - ΝΕΟ 17/08, ρητό αίτημα
        // χρήστη ("οι πληροφορίες για φιλτράρισμα θέλω να γίνονται στο
        // main παράθυρο, στο chat box θέλω να μένει ΜΟΝΟ chat") - καλούνται
        // όταν ο Claude χρησιμοποιήσει filter_email_inbox/filter_calendar
        // (βλ. JarvisEmailAccess) - κάνουν το ΠΡΑΓΜΑΤΙΚΟ postMessage στο
        // index.html που ενημερώνει/ξανα-φορτώνει το ΚΥΡΙΟ παράθυρο.
        // Action<string,string,string> = (date, searchText, insight) - ΝΕΟ
        // 17/08, ρητό αίτημα χρήστη "συνθέτει φίλτρο, δηλαδή ημερομηνία και
        // κάτι ακόμα" + "στο chat box θέλω να μένει ΜΟΝΟ chat" (το insight
        // είναι το "κάτι ακόμα" - αναλυτική απάντηση που ζει στο κύριο
        // παράθυρο, ΟΧΙ στο chat) - searchText/insight προαιρετικά (null).
        public async Task<string> AskAsync(
            string agentAccountRef, XSupport xSupport,
            List<JObject> history, string userText,
            string attachmentBase64 = null, string attachmentMimeType = null,
            Action<string> onProgress = null, bool helpMode = false,
            bool browserMode = false, Action<string> onNavigate = null,
            Func<Task<string>> onReadPage = null,
            Func<int?, Task<string>> onExtractPageTables = null,
            bool emailMode = false,
            Action<string, string, string, JArray> onFilterEmailInbox = null,
            Action<string, string, string> onFilterCalendar = null,
            Action<string, JArray> onShowCalendarEntries = null,
            bool courierMode = false,
            Action<JArray> onShowCourierDocuments = null,
            Action<JArray> onShowContactResults = null,
            Func<string, int[], Task<string>> onExportShownTable = null,
            string routingHint = null,
            Action<string> onModeChosen = null,
            int maxIterations = MaxIterations)
        {
            JToken userContent;
            if (!string.IsNullOrEmpty(attachmentBase64))
            {
                bool isPdf = attachmentMimeType == "application/pdf";
                bool isImage = attachmentMimeType != null &&
                    attachmentMimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                if (!isPdf && !isImage)
                {
                    return "✖ Αυτός ο τύπος αρχείου (" + attachmentMimeType +
                        ") δεν υποστηρίζεται ακόμα - μόνο εικόνες (PNG/JPEG) και PDF.";
                }

                var contentArray = new JArray
                {
                    new JObject
                    {
                        ["type"] = isPdf ? "document" : "image",
                        ["source"] = new JObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = attachmentMimeType,
                            ["data"] = attachmentBase64
                        }
                    }
                };
                if (!string.IsNullOrWhiteSpace(userText))
                    contentArray.Add(new JObject { ["type"] = "text", ["text"] = userText });
                userContent = contentArray;
            }
            else
            {
                userContent = userText;
            }

            history.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = userContent
            });

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            var myCts = _cts;

            int reportDecimalPlaces = JarvisTools.GetReportDecimalPlaces(xSupport);
            string extraInstructions = JarvisTools.GetOptionalParamString(xSupport, 500027);
            string currentUserName = JarvisTools.GetCurrentUserDisplayName(xSupport);

            bool itemMode = false;
            bool traderMode = false;
            bool isEmailCurtain = emailMode;
            if (!helpMode && !browserMode && !emailMode && !courierMode)
            {
                RoutingDecision routed = RouteMainChatAgent(userText, routingHint);
                onModeChosen?.Invoke(routed.StickyLabel);
                itemMode = routed.Item;
                traderMode = routed.Trader;
                emailMode = routed.Email;
            }

            string activeAgentName = helpMode ? "Sage"
                : browserMode ? "Scout"
                : courierMode ? "Sprint"
                : itemMode ? "Forge"
                : traderMode ? "Compass"
                : emailMode ? "Echo"
                : "Atlas";
            string resolvedModel = ResolveAgentModel(xSupport, activeAgentName);

            try
            {
                for (int iteration = 0; iteration < maxIterations; iteration++)
                {
                    bool isLastIteration = iteration == maxIterations - 1;

                    string systemPromptText = BuildSystemPrompt(xSupport, forceFinalAnswer: isLastIteration,
                        helpMode: helpMode, reportDecimalPlaces: reportDecimalPlaces,
                        browserMode: browserMode, emailMode: emailMode, courierMode: courierMode,
                        extraInstructions: extraInstructions,
                        itemMode: itemMode, traderMode: traderMode,
                        currentUserName: currentUserName);

                    object[] toolsForCall = isLastIteration
                            ? new object[0]
                            : browserMode
                                ? new object[] {
                                    JarvisTools.OpenUrlToolDefinition,
                                    JarvisTools.ReadPageContentToolDefinition,
                                    JarvisTools.ExtractPageTablesToolDefinition,
                                    JarvisTools.QueryDataToolDefinition,
                                    JarvisTools.ExportQueryToFileToolDefinition,
                                    JarvisTools.OpenDocumentToolDefinition,
                                    JarvisTools.GetConversionTargetsToolDefinition,
                                    JarvisTools.CreateCrmTaskToolDefinition,
                                    JarvisTools.CreateOrderToolDefinition,
                                    JarvisEmailAccess.ReadEmailToolDefinition,
                                    JarvisEmailAccess.DownloadEmailAttachmentToolDefinition,
                                    JarvisEmailAccess.SendEmailToolDefinition,
                                    JarvisEmailAccess.ReplyEmailToolDefinition,
                                    JarvisTools.ShowContactResultsToolDefinition,
                                    JarvisEmailAccess.SearchOutlookContactsToolDefinition,
                                    JarvisEmailAccess.CreateOutlookEventToolDefinition,
                                    JarvisItems.GetItemTemplateToolDefinition,
                                    JarvisItems.CreateItemToolDefinition
                                }
                                : isEmailCurtain
                                    ? new object[] {
                                        JarvisTools.QueryDataToolDefinition,
                                        JarvisTools.ExportQueryToFileToolDefinition,
                                        JarvisTools.OpenDocumentToolDefinition,
                                        JarvisTools.CreateCrmTaskToolDefinition,
                                        JarvisEmailAccess.ReadEmailToolDefinition,
                                        JarvisEmailAccess.DownloadEmailAttachmentToolDefinition,
                                        JarvisEmailAccess.ReadCalendarToolDefinition,
                                        JarvisEmailAccess.FilterEmailInboxToolDefinition,
                                        JarvisEmailAccess.FilterCalendarToolDefinition,
                                        JarvisEmailAccess.ShowCalendarEntriesToolDefinition,
                                        JarvisEmailAccess.SendEmailToolDefinition,
                                        JarvisEmailAccess.ReplyEmailToolDefinition,
                                        JarvisTools.ShowContactResultsToolDefinition,
                                        JarvisEmailAccess.SearchOutlookContactsToolDefinition,
                                        JarvisEmailAccess.CreateOutlookEventToolDefinition
                                    }
                                    : courierMode
                                        ? new object[] {
                                            JarvisTools.QueryDataToolDefinition,
                                            JarvisTools.OpenDocumentToolDefinition,
                                            JarvisCourier.ShowCourierDocumentsToolDefinition,
                                            JarvisCourier.CancelCourierVoucherToolDefinition,
                                            JarvisCourier.GetCourierVoucherDataToolDefinition,
                                            JarvisCourier.CreateCourierVoucherToolDefinition
                                        }
                                        : BuildRoutedTools(itemMode, traderMode, emailMode);

                    var requestBody = new
                    {
                        model = resolvedModel,
                        max_tokens = MaxTokens,
                        output_config = new { effort = "medium" },
                        system = new object[] {
                            new
                            {
                                type = "text",
                                text = systemPromptText,
                                cache_control = new { type = "ephemeral" }
                            }
                        },
                        tools = ToolsWithCacheBreakpoint(toolsForCall),
                        messages = history
                    };

                    string anthropicJson = JsonConvert.SerializeObject(requestBody);
                    DebugLog.Log($"[iter {iteration}] isLast={isLastIteration} REQUEST: {anthropicJson}");

                    // Explicit propagation: the logical Jarvis role was already
                    // selected above. Verilic receives that exact role and never
                    // has to infer it from prompts, tools or model names.
                    var proxyResp = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                        .SendAsync(xSupport, activeAgentName, anthropicJson, token);

                    DebugLog.Log($"[iter {iteration}] PROXY success={proxyResp.Success} " +
                        $"credits={proxyResp.CreditsExhausted} err={proxyResp.ErrorMessage} " +
                        $"usage={proxyResp.UsageInputTokens}/{proxyResp.UsageOutputTokens}");

                    if (!proxyResp.Success)
                    {
                        return proxyResp.CreditsExhausted
                            ? "✖ Το AI account αυτής της άδειας έχει εξαντλήσει τα credits του."
                            : "✖ " + (proxyResp.ErrorMessage ?? "Άγνωστο σφάλμα από τον Nexus.");
                    }

                    JObject anthropicResponse;
                    try
                    {
                        anthropicResponse = JObject.Parse(proxyResp.RawResponseJson);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log($"[iter {iteration}] RAW JSON PARSE FAILED: {ex.Message} | raw={proxyResp.RawResponseJson}");
                        return "✖ Άκυρη απάντηση από το AI: " + ex.Message;
                    }

                    string stopReason = anthropicResponse["stop_reason"]?.ToString();
                    JArray content = anthropicResponse["content"] as JArray ?? new JArray();

                    DebugLog.Log($"[iter {iteration}] RESPONSE stop_reason={stopReason} " +
                        $"content_types=[{string.Join(",", content.Select(b => (string)b["type"]))}]");
                    DebugLog.Log($"[iter {iteration}] RAW RESPONSE: {proxyResp.RawResponseJson}");

                    if (stopReason == "refusal")
                        return "Δεν μπορώ να απαντήσω σε αυτό το ερώτημα.";

                    history.Add(new JObject { ["role"] = "assistant", ["content"] = content });

                    if (stopReason == "max_tokens")
                    {
                        history.RemoveAt(history.Count - 1);
                        return "✖ Η απάντηση ήταν πολύ μεγάλη και έκοψε στη μέση " +
                            "(όριο μεγέθους). Δοκίμασε κάτι πιο σύντομο, ή ζήτα το " +
                            "σε μικρότερα κομμάτια (π.χ. λιγότερες εγγραφές ανά φορά).";
                    }

                    if (stopReason != "tool_use")
                    {
                        var textBlock = content.FirstOrDefault(b => (string)b["type"] == "text");
                        return textBlock?["text"]?.ToString() ?? "";
                    }

                    onProgress?.Invoke(BuildProgressCaption(content));

                    var toolResults = new JArray();
                    foreach (var block in content)
                    {
                        if ((string)block["type"] != "tool_use") continue;

                        string toolUseId = block["id"]?.ToString();
                        string toolName = block["name"]?.ToString();
                        JObject input = block["input"] as JObject ?? new JObject();

                        DebugLog.Log($"[iter {iteration}] TOOL_USE {toolName} input={input}");

                        string resultText;
                        bool isError = false;
                        try
                        {
                            resultText = await ExecuteTool(toolName, input, xSupport, onNavigate, onReadPage,
                                onExtractPageTables, onFilterEmailInbox, onFilterCalendar, onShowCalendarEntries,
                                onShowCourierDocuments, onShowContactResults, onExportShownTable);
                        }
                        catch (Exception ex)
                        {
                            resultText = "Σφάλμα: " + ex.Message;
                            isError = true;
                        }

                        DebugLog.Log($"[iter {iteration}] TOOL_RESULT {toolName} isError={isError} " +
                            $"result={Truncate(resultText, 2000)}");

                        var toolResult = new JObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = toolUseId,
                            ["content"] = resultText
                        };
                        if (isError) toolResult["is_error"] = true;
                        toolResults.Add(toolResult);
                    }

                    string resultCaption = BuildResultCaption(toolResults);
                    if (resultCaption != null) onProgress?.Invoke(resultCaption);

                    history.Add(new JObject { ["role"] = "user", ["content"] = toolResults });
                }

                DebugLog.Log($"[loop] MaxIterations ({maxIterations}) reached χωρίς end_turn.");
                return "✖ Έφτασα το όριο βημάτων χωρίς τελική απάντηση - δοκίμασε πιο συγκεκριμένη ερώτηση.";
            }
            catch (OperationCanceledException)
            {
                DebugLog.Log("[loop] Ακυρώθηκε από τον χρήστη (Stop).");
                return "⏹ Σταμάτησα. Θέλεις κάτι άλλο;";
            }
            finally
            {
                if (ReferenceEquals(_cts, myCts))
                    _cts = null;
                myCts.Dispose();
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…(κόπηκε)";

        private static string BuildProgressCaption(JArray content)
        {
            var thinkingBlock = content.FirstOrDefault(b => (string)b["type"] == "thinking");
            string thinkingText = thinkingBlock?["thinking"]?.ToString();

            if (!string.IsNullOrWhiteSpace(thinkingText))
                return Truncate(FirstSentence(thinkingText.Trim()), 140);

            var toolBlock = content.FirstOrDefault(b => (string)b["type"] == "tool_use");
            string toolName = toolBlock?["name"]?.ToString();
            switch (toolName)
            {
                case "query_data": return "Ψάχνει στη βάση δεδομένων…";
                case "export_query_to_file": return "Αποθηκεύει όλα τα αποτελέσματα σε αρχείο…";
                case "open_document": return "Ανοίγει το παραστατικό στο Soft1…";
                case "extract_page_tables": return "Διαβάζει πίνακες από τη σελίδα…";
                case "get_conversion_targets": return "Ψάχνει πιθανές σειρές μετασχηματισμού…";
                case "create_crm_task": return "Δημιουργεί εργασία CRM…";
                case "create_order": return "Καταχωρεί παραγγελία…";
                case "read_email": return "Διαβάζει τα email…";
                case "download_email_attachment": return "Κατεβάζει συνημμένο…";
                case "read_calendar": return "Διαβάζει το ημερολόγιο…";
                case "filter_email_inbox": return "Ενημερώνει τη λίστα email…";
                case "filter_calendar": return "Ενημερώνει το ημερολόγιο…";
                case "show_calendar_entries": return "Δείχνει το αποτέλεσμα στο ημερολόγιο…";
                case "send_email": return "Στέλνει το email…";
                case "reply_email": return "Στέλνει την απάντηση…";
                case "search_outlook_contacts": return "Ψάχνει τις επαφές Outlook…";
                case "show_contact_results": return "Δείχνει τα στοιχεία επαφής…";
                case "export_shown_table": return "Εξάγει τον πίνακα σε αρχείο…";
                case "create_outlook_event": return "Δημιουργεί το ραντεβού στο Outlook…";
                case "get_item_template": return "Μαζεύει στοιχεία πρότυπου είδους…";
                case "create_item": return "Δημιουργεί το είδος…";
                case "show_courier_documents": return "Δείχνει τα παραστατικά…";
                case "cancel_courier_voucher": return "Ακυρώνει την αποστολή…";
                case "get_courier_voucher_data": return "Μαζεύει στοιχεία αποστολέα/παραλήπτη/courier…";
                case "create_courier_voucher": return "Εκδίδει την αποστολή…";
                case "find_trader_by_afm": return "Ψάχνει συναλλασσόμενο με ΑΦΜ…";
                case "get_aade_data": return "Φέρνει στοιχεία από την ΑΑΔΕ…";
                case "create_trader_from_aade": return "Δημιουργεί συναλλασσόμενο…";
                default: return "Επεξεργάζεται…";
            }
        }

        private static string FirstSentence(string s)
        {
            int idx = s.IndexOfAny(new[] { '.', '\n' });
            return idx > 10 ? s.Substring(0, idx + 1).Trim() : s;
        }

        private static string BuildResultCaption(JArray toolResults)
        {
            var first = toolResults.FirstOrDefault();
            if (first == null) return null;

            bool isError = (bool?)first["is_error"] == true;
            if (isError) return "Σφάλμα στο ερώτημα - δοκιμάζει διαφορετική προσέγγιση…";

            string content = first["content"]?.ToString();
            if (string.IsNullOrEmpty(content)) return null;

            try
            {
                var payload = JObject.Parse(content);
                if (payload["rowsWritten"] != null)
                {
                    int rowsWritten = (int?)payload["rowsWritten"] ?? 0;
                    bool wasCapped = (bool?)payload["wasCapped"] ?? false;
                    return wasCapped
                        ? $"Αποθήκευσε τις πρώτες {rowsWritten} γραμμές σε αρχείο."
                        : $"Αποθήκευσε {rowsWritten} γραμμές σε αρχείο.";
                }

                int totalRowCount = (int?)payload["totalRowCount"] ?? -1;
                if (totalRowCount < 0) return null;
                return totalRowCount == 0
                    ? "Δεν βρήκε αποτελέσματα - ξαναψάχνει με άλλο κριτήριο…"
                    : $"Βρήκε {totalRowCount} εγγραφές.";
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> ExecuteTool(
            string name, JObject input, XSupport xSupport,
            Action<string> onNavigate, Func<Task<string>> onReadPage,
            Func<int?, Task<string>> onExtractPageTables = null,
            Action<string, string, string, JArray> onFilterEmailInbox = null,
            Action<string, string, string> onFilterCalendar = null,
            Action<string, JArray> onShowCalendarEntries = null,
            Action<JArray> onShowCourierDocuments = null,
            Action<JArray> onShowContactResults = null,
            Func<string, int[], Task<string>> onExportShownTable = null)
        {
            switch (name)
            {
                case "query_data": return JarvisTools.ExecuteQueryData(xSupport, input["sql"]?.ToString());
                case "export_query_to_file": return JarvisTools.ExecuteExportQueryToFile(xSupport, input);
                case "open_url": return JarvisTools.ExecuteOpenUrl(input["url"]?.ToString(), onNavigate);
                case "read_page_content": return await JarvisTools.ExecuteReadPageContent(xSupport, onReadPage);
                case "extract_page_tables": return await JarvisTools.ExecuteExtractPageTables(input, onExtractPageTables);
                case "open_document": return JarvisTools.ExecuteOpenDocument(xSupport, input);
                case "get_conversion_targets": return JarvisTools.ExecuteGetConversionTargets(xSupport, (int)input["findoc"]);
                case "create_crm_task": return JarvisTools.ExecuteCreateCrmTask(xSupport, input);
                case "create_order": return JarvisTools.ExecuteCreateOrder(xSupport, input);
                case "read_email": return await JarvisEmailAccess.ExecuteReadEmail(xSupport, input);
                case "download_email_attachment": return await JarvisEmailAccess.ExecuteDownloadEmailAttachment(xSupport, input);
                case "read_calendar": return await JarvisEmailAccess.ExecuteReadCalendar(xSupport, input);
                case "filter_email_inbox": return JarvisEmailAccess.ExecuteFilterEmailInbox(input, onFilterEmailInbox);
                case "filter_calendar": return JarvisEmailAccess.ExecuteFilterCalendar(input, onFilterCalendar);
                case "show_calendar_entries": return JarvisEmailAccess.ExecuteShowCalendarEntries(input, onShowCalendarEntries);
                case "send_email": return await JarvisEmailAccess.ExecuteSendEmail(xSupport, input);
                case "reply_email": return await JarvisEmailAccess.ExecuteReplyEmail(xSupport, input);
                case "search_outlook_contacts": return await JarvisEmailAccess.ExecuteSearchOutlookContacts(xSupport, input);
                case "show_contact_results": return JarvisTools.ExecuteShowContactResults(input, onShowContactResults);
                case "export_shown_table": return await JarvisTools.ExecuteExportShownTable(input, onExportShownTable);
                case "create_outlook_event": return await JarvisEmailAccess.ExecuteCreateOutlookEvent(xSupport, input);
                case "get_item_template": return JarvisItems.ExecuteGetItemTemplate(xSupport, input);
                case "create_item": return JarvisItems.ExecuteCreateItem(xSupport, input);
                case "show_courier_documents": return JarvisCourier.ExecuteShowCourierDocuments(input, onShowCourierDocuments);
                case "cancel_courier_voucher": return await JarvisCourier.ExecuteCancelCourierVoucherChatAsync(xSupport, input);
                case "get_courier_voucher_data": return JarvisCourier.ExecuteGetCourierVoucherData(xSupport, input);
                case "create_courier_voucher": return await JarvisCourier.ExecuteCreateCourierVoucherChatAsync(xSupport, input);
                case "find_trader_by_afm":
                case "get_aade_data":
                case "create_trader_from_aade":
                {
                    var access = await Task.Run(
                        () => JarvisLicenseGuard.CheckAccessSilent(xSupport, AccessConfig.DocReaderToolName));
                    if (!access.Allowed)
                        return JsonConvert.SerializeObject(new
                        { success = false, found = false, error = "Δεν υπάρχει άδεια χρήσης αυτής της λειτουργίας (JARVISDOCREADER)." });

                    switch (name)
                    {
                        case "find_trader_by_afm":
                            return JarvisTools.ExecuteFindTraderByAfmTool(xSupport, input);
                        case "get_aade_data":
                            return JarvisTools.ExecuteGetAadeData(
                                xSupport, input["afm"]?.ToString(), (int?)input["sodType"] ?? 12);
                        default:
                            return JarvisTools.ExecuteCreateTraderFromAade(xSupport, input);
                    }
                }
                default:
                    throw new Exception($"Άγνωστο tool: {name}");
            }
        }

        private static object ToolsWithCacheBreakpoint(object[] tools)
        {
            if (tools == null || tools.Length == 0) return tools;
            JArray arr = JArray.FromObject(tools);
            if (arr.Last is JObject lastTool)
                lastTool["cache_control"] = new JObject { ["type"] = "ephemeral" };
            return arr;
        }

        private struct RoutingDecision
        {
            public bool Item;
            public bool Trader;
            public bool Email;
            public string StickyLabel;
        }

        private static RoutingDecision RouteMainChatAgent(string userText, string routingHint)
        {
            string t = NormalizeGreek(userText);

            string[] createVerbs = {
                "ανοιγ", "ανοιξ", "δημιουργ", "φτιαξ", "φτιαχν",
                "καταχωρ", "εισαγ", "εισηγαγ",
                "νεο", "νεα", "νεος"
            };
            bool createVerbHit = ContainsAny(t, createVerbs);

            bool itemNounHit = ContainsAny(t, new[] { "ειδος", "ειδη", "ειδων", "ειδους" });
            bool itemHit = (createVerbHit && itemNounHit)
                || t.Contains("mtrl") || t.Contains(NormalizeGreek("τιμοκατάλογ")) || t.Contains("bulk import");

            bool traderNounHit = t.Contains(NormalizeGreek("πελάτ")) || t.Contains(NormalizeGreek("προμηθευτ"))
                || t.Contains(NormalizeGreek("συναλλασσόμεν"));
            bool traderHit = (createVerbHit && traderNounHit) || t.Contains("αφμ");

            string[] emailVerbStems = { "στειλ", "στελν", "απαντ", "γραψ", "γραφ" };
            bool emailVerbHit = ContainsAny(t, emailVerbStems);
            bool emailNounHit = t.Contains("email") || t.Contains("mail");
            string[] emailReadVerbStems = {
                "βρες", "βρω", "βρεις", "βρει", "ψαξ", "ψαχν",
                "διαβασ", "διαβαζ", "δες", "δεις", "κοιτ", "ελεγξ", "ελεγχ"
            };
            bool emailReadHit = ContainsAny(t, emailReadVerbStems)
                && (t.Contains("email") || t.Contains("mail") || t.Contains(NormalizeGreek("εισερχόμενα")));
            bool calendarHit = t.Contains(NormalizeGreek("ραντεβού")) || t.Contains(NormalizeGreek("υπενθύμιση"));
            bool contactSearchHit = ContainsAny(t, new[] { "βρες", "βρω", "βρεις", "βρει", "αναζητ" })
                && t.Contains(NormalizeGreek("επαφ"));
            bool emailHit = (emailVerbHit && emailNounHit) || emailReadHit || calendarHit || contactSearchHit;

            int hitCount = (itemHit ? 1 : 0) + (traderHit ? 1 : 0) + (emailHit ? 1 : 0);

            if (hitCount >= 1)
            {
                string sticky = hitCount == 1
                    ? (itemHit ? "item" : traderHit ? "trader" : "email")
                    : "general";
                return new RoutingDecision { Item = itemHit, Trader = traderHit, Email = emailHit, StickyLabel = sticky };
            }

            string fallback = !string.IsNullOrEmpty(routingHint) ? routingHint : "general";
            return new RoutingDecision
            {
                Item = fallback == "item",
                Trader = fallback == "trader",
                Email = fallback == "email",
                StickyLabel = fallback
            };
        }

        private static object[] BuildRoutedTools(bool itemMode, bool traderMode, bool emailMode)
        {
            var list = new List<object>
            {
                JarvisTools.QueryDataToolDefinition,
                JarvisTools.ExportQueryToFileToolDefinition,
                JarvisTools.OpenDocumentToolDefinition,
                JarvisTools.GetConversionTargetsToolDefinition,
                JarvisTools.CreateCrmTaskToolDefinition,
                JarvisTools.CreateOrderToolDefinition,
                JarvisEmailAccess.SendEmailToolDefinition,
                JarvisEmailAccess.ReplyEmailToolDefinition,
                JarvisTools.ShowContactResultsToolDefinition,
                JarvisEmailAccess.SearchOutlookContactsToolDefinition,
                JarvisTools.ExportShownTableToolDefinition
            };
            if (itemMode)
            {
                list.Add(JarvisItems.GetItemTemplateToolDefinition);
                list.Add(JarvisItems.CreateItemToolDefinition);
            }
            if (traderMode)
            {
                list.Add(JarvisTools.FindTraderByAfmToolDefinition);
                list.Add(JarvisTools.GetAadeDataToolDefinition);
                list.Add(JarvisTools.CreateTraderFromAadeToolDefinition);
            }
            if (emailMode)
            {
                list.Add(JarvisEmailAccess.ReadEmailToolDefinition);
                list.Add(JarvisEmailAccess.DownloadEmailAttachmentToolDefinition);
                list.Add(JarvisEmailAccess.ReadCalendarToolDefinition);
                list.Add(JarvisEmailAccess.CreateOutlookEventToolDefinition);
                list.Add(JarvisEmailAccess.FilterEmailInboxToolDefinition);
            }
            return list.ToArray();
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (string needle in needles)
                if (haystack.Contains(NormalizeGreek(needle))) return true;
            return false;
        }

        private static string NormalizeGreek(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.ToLowerInvariant();

            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                char r;
                switch (c)
                {
                    case 'ά': r = 'α'; break;
                    case 'έ': r = 'ε'; break;
                    case 'ή': r = 'η'; break;
                    case 'ί': case 'ϊ': case 'ΐ': r = 'ι'; break;
                    case 'ό': r = 'ο'; break;
                    case 'ύ': case 'ϋ': case 'ΰ': r = 'υ'; break;
                    case 'ώ': r = 'ω'; break;
                    default: r = c; break;
                }
                sb.Append(r);
            }
            s = sb.ToString();

            const string OuMarker = "\u0001";
            s = s.Replace("ου", OuMarker)
                 .Replace("αι", "ε")
                 .Replace("ει", "ι")
                 .Replace("οι", "ι")
                 .Replace("υι", "ι")
                 .Replace('η', 'ι')
                 .Replace('υ', 'ι')
                 .Replace('ω', 'ο')
                 .Replace(OuMarker, "ου");

            return s;
        }

        private static string ResolveAgentModel(XSupport xSupport, string agentName)
        {
            int paramCode;
            switch (agentName)
            {
                case "Forge": paramCode = 500030; break;
                case "Compass": paramCode = 500031; break;
                case "Echo": paramCode = 500032; break;
                case "Sprint": paramCode = 500033; break;
                case "Scout": paramCode = 500034; break;
                case "Sage": paramCode = 500035; break;
                default: paramCode = 500029; break;
            }
            string overrideModel = JarvisTools.GetOptionalParamString(xSupport, paramCode);
            return string.IsNullOrWhiteSpace(overrideModel) ? Model : overrideModel;
        }

        private string BuildSystemPrompt(
            XSupport xSupport, bool forceFinalAnswer = false, bool helpMode = false,
            int reportDecimalPlaces = 2, bool browserMode = false, bool emailMode = false,
            bool courierMode = false, string extraInstructions = null,
            bool itemMode = false, bool traderMode = false, string currentUserName = null)
        {
            var info = xSupport.ConnectionInfo;
            string prompt =
                "Είσαι ο Jarvis, ο ψηφιακός βοηθός μέσα στο Soft1 της Jetoil " +
                "(εταιρία διανομής καυσίμων/πετρελαιοειδών). Απαντάς στα " +
                "ελληνικά, σύντομα και συγκεκριμένα.\n\n" +
                "Έχεις πρόσβαση στη βάση δεδομένων του Soft1 (SQL Server) " +
                "μέσω του εργαλείου query_data (μόνο SELECT). Χρησιμοποίησέ " +
                "το για οποιαδήποτε ερώτηση αφορά δεδομένα. Μη μαντεύεις " +
                "ονόματα πινάκων/στηλών που ΔΕΝ αναφέρονται παρακάτω - αν δεν " +
                "είσαι σίγουρος, ρώτα πρώτα με ένα ερώτημα στο " +
                "INFORMATION_SCHEMA.COLUMNS.\n\n" +
                "ΓΝΩΣΤΟ SCHEMA (χρησιμοποίησέ το ΚΑΤΕΥΘΕΙΑΝ, μην το " +
                "ξανα-ανακαλύπτεις με INFORMATION_SCHEMA - χάνεις iterations " +
                "άδικα):\n" +
                "- Στοιχεία λογαριασμού/συναλλασσόμενου: πίνακας TRDR " +
                "(TRDR=id, CODE, NAME, AFM, SODTYPE: 12=Προμηθευτής, " +
                "13=Πελάτης).\n" +
                "- Κινήσεις/παραστατικά λογαριασμού: πίνακας FINDOC " +
                "(FINDOC=δικό του id/πρωτεύον κλειδί, ίδια σύμβαση με TRDR, " +
                "TRDR=id συναλλασσόμενου, TRNDATE, FINCODE=κωδικός " +
                "παραστατικού, SUMAMNT=ποσό, SERIES/SOSOURCE/COMPANY). Για " +
                "το όνομα του τύπου παραστατικού (π.χ. \"Τιμολόγιο\", " +
                "\"Είσπραξη\"): JOIN SERIES ON SERIES.COMPANY=FINDOC.COMPANY " +
                "AND SERIES.SERIES=FINDOC.SERIES AND " +
                "SERIES.SOSOURCE=FINDOC.SOSOURCE. ΔΕΝ υπάρχει πίνακας " +
                "\"FINTRD\" - μην τον δοκιμάζεις.\n" +
                "- Αντιστοίχιση SOSOURCE -> κύκλωμα: 1351=Πωλήσεις/Τιμολόγια, " +
                "1353=Παροχή Υπηρεσιών, 1251=Παραλαβή/ΔΑ Προμηθευτή, " +
                "1253=Παροχή Υπηρεσιών (αγορές), 5151=Ενδοδιακίνηση/Παραγωγή, " +
                "1412=Έμβασμα σε προμηθευτή, 1413=Έμβασμα από πελάτη, " +
                "2021=Εργασία CRM.\n" +
                "- Προοδευτικά υπόλοιπα: TRDBALSHEET (TRDR, FISCPRD, LDEBIT, LCREDIT).\n" +
                "- Χρήστες Soft1: USERS (USERS=id, NAME).\n\n" +
                "ΑΠΟΦΑΣΙΣΤΙΚΟΤΗΤΑ: μόλις έχεις αρκετά δεδομένα από τα tool " +
                "results για να απαντήσεις, ΣΤΑΜΑΤΑ τα queries και γράψε την " +
                "απάντηση αμέσως.\n\n" +
                "ΔΙΕΥΚΡΙΝΙΣΤΙΚΕΣ ΕΡΩΤΗΣΕΙΣ: όταν χρειάζεται επιλογή, χρησιμοποίησε:\n" +
                "❓ <σύντομη ερώτηση>\n> <επιλογή 1>\n> <επιλογή 2>\n" +
                "και μην καλείς tool στο ίδιο μήνυμα.\n\n" +
                $"Μορφοποίηση: χρησιμοποίησε ΑΚΡΙΒΩΣ {reportDecimalPlaces} δεκαδικά " +
                "σε αριθμητικές τιμές και Markdown tables για tabular δεδομένα.\n";

            if (traderMode)
            {
                prompt +=
                    "\n- ΑΝΟΙΓΜΑ/ΔΗΜΙΟΥΡΓΙΑ ΣΥΝΑΛΛΑΣΣΟΜΕΝΟΥ ΜΕ ΑΦΜ: " +
                    "χρησιμοποίησε find_trader_by_afm, get_aade_data και μόνο μετά από " +
                    "ρητή επιβεβαίωση σε επόμενο turn create_trader_from_aade.\n";
            }

            if (itemMode)
            {
                prompt +=
                    "\n- ΑΝΟΙΓΜΑ/ΔΗΜΙΟΥΡΓΙΑ ΕΙΔΟΥΣ: χρησιμοποίησε get_item_template, " +
                    "μάζεψε τα απαραίτητα πεδία, δείξε πλήρες draft και μόνο μετά από " +
                    "ρητή επιβεβαίωση σε επόμενο turn κάλεσε create_item.\n";
            }

            string currentUserLine = string.IsNullOrWhiteSpace(currentUserName)
                ? $", ΤρέχωνΧρήστης=UserId {info.UserId} (όνομα άγνωστο)"
                : $", ΤρέχωνΧρήστης={currentUserName} (UserId={info.UserId})";
            prompt +=
                "\nΤρέχον context: Company=" + info.CompanyId + ", Branch=" + info.BranchId +
                currentUserLine + ".";

            if (helpMode)
                prompt += "\n\n🆘 HELP MODE: βοήθησε τον χειριστή με συγκεκριμένο πρόβλημα και χρησιμοποίησε το quick-reply format όπου χρειάζεται.";

            if (browserMode)
                prompt += "\n\n🌐 BROWSER MODE: χρησιμοποίησε open_url/read_page_content/extract_page_tables όταν ζητείται περιήγηση ή scraping.";

            if (emailMode)
                prompt += "\n\n📧 EMAIL MODE: χρησιμοποίησε τα email/calendar tools, με ρητή επιβεβαίωση πριν από πραγματική αποστολή email ή πρόσκληση.";

            if (courierMode)
                prompt += "\n\n📦 COURIER MODE: χρησιμοποίησε τα courier tools και απαίτησε ρητή επιβεβαίωση πριν από έκδοση ή ακύρωση voucher.";

            if (forceFinalAnswer)
                prompt += "\n\n⚠️ ΤΕΛΕΥΤΑΙΟ ΔΙΑΘΕΣΙΜΟ ΒΗΜΑ: απάντησε τώρα με ό,τι έχεις ήδη συλλέξει.";

            if (!string.IsNullOrWhiteSpace(extraInstructions))
            {
                prompt +=
                    "\n\n📋 ΠΡΟΣΘΕΤΕΣ ΟΔΗΓΙΕΣ ΑΠΟ ΤΟΝ ΔΙΑΧΕΙΡΙΣΤΗ " +
                    "(συμπληρωματικές, δεν ακυρώνουν κανόνες ασφαλείας):\n" + extraInstructions;
            }

            return prompt;
        }

        public async Task<JObject> DetectDocumentIssuerAsync(
            string agentAccountRef, string base64, string mimeType)
        {
            bool isPdf = mimeType == "application/pdf";
            bool isImage = mimeType != null &&
                mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            if (!isPdf && !isImage)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["errorMessage"] = "Μη υποστηριζόμενος τύπος αρχείου για AI ανάγνωση ακόμα (μόνο PDF/εικόνα)."
                };
            }

            string prompt =
                "Κοίτα αυτό το παραστατικό και βρες ΜΟΝΟ το ΑΦΜ του ΕΚΔΟΤΗ.\n" +
                "Επέστρεψε ΜΟΝΟ JSON με issuer_afm, issuer_name, doc_type, doc_number, doc_date, confidence.";

            var requestBody = new
            {
                model = "claude-haiku-4-5",
                max_tokens = 1024,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = isPdf ? "document" : "image",
                                  source = new { type = "base64", media_type = mimeType, data = base64 } },
                            new { type = "text", text = prompt }
                        }
                    }
                }
            };

            string anthropicJson = JsonConvert.SerializeObject(requestBody);
            var proxyResp = await CallProxyAsync(
                new AgentProxyRequest { AgentAccountRef = agentAccountRef, AnthropicRequestJson = anthropicJson },
                CancellationToken.None);

            if (!proxyResp.Success)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["errorMessage"] = proxyResp.CreditsExhausted
                        ? "Το AI account αυτής της άδειας έχει εξαντλήσει τα credits του."
                        : (proxyResp.ErrorMessage ?? "Άγνωστο σφάλμα από τον Nexus.")
                };
            }

            try
            {
                var anthropicResponse = JObject.Parse(proxyResp.RawResponseJson);
                JArray content = anthropicResponse["content"] as JArray ?? new JArray();
                string text = content.FirstOrDefault(b => (string)b["type"] == "text")?["text"]?.ToString() ?? "";
                string clean = CleanJsonBlock(text);
                var obj = JObject.Parse(clean);
                string afm = NormalizeAfm(obj["issuer_afm"]?.ToString() ?? "");

                return new JObject
                {
                    ["success"] = !string.IsNullOrEmpty(afm),
                    ["issuerAfm"] = afm,
                    ["issuerName"] = obj["issuer_name"]?.ToString() ?? "",
                    ["docType"] = obj["doc_type"]?.ToString() ?? "",
                    ["docNumber"] = obj["doc_number"]?.ToString() ?? "",
                    ["docDate"] = obj["doc_date"]?.ToString() ?? "",
                    ["confidence"] = obj["confidence"]?.ToObject<double>() ?? 0
                };
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] DetectDocumentIssuerAsync parse EXCEPTION: " + ex + " | raw=" + proxyResp.RawResponseJson);
                return new JObject { ["success"] = false, ["errorMessage"] = "Σφάλμα ανάγνωσης απάντησης AI: " + ex.Message };
            }
        }

        public async Task<JObject> ExtractDocumentLinesAsync(
            string agentAccountRef, string base64, string mimeType, string companyAfm)
        {
            bool isPdf = mimeType == "application/pdf";
            bool isImage = mimeType != null &&
                mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            if (!isPdf && !isImage)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["errorMessage"] = "Μη υποστηριζόμενος τύπος αρχείου για AI ανάγνωση ακόμα (μόνο PDF/εικόνα)."
                };
            }

            string companyRule = string.IsNullOrWhiteSpace(companyAfm) ? "" :
                $"\nΚΡΙΣΙΜΟ: Το ΑΦΜ της δικής μας εταιρίας είναι {companyAfm}.\n";

            string prompt =
                "Διάβασε ΠΡΟΣΕΚΤΙΚΑ αυτό το ελληνικό παραστατικό και εξήγαγε issuer, document_info, line_items, totals, aade_link, remarks, confidence σε JSON." + companyRule;

            var requestBody = new
            {
                model = Model,
                max_tokens = 4000,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = isPdf ? "document" : "image",
                                  source = new { type = "base64", media_type = mimeType, data = base64 } },
                            new { type = "text", text = prompt }
                        }
                    }
                }
            };

            string anthropicJson = JsonConvert.SerializeObject(requestBody);
            var proxyResp = await CallProxyAsync(
                new AgentProxyRequest { AgentAccountRef = agentAccountRef, AnthropicRequestJson = anthropicJson },
                CancellationToken.None);

            if (!proxyResp.Success)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["errorMessage"] = proxyResp.CreditsExhausted
                        ? "Το AI account αυτής της άδειας έχει εξαντλήσει τα credits του."
                        : (proxyResp.ErrorMessage ?? "Άγνωστο σφάλμα από τον Nexus.")
                };
            }

            try
            {
                var anthropicResponse = JObject.Parse(proxyResp.RawResponseJson);
                JArray content = anthropicResponse["content"] as JArray ?? new JArray();
                string text = content.FirstOrDefault(b => (string)b["type"] == "text")?["text"]?.ToString() ?? "";
                string clean = CleanJsonBlock(text);
                var obj = JObject.Parse(clean);
                obj["success"] = true;
                return obj;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] ExtractDocumentLinesAsync parse EXCEPTION: " + ex + " | raw=" + proxyResp.RawResponseJson);
                return new JObject { ["success"] = false, ["errorMessage"] = "Σφάλμα ανάγνωσης απάντησης AI: " + ex.Message };
            }
        }

        private static string NormalizeAfm(string afm)
        {
            if (string.IsNullOrEmpty(afm)) return afm;
            afm = afm.Trim().ToUpperInvariant();
            if (afm.StartsWith("EL")) afm = afm.Substring(2);
            if (afm.StartsWith("GR")) afm = afm.Substring(2);
            return afm;
        }

        private static string CleanJsonBlock(string text)
        {
            string clean = (text ?? "").Trim();
            if (clean.StartsWith("```"))
            {
                int start = clean.IndexOf('\n') + 1;
                int end = clean.LastIndexOf("```");
                if (end > start) clean = clean.Substring(start, end - start).Trim();
            }
            int firstBrace = clean.IndexOf('{');
            int lastBrace = clean.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                clean = clean.Substring(firstBrace, lastBrace - firstBrace + 1);
            return clean;
        }

        private async Task<AgentProxyResponse> CallProxyAsync(
            AgentProxyRequest request, CancellationToken token)
        {
            string url = AccessConfig.ServiceUrl.TrimEnd('/') + "/agent/vision";
            string body = JsonConvert.SerializeObject(request);

            using (var msg = new HttpRequestMessage(HttpMethod.Post, url))
            {
                msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(AccessConfig.ClientKey))
                    msg.Headers.Add("X-Client-Key", AccessConfig.ClientKey);

                using (var resp = await _http.SendAsync(msg, token))
                {
                    string json = await resp.Content.ReadAsStringAsync();

                    if (!resp.IsSuccessStatusCode)
                        return new AgentProxyResponse
                        {
                            Success = false,
                            ErrorMessage = $"Ο διακομιστής απάντησε με σφάλμα ({(int)resp.StatusCode})."
                        };

                    var result = JsonConvert.DeserializeObject<AgentProxyResponse>(json);
                    return result ?? new AgentProxyResponse
                    {
                        Success = false,
                        ErrorMessage = "Άκυρη απάντηση διακομιστή."
                    };
                }
            }
        }
    }
}
