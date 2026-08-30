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
        // όταν το active model χρησιμοποιήσει filter_email_inbox/filter_calendar
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
            // ΝΕΟ 18/08, ρητό αίτημα χρήστη ("scraping δεδομένων από
            // ιστοσελίδες") - extract_page_tables tool, ΙΔΙΟ browserMode
            // scope με onReadPage πιο πάνω.
            Func<int?, Task<string>> onExtractPageTables = null,
            bool emailMode = false,
            Action<string, string, string, JArray> onFilterEmailInbox = null,
            Action<string, string, string> onFilterCalendar = null,
            Action<string, JArray> onShowCalendarEntries = null,
            // courierMode/onShowCourierDocuments - ΝΕΟ 17/08, ρητό αίτημα
            // χρήστη ("JARVISCOURIER" - φέρνει τον S1Courier μέσα στον
            // Jarvis). Ίδιο idiom με emailMode/onShowCalendarEntries -
            // ξεχωριστό conversation history (JarvisShell.
            // _courierConversation), ξεχωριστό tool subset (query_data +
            // show_courier_documents), αποτέλεσμα ΠΑΝΤΑ στο κύριο παράθυρο
            // της κουρτίνας, ΟΧΙ στο chat.
            bool courierMode = false,
            Action<JArray> onShowCourierDocuments = null,
            // show_contact_results - ΝΕΟ 18/08, ρητό αίτημα χρήστη
            // ("να φτιάξουμε μια εντολή ... θα του επιστρέφει σε modal τα
            // στοιχεία της επαφής"). Διαθέσιμο σε general/browserMode/
            // emailMode (ΙΔΙΕΣ branches με send_email/reply_email) - ΙΔΙΟ
            // idiom "Claude υπολογίζει, το tool ΜΕΤΑΦΕΡΕΙ" με
            // onShowCourierDocuments/onShowCalendarEntries πιο πάνω.
            Action<JArray> onShowContactResults = null,
            // export_shown_table - ΝΕΟ 19/08, ρητό αίτημα χρήστη ("το
            // κουμπί PDF/CSV/Excel πρέπει να είναι οδηγία για τον agent,
            // όχι απλά κουμπί"). ΙΔΙΟ idiom "Claude υπολογίζει, το tool
            // ΜΕΤΑΦΕΡΕΙ" με onShowContactResults πιο πάνω - ΑΛΛΑ ΔΙΟΡΘΩΘΗΚΕ
            // 19/08 (ζωντανή διευκρίνιση χρήστη - "σε εκείνο το σημείο
            // έχει φτιάξει το αρχείο και ξέρει και σε ποιο path"):
            // Func<string,Task<string>> (ΟΧΙ Action<string>) - ΠΡΑΓΜΑΤΙΚΟ
            // round-trip, περιμένει να ολοκληρωθεί η εγγραφή στο δίσκο
            // (window.triggerTableExport, index.html) ΚΑΙ επιστρέφει το
            // πραγματικό path - το tool_result έχει το path ώστε ο
            // Jarvis να μπορεί ΜΕΤΑ να το επισυνάψει σε send_email
            // (attachmentFilePath). v1: ΜΟΝΟ κύριο chat.
            Func<string, int[], Task<string>> onExportShownTable = null,
            // ΝΕΟ 19/08, agent-clustering restructuring (ζωντανό review
            // χρήστη - latency): ΜΟΝΟ για το ελεύθερο κύριο chat (καμία
            // από τις άλλες κουρτίνες - helpMode/browserMode/emailMode/
            // courierMode - δεν χρειάζεται routing, ΗΔΗ ξέρουν το mode
            // τους ρητά). routingHint = το mode που διάλεξε ο router στο
            // ΠΡΟΗΓΟΥΜΕΝΟ turn της ΙΔΙΑΣ συζήτησης ("item"/"trader"/
            // "email"/"general"/null) - "sticky" fallback όταν το τρέχον
            // μήνυμα δεν έχει σαφές keyword-σήμα (π.χ. "ναι, κάνε το" σε
            // συνέχεια). onModeChosen: callback ΠΡΟΣ τον caller
            // (JarvisShell) με το mode που ΤΕΛΙΚΑ διαλέχθηκε αυτό το turn,
            // ώστε να αποθηκευτεί σαν το επόμενο routingHint. Βλ.
            // RouteMainChatAgent πιο κάτω.
            string routingHint = null,
            Action<string> onModeChosen = null,
            // ΝΕΟ 18/08, ρητό αίτημα χρήστη ("bulk import ειδών από
            // αρχείο/Browser") - προαιρετικό override του σταθερού
            // MaxIterations. ΧΩΡΙΣ κόστος για κανονικές συζητήσεις (το
            // loop ΗΔΗ σταματάει νωρίς μόλις το stop_reason δεν είναι
            // πια "tool_use" - το όριο είναι ΜΟΝΟ οροφή ασφαλείας, ΔΕΝ
            // "καταναλώνεται" ποτέ σε κανονική χρήση) - μόνο τα
            // σενάρια που ΠΡΑΓΜΑΤΙΚΑ χρειάζονται πολλά tool calls στη
            // σειρά (π.χ. δημιουργία 50 ειδών από τιμοκατάλογο) το
            // εκμεταλλεύονται. Default = ίδιο ΑΚΡΙΒΩΣ με πριν.
            int maxIterations = MaxIterations)
        {
            JToken userContent;
            if (!string.IsNullOrEmpty(attachmentBase64))
            {
                // Το Anthropic API δέχεται "image" μόνο για πραγματικές
                // εικόνες (jpeg/png/gif/webp) και "document" ΜΟΝΟ για PDF -
                // ένα .xlsx/.xls (raw Excel binary) δεν είναι κανένα από τα
                // δύο. Αντί να στείλουμε λάθος media_type (σκάει με 400),
                // αποτυγχάνουμε νωρίς με φιλικό μήνυμα.
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
                // ΣΗΜΑΝΤΙΚΟ: το Anthropic API απορρίπτει με 400 ένα κενό
                // text content block ({"type":"text","text":""}) - αυτό
                // ακριβώς συνέβαινε όταν ο χρήστης έστελνε ΜΟΝΟ εικόνα,
                // χωρίς λεζάντα. Το text block μπαίνει ΜΟΝΟ αν υπάρχει
                // πραγματικό κείμενο.
                if (!string.IsNullOrWhiteSpace(userText))
                {
                    contentArray.Add(new JObject { ["type"] = "text", ["text"] = userText });
                }
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
            // Τοπική αναφορά στο ΔΙΚΟ ΜΑΣ token source - βλ. finally παρακάτω.
            // Χρειάζεται τώρα που το dashboard επιτρέπει "override" (refresh
            // ενώ φορτώνει ήδη κάτι, βλ. index.html requestDashboardData):
            // αν δύο AskAsync κλήσεις επικαλυφθούν (η παλιά ακυρώνεται, η
            // νέα ξεκινάει πριν προλάβει να τελειώσει η παλιά), χωρίς αυτόν
            // τον έλεγχο η παλιά θα έκανε Dispose()/null το _cts της ΝΕΑΣ
            // στο δικό της finally, "τυφλώνοντας" ένα επόμενο Stop.
            var myCts = _cts;

            // Διαβάζεται ΜΙΑ φορά πριν το loop (ΟΧΙ ανά iteration μέσα στο
            // BuildSystemPrompt - θα ήταν ένα άσκοπο SQL round-trip x14 σε
            // ένα βαρύ ερώτημα). ParamCode 500009 - βλ. JarvisTools.
            // GetReportDecimalPlaces, ασφαλές default αν λείπει.
            int reportDecimalPlaces = JarvisTools.GetReportDecimalPlaces(xSupport);
            // ΔΙΟΡΘΩΘΗΚΕ 19/08 - ΙΔΙΟ σκεπτικό, ΜΙΑ φορά εδώ (ΟΧΙ ανά
            // iteration μέσα στο BuildSystemPrompt - ήταν πραγματικό bug,
            // βλ. session review 19/08).
            string extraInstructions = JarvisTools.GetOptionalParamString(xSupport, 500027);
            // ΝΕΟ 19/08, ζωντανό bug report χρήστη ("δεν είναι αποδεκτό
            // δεν γίνεται να μην καταλαβαίνει ποιος είναι ο User που του
            // μιλάει ενώ στην αρχή του session τον έχει χαιρετίσει με το
            // όνομά του"): το greeting ήταν ΚΑΘΑΡΑ cosmetic UI text, ΠΟΤΕ
            // δεν έφτανε στον Jarvis - τώρα περνάει στο "Τρέχον context"
            // (ΠΑΝΤΑ unconditional, βλ. BuildSystemPrompt) ώστε
            // "ανάθεσε σε μένα"/"βάλε σε εμένα" να λύνεται ΑΠΕΥΘΕΙΑΣ,
            // ΧΩΡΙΣ να ρωτάει ποιος είναι ο χειριστής.
            string currentUserName = JarvisTools.GetCurrentUserDisplayName(xSupport);

            // ΝΕΟ 19/08, agent-clustering restructuring: router ΜΟΝΟ για
            // το ελεύθερο κύριο chat - καμία κουρτίνα (help/browser/email/
            // courier) δεν περνάει από εδώ, ΗΔΗ ξέρει το mode της. v1:
            // απλό keyword heuristic (ΧΩΡΙΣ επιπλέον LLM call - θα ακύρωνε
            // ΑΚΡΙΒΩΣ το latency κέρδος που ψάχνουμε), με "sticky" fallback
            // στο routingHint όταν δεν υπάρχει σαφές σήμα (π.χ. σύντομη
            // απάντηση συνέχειας μέσα στην ΙΔΙΑ ροή).
            bool itemMode = false;
            bool traderMode = false;
            // ΔΙΟΡΘΩΘΗΚΕ 19/08: captured ΠΡΙΝ ο router (πιο κάτω) ενδεχομένως
            // ξαναγράψει το τοπικό emailMode=true - διακρίνει πραγματική
            // Email κουρτίνα (caller πέρασε emailMode=true ρητά, ΕΧΕΙ τα
            // onFilterEmailInbox/onFilterCalendar/onShowCalendarEntries
            // callbacks) από routed "Echo" στο γενικό chat (ΔΕΝ τα έχει) -
            // βλ. σχόλιο στο tools ternary πιο κάτω.
            bool isEmailCurtain = emailMode;
            if (!helpMode && !browserMode && !emailMode && !courierMode)
            {
                // ΔΙΟΡΘΩΘΗΚΕ 19/08 - γενίκευση μετά από ζωντανό feedback
                // χρήστη ("πρέπει να έχει μια λίστα ανά agent με τα skill
                // set που έχει ο καθένας... και όταν δεν μπορεί να
                // αποφασίσει ο ίδιος να δίνει όλα τα παρεμφερή skills των
                // agents ως επιλογές"): ΠΡΙΝ, το RouteMainChatAgent
                // επέστρεφε ΕΝΑ όνομα - αν 2+ domains ταυτόχρονα έδειχναν
                // σχετικά (π.χ. "άνοιξε πελάτη Χ ΚΑΙ ένα νέο είδος Υ"),
                // ΟΛΑ χάνονταν, έπεφτε σε γενικό fallback ΧΩΡΙΣ κανένα
                // από τα δύο tool sets. Τώρα επιστρέφει ΤΑ ΤΡΙΑ flags
                // απευθείας (Item/Trader/Email) - μπορούν να είναι
                // ΠΕΡΙΣΣΟΤΕΡΑ ΑΠΟ ΕΝΑ ταυτόχρονα ΤΑΥΤΟΧΡΟΝΑ (ένωση/union),
                // ΟΧΙ αποκλειστική επιλογή. Το StickyLabel είναι ό,τι
                // αποθηκεύεται για το επόμενο turn (routingHint).
                RoutingDecision routed = RouteMainChatAgent(userText, routingHint);
                onModeChosen?.Invoke(routed.StickyLabel);
                itemMode = routed.Item;
                traderMode = routed.Trader;
                emailMode = routed.Email;
            }

            // ΝΕΟ 19/08 (συνέχεια review χρήστη - "δεν έχουμε αποφασίσει
            // μοντέλα ανά agent και τον μηχανισμό που θα αλλάζει τα
            // μοντέλα"): κάθε agent domain μπορεί ΤΩΡΑ να έχει το ΔΙΚΟ
            // του μοντέλο μέσω ξεχωριστού cccParams (ίδιο idiom με
            // extraInstructions/500027) - admin-only, ΟΧΙ στο UI του
            // χειριστή (ρητή απόφαση, βλ. session notes). ΣΥΝΤΗΡΗΤΙΚΗ
            // αφετηρία (ρητή επιλογή χρήστη): ΟΛΑ default σε Opus 5 -
            // ΚΑΜΙΑ αλλαγή συμπεριφοράς σήμερα, tuning ανά domain
            // ΑΡΓΟΤΕΡΑ ΜΟΝΟ μέσω params, μετά από ζωντανή δοκιμή
            // ποιότητας. ΣΗΜΕΙΩΣΗ χρήστη (καταγεγραμμένη, ΟΧΙ λυμένη
            // ακόμα): το cccParams είναι per-company, ΟΧΙ per-Soft1-user
            // - αν χρειαστεί ποτέ tuning ΑΝΑ χρήστη, θα χρειαστεί
            // διαφορετικός μηχανισμός (deferred, βλ. README). Codex (DR)
            // ΔΕΝ περνάει από εδώ - παραμένει hardcoded Haiku/Opus όπως
            // ήδη ήταν (ήδη σωστά tuned, standalone μέθοδοι χωρίς
            // xSupport διαθέσιμο σήμερα - βλ. README για πιθανή μελλοντική
            // επέκταση).
            string activeAgentName = helpMode ? "Sage"
                : browserMode ? "Scout"
                : courierMode ? "Sprint"
                : itemMode ? "Forge"
                : traderMode ? "Compass"
                : emailMode ? "Echo"
                : "Atlas";
            string resolvedModel = ResolveAgentModel(activeAgentName);

            try
            {
                for (int iteration = 0; iteration < maxIterations; iteration++)
                {
                    // ΔΙΚΛΕΙΔΑ ΑΣΦΑΛΕΙΑΣ: στο ΤΕΛΕΥΤΑΙΟ διαθέσιμο iteration,
                    // αφαιρούμε το tools από το request - ο Claude ΔΕΝ μπορεί
                    // πια να ζητήσει tool_use, άρα αναγκαστικά επιστρέφει
                    // κείμενο (stop_reason θα είναι end_turn, όχι tool_use).
                    // Χωρίς αυτό, ένα πολύ βαρύ ερώτημα που χρειάζεται
                    // περισσότερα iterations απ' όσα έχουμε θα κατέληγε στο
                    // γενικό "έφτασα το όριο" μήνυμα πετώντας ό,τι δεδομένα
                    // είχε ήδη μαζέψει (βλ. session notes - επιβεβαιωμένο σε
                    // ερώτημα "στοιχεία και κινήσεις" λογαριασμού). Έτσι
                    // τουλάχιστον απαντάει με ό,τι έχει ήδη συλλέξει.
                    bool isLastIteration = iteration == maxIterations - 1;

                    string systemPromptText = BuildSystemPrompt(xSupport, forceFinalAnswer: isLastIteration,
                        helpMode: helpMode, reportDecimalPlaces: reportDecimalPlaces,
                        browserMode: browserMode, emailMode: emailMode, courierMode: courierMode,
                        extraInstructions: extraInstructions,
                        itemMode: itemMode, traderMode: traderMode,
                        currentUserName: currentUserName);

                    // Browser mode: ΔΙΟΡΘΩΘΗΚΕ 15/08 - ΚΑΙ query_data/
                        // export_query_to_file ΤΩΡΑ (πριν ήταν ΜΟΝΟ open_url/
                        // read_page_content - ο χρήστης ζήτησε ρητά τον
                        // συνδυασμό, π.χ. "βρες τα στοιχεία πελάτη στο Soft1
                        // ΚΑΙ δες το site του" μέσα στην ΙΔΙΑ συζήτηση).
                        // Email mode: ΝΕΟ 17/08 - ΧΩΡΙΣ open_url/read_page_content
                        // (άσχετα εδώ), ΜΕ read_email/download_email_attachment +
                        // create_crm_task (π.χ. "φτιάξε επόμενη ενέργεια από αυτό
                        // το email/ραντεβού") + query_data (ταυτοποίηση
                        // πελάτη/TRDR από το email αποστολέα). read_calendar θα
                        // προστεθεί εδώ όταν χτιστεί (βλ. README Roadmap #1).
                    object[] toolsForCall = isLastIteration
                            ? new object[0]
                            : browserMode
                                ? new object[] {
                                    JarvisTools.OpenUrlToolDefinition,
                                    JarvisTools.ReadPageContentToolDefinition,
                                    // ΝΕΟ 18/08, ρητό αίτημα χρήστη - "scraping δεδομένων
                                    // από ιστοσελίδες".
                                    JarvisTools.ExtractPageTablesToolDefinition,
                                    JarvisTools.QueryDataToolDefinition,
                                    JarvisTools.ExportQueryToFileToolDefinition,
                                    JarvisTools.OpenDocumentToolDefinition,
                                    JarvisTools.GetConversionTargetsToolDefinition,
                                    JarvisTools.CreateCrmTaskToolDefinition,
                                    JarvisTools.CreateOrderToolDefinition,
                                    JarvisEmailAccess.ReadEmailToolDefinition,
                                    JarvisEmailAccess.DownloadEmailAttachmentToolDefinition,
                                    // ΝΕΟ 18/08, ρητό αίτημα χρήστη - "θα πρέπει να το
                                    // βάλουμε να στέλνει email". Υποχρεωτική επιβεβαίωση
                                    // σε ξεχωριστό turn - βλ. BuildSystemPrompt πιο κάτω.
                                    JarvisEmailAccess.SendEmailToolDefinition,
                                    JarvisEmailAccess.ReplyEmailToolDefinition,
                                    // ΝΕΟ 18/08, ρητό αίτημα χρήστη - "εντολή που θα δουλεύει
                                    // περιγραφικά ... θα επιστρέφει σε modal τα στοιχεία της
                                    // επαφής". search_outlook_contacts fail-graceful αν λείπει
                                    // το Contacts.Read permission - βλ. BuildSystemPrompt.
                                    JarvisTools.ShowContactResultsToolDefinition,
                                    JarvisEmailAccess.SearchOutlookContactsToolDefinition,
                                    // ΝΕΟ 18/08, ρητό αίτημα χρήστη - "θέλω να μπορώ να βάζω
                                    // υπενθυμίσεις ... στο Outlook Calendar". Calendars.ReadWrite
                                    // ΗΔΗ υπάρχει (ο χρήστης το είχε ήδη προσθέσει).
                                    JarvisEmailAccess.CreateOutlookEventToolDefinition,
                                    // ΝΕΟ 18/08, ρητό αίτημα χρήστη - "στην Browser καρτέλα,
                                    // εφόσον έχουμε πλέον διαδικασία scrape, θα πρέπει και από
                                    // εκεί να εισάγουμε είδη πάλι με την ρουτίνα μας".
                                    JarvisItems.GetItemTemplateToolDefinition,
                                    JarvisItems.CreateItemToolDefinition
                                }
                                // ΔΙΟΡΘΩΘΗΚΕ 19/08: filter_email_inbox/filter_calendar/
                                // show_calendar_entries καλούν onFilterEmailInbox/
                                // onFilterCalendar/onShowCalendarEntries - ΚΕΝΑ (null)
                                // ΕΚΤΟΣ της πραγματικής Email κουρτίνας. isEmailCurtain
                                // (captured ΠΡΙΝ το routing) διαχωρίζει πραγματική
                                // κουρτίνα (πλήρες, αποκλειστικό σύνολο - ΠΟΤΕ
                                // συνδυάζεται με item/trader) από routed Echo στο γενικό
                                // chat (πάει στο BuildRoutedTools από κάτω, ΜΑΖΙ με
                                // itemMode/traderMode αν είναι ΚΑΙ αυτά ενεργά - ένωση).
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
                                        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - ΓΕΝΙΚΕΥΣΗ (ζωντανό feedback
                                        // χρήστη, βλ. RoutingDecision πιο πάνω): itemMode/
                                        // traderMode/emailMode(routed) ΔΕΝ είναι πια
                                        // αποκλειστικοί ternary-κλάδοι (θα έχανε tools αν 2+
                                        // flags ήταν true ταυτόχρονα σε ένωση/union) -
                                        // BuildRoutedTools προσθέτει ΠΡΟΣΘΕΤΙΚΑ (additive) το
                                        // κάθε σχετικό tool set πάνω σε ένα κοινό Atlas base
                                        // (ΠΕΡΙΛΑΜΒΑΝΕΙ ΗΔΗ send_email/reply_email/contact-
                                        // lookup - βλ. σχόλιο μέσα στη μέθοδο).
                                        : BuildRoutedTools(itemMode, traderMode, emailMode);

                    // Prompt caching (ΝΕΟ 19/08, ζωντανό review χρήστη -
                    // latency): breakpoint στο ΤΕΛΟΣ του system prompt ΚΑΙ
                    // στο ΤΕΛΕΥΤΑΙΟ tool definition - το Anthropic API
                    // cache-άρει ΟΛΟ το prefix μέχρι εκεί. Το system prompt
                    // ΚΑΙ τα tools είναι ΙΔΙΑ σε πολλά διαδοχικά iterations
                    // του ΙΔΙΟΥ turn (αλλάζει ΜΟΝΟ στο τελευταίο,
                    // forceFinalAnswer) - μεγάλο κέρδος σε bulk import (έως
                    // 40 iterations). ΠΡΙΝ: system ήταν απλό string (ΚΑΝΕΝΑ
                    // caching δυνατό - το Anthropic API χρειάζεται array με
                    // cache_control για να γίνει breakpoint).
                    var requestBody = new
                    {
                        model = resolvedModel,
                        max_tokens = MaxTokens,
                        // effort "medium": ισορροπία latency/κόστους για
                        // interactive chat - βλ. SKILL guidance.
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

                    // Main Jarvis chat goes through the signed Verilic endpoint.
                    // Licence, routing, account and model are re-validated server-side.
                    var proxyResp = await new S1Jarvis.Access.Verilic.VerilicAiMessagesClient()
                        .SendAsync(xSupport, anthropicJson, token);

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

                    // ΣΗΜΑΝΤΙΚΟ: περνάμε πίσω ΟΛΟΚΛΗΡΟ το content array (όχι μόνο
                    // το κείμενο/tool_use) - αν ο provider επιστρέφει thinking blocks, αυτά είναι
                    // μέρος του protocol και πρέπει να ταξιδεύουν
                    // αναλλοίωτα πίσω στο επόμενο turn, αλλιώς σκάει η κλήση.
                    history.Add(new JObject { ["role"] = "assistant", ["content"] = content });

                    // ΔΙΟΡΘΩΘΗΚΕ 19/08 - BUG (ζωντανό, σοβαρό: "ΜΟΝΙΜΑ 400 σε
                    // ΚΑΘΕ μήνυμα, ακόμα και 'είσαι εδώ;'"): όταν η απάντηση
                    // κόβεται στη μέση (stop_reason=="max_tokens" - π.χ. το
                    // Claude προσπαθούσε να γράψει send_email με ΤΕΡΑΣΤΙΟ
                    // attachmentContent/body, 148 γραμμές πελατών), το
                    // ΜΟΛΙΣ προστεθέν assistant μήνυμα πιο πάνω μπορεί να
                    // έχει ΗΜΙΤΕΛΕΣ/dangling tool_use block - ΚΑΝΕΝΑ tool
                    // ΔΕΝ εκτελείται σε αυτό το κλαδί (stopReason != "tool_use"
                    // πιο κάτω), άρα ΠΟΤΕ δεν προστίθεται το αντίστοιχο
                    // tool_result. Το Anthropic API απαιτεί ΚΑΘΕ tool_use να
                    // έχει tool_result στο ΕΠΟΜΕΝΟ μήνυμα - χωρίς αυτό, η
                    // ιστορία μένει ΜΟΝΙΜΑ κατεστραμμένη (η ΙΔΙΑ ιστορία
                    // στέλνεται σε ΚΑΘΕ επόμενο request) -> 400 σε ΚΑΘΕ
                    // επόμενο μήνυμα, ασχέτως περιεχομένου, μέχρι χειροκίνητο
                    // "CLEAR". Fix: αφαίρεσε το μόλις προστεθέν μήνυμα ΠΡΙΝ
                    // επιστρέψεις - η ιστορία μένει καθαρή/valid, ο χειριστής
                    // ξαναδοκιμάζει με κάτι πιο σύντομο. Βλ. ΚΑΙ MaxTokens
                    // (αυξήθηκε ΚΑΙ αυτό, μειώνει τη συχνότητα - ΔΕΝ λύνει
                    // την αιτία μόνο του, ΠΑΝΤΑ μπορεί να υπάρξει αρκετά
                    // μεγάλο αίτημα να ξαναχτυπήσει το όριο).
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

                    // Το loop συνεχίζει (θα εκτελεστούν tools) - ενημέρωσε το
                    // UI ΤΩΡΑ με σύντομο caption, ΠΡΙΝ την εκτέλεση (ώστε ο
                    // χειριστής να βλέπει τι ΠΑΕΙ να κάνει ο Jarvis, όχι με
                    // καθυστέρηση). Στο ΤΕΛΕΥΤΑΙΟ iteration (text answer) δεν
                    // χρειάζεται - η πραγματική απάντηση έρχεται αμέσως μετά.
                    onProgress?.Invoke(BuildProgressCaption(content));

                    // ── Εκτέλεση tools, ΟΛΑ τα tool_use blocks αυτού του turn,
                    //    αποτελέσματα πίσω σε ΕΝΑ user message (parallel tool
                    //    use - βλ. SKILL guidance, μη σπας σε πολλά messages). ──
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

                    // Δεύτερο caption ΓΙΑ ΤΟ ΑΠΟΤΕΛΕΣΜΑ (όχι μόνο πρόθεση πριν -
                    // βλ. onProgress πιο πάνω) - "τι βρήκε" πριν προχωρήσει στο
                    // επόμενο iteration (π.χ. ξανα-φιλτράρισμα). Μαζί με το
                    // caption πρόθεσης, το #orbCaption χτίζει ένα μικρό
                    // "log" βημάτων (βρήκε/φιλτράρει/ξαναέψαξε...) - βλ.
                    // index.html setThinkingCaption (accumulate, όχι replace).
                    string resultCaption = BuildResultCaption(toolResults);
                    if (resultCaption != null) onProgress?.Invoke(resultCaption);

                    history.Add(new JObject { ["role"] = "user", ["content"] = toolResults });
                }

                DebugLog.Log($"[loop] MaxIterations ({maxIterations}) reached χωρίς end_turn.");
                return "✖ Έφτασα το όριο βημάτων χωρίς τελική απάντηση - δοκίμασε πιο συγκεκριμένη ερώτηση.";
            }
            catch (OperationCanceledException)
            {
                // Ο χρήστης πάτησε "Stop" (βλ. CancelCurrent) - φιλικό μήνυμα
                // αντί για exception, ρωτάμε αν θέλει κάτι άλλο.
                DebugLog.Log("[loop] Ακυρώθηκε από τον χρήστη (Stop).");
                return "⏹ Σταμάτησα. Θέλεις κάτι άλλο;";
            }
            finally
            {
                // Καθάρισε το ΚΟΙΝΟ _cts ΜΟΝΟ αν ακόμα δείχνει στο ΔΙΚΟ ΜΑΣ
                // token source - αν κάποια νεότερη κλήση το έχει ήδη
                // αντικαταστήσει (overlapping AskAsync), μην την αγγίξεις.
                if (ReferenceEquals(_cts, myCts))
                {
                    _cts = null;
                }
                myCts.Dispose();
            }
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…(κόπηκε)";

        // Φτιάχνει το σύντομο caption για το #orbCaption (index.html) -
        // προτεραιότητα στο ΠΡΑΓΜΑΤΙΚΟ provider thinking block,
        // όταν υπάρχει, αλλιώς fallback σε
        // γενικό μήνυμα ανά tool name. ΠΟΤΕ κενό - πάντα κάτι φιλικό.
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
                case "query_data":
                    return "Ψάχνει στη βάση δεδομένων…";
                case "export_query_to_file":
                    return "Αποθηκεύει όλα τα αποτελέσματα σε αρχείο…";
                case "open_document":
                    return "Ανοίγει το παραστατικό στο Soft1…";
                case "extract_page_tables":
                    return "Διαβάζει πίνακες από τη σελίδα…";
                case "get_conversion_targets":
                    return "Ψάχνει πιθανές σειρές μετασχηματισμού…";
                case "create_crm_task":
                    return "Δημιουργεί εργασία CRM…";
                case "create_order":
                    return "Καταχωρεί παραγγελία…";
                case "read_email":
                    return "Διαβάζει τα email…";
                case "download_email_attachment":
                    return "Κατεβάζει συνημμένο…";
                case "read_calendar":
                    return "Διαβάζει το ημερολόγιο…";
                case "filter_email_inbox":
                    return "Ενημερώνει τη λίστα email…";
                case "filter_calendar":
                    return "Ενημερώνει το ημερολόγιο…";
                case "show_calendar_entries":
                    return "Δείχνει το αποτέλεσμα στο ημερολόγιο…";
                case "send_email":
                    return "Στέλνει το email…";
                case "reply_email":
                    return "Στέλνει την απάντηση…";
                case "search_outlook_contacts":
                    return "Ψάχνει τις επαφές Outlook…";
                case "show_contact_results":
                    return "Δείχνει τα στοιχεία επαφής…";
                case "export_shown_table":
                    return "Εξάγει τον πίνακα σε αρχείο…";
                case "create_outlook_event":
                    return "Δημιουργεί το ραντεβού στο Outlook…";
                case "get_item_template":
                    return "Μαζεύει στοιχεία πρότυπου είδους…";
                case "create_item":
                    return "Δημιουργεί το είδος…";
                case "show_courier_documents":
                    return "Δείχνει τα παραστατικά…";
                case "cancel_courier_voucher":
                    return "Ακυρώνει την αποστολή…";
                case "get_courier_voucher_data":
                    return "Μαζεύει στοιχεία αποστολέα/παραλήπτη/courier…";
                case "create_courier_voucher":
                    return "Εκδίδει την αποστολή…";
                case "find_trader_by_afm":
                    return "Ψάχνει συναλλασσόμενο με ΑΦΜ…";
                case "get_aade_data":
                    return "Φέρνει στοιχεία από την ΑΑΔΕ…";
                case "create_trader_from_aade":
                    return "Δημιουργεί συναλλασσόμενο…";
                default:
                    return "Επεξεργάζεται…";
            }
        }

        // Πρώτη πρόταση/γραμμή ενός thinking block - το caption είναι ΜΙΑ
        // κοντή γραμμή κάτω από το orb, όχι όλο το (συχνά μακρύ) σκεπτικό.
        private static string FirstSentence(string s)
        {
            int idx = s.IndexOfAny(new[] { '.', '\n' });
            return idx > 10 ? s.Substring(0, idx + 1).Trim() : s;
        }

        // Δεύτερο caption - "τι ΒΡΗΚΕ" (σε αντίθεση με το BuildProgressCaption
        // που λέει τι ΠΑΕΙ να κάνει) - διαβάζει το JSON payload του
        // query_data αποτελέσματος (rowCount/truncated, βλ.
        // JarvisTools.ExecuteQueryData) και φτιάχνει σύντομη περιγραφή. Μαζί
        // με τα captions πρόθεσης σε κάθε iteration, χτίζουν ένα μικρό
        // "log" (βρήκε/φιλτράρει/ξαναέψαξε...) στο #orbCaption. null αν δεν
        // υπάρχει κάτι αξιόλογο να πει - ΔΕΝ στέλνεται δεύτερο caption τότε.
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

                // export_query_to_file σχήμα (βλ. JarvisTools.
                // ExecuteExportQueryToFile) - ελέγχεται ΠΡΩΤΑ, ξεχωριστό
                // σχήμα από το query_data.
                if (payload["rowsWritten"] != null)
                {
                    int rowsWritten = (int?)payload["rowsWritten"] ?? 0;
                    bool wasCapped = (bool?)payload["wasCapped"] ?? false;
                    return wasCapped
                        ? $"Αποθήκευσε τις πρώτες {rowsWritten} γραμμές σε αρχείο."
                        : $"Αποθήκευσε {rowsWritten} γραμμές σε αρχείο.";
                }

                // ΔΙΟΡΘΩΘΗΚΕ 15/08: totalRowCount (πραγματικό σύνολο), ΟΧΙ
                // rowCount (το κομμένο πλήθος, ≤200 - βλ. JarvisTools.
                // ExecuteQueryData) - αλλιώς λέει πάντα "200" όταν στην
                // πραγματικότητα βρέθηκαν περισσότερα.
                int totalRowCount = (int?)payload["totalRowCount"] ?? -1;
                if (totalRowCount < 0) return null; // όχι αναγνωρίσιμο σχήμα
                return totalRowCount == 0
                    ? "Δεν βρήκε αποτελέσματα - ξαναψάχνει με άλλο κριτήριο…"
                    : $"Βρήκε {totalRowCount} εγγραφές.";
            }
            catch
            {
                return null; // όχι JSON (π.χ. μελλοντικό tool με άλλη μορφή)
            }
        }

        // Async (ΔΙΟΡΘΩΘΗΚΕ 15/08 - πριν sync) - το read_page_content
        // χρειάζεται await στο ExecuteScriptAsync (βλ. JarvisShell.
        // onReadPage). Τα υπόλοιπα tools παραμένουν sync εσωτερικά, απλά
        // "τυλίγονται" σε ολοκληρωμένο Task μέσω του await.
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
                case "query_data":
                    return JarvisTools.ExecuteQueryData(xSupport, input["sql"]?.ToString());
                case "export_query_to_file":
                    return JarvisTools.ExecuteExportQueryToFile(xSupport, input);
                case "open_url":
                    return JarvisTools.ExecuteOpenUrl(input["url"]?.ToString(), onNavigate);
                case "read_page_content":
                    return await JarvisTools.ExecuteReadPageContent(xSupport, onReadPage);
                case "extract_page_tables":
                    return await JarvisTools.ExecuteExtractPageTables(input, onExtractPageTables);
                case "open_document":
                    return JarvisTools.ExecuteOpenDocument(xSupport, input);
                case "get_conversion_targets":
                    return JarvisTools.ExecuteGetConversionTargets(xSupport, (int)input["findoc"]);
                case "create_crm_task":
                    return JarvisTools.ExecuteCreateCrmTask(xSupport, input);
                case "create_order":
                    return JarvisTools.ExecuteCreateOrder(xSupport, input);
                case "read_email":
                    return await JarvisEmailAccess.ExecuteReadEmail(xSupport, input);
                case "download_email_attachment":
                    return await JarvisEmailAccess.ExecuteDownloadEmailAttachment(xSupport, input);
                case "read_calendar":
                    return await JarvisEmailAccess.ExecuteReadCalendar(xSupport, input);
                case "filter_email_inbox":
                    return JarvisEmailAccess.ExecuteFilterEmailInbox(input, onFilterEmailInbox);
                case "filter_calendar":
                    return JarvisEmailAccess.ExecuteFilterCalendar(input, onFilterCalendar);
                case "show_calendar_entries":
                    return JarvisEmailAccess.ExecuteShowCalendarEntries(input, onShowCalendarEntries);
                case "send_email":
                    return await JarvisEmailAccess.ExecuteSendEmail(xSupport, input);
                case "reply_email":
                    return await JarvisEmailAccess.ExecuteReplyEmail(xSupport, input);
                case "search_outlook_contacts":
                    return await JarvisEmailAccess.ExecuteSearchOutlookContacts(xSupport, input);
                case "show_contact_results":
                    return JarvisTools.ExecuteShowContactResults(input, onShowContactResults);
                case "export_shown_table":
                    return await JarvisTools.ExecuteExportShownTable(input, onExportShownTable);
                case "create_outlook_event":
                    return await JarvisEmailAccess.ExecuteCreateOutlookEvent(xSupport, input);
                case "get_item_template":
                    return JarvisItems.ExecuteGetItemTemplate(xSupport, input);
                case "create_item":
                    return JarvisItems.ExecuteCreateItem(xSupport, input);
                case "show_courier_documents":
                    return JarvisCourier.ExecuteShowCourierDocuments(input, onShowCourierDocuments);
                case "cancel_courier_voucher":
                    return await JarvisCourier.ExecuteCancelCourierVoucherChatAsync(xSupport, input);
                case "get_courier_voucher_data":
                    return JarvisCourier.ExecuteGetCourierVoucherData(xSupport, input);
                case "create_courier_voucher":
                    return await JarvisCourier.ExecuteCreateCourierVoucherChatAsync(xSupport, input);
                case "find_trader_by_afm":
                case "get_aade_data":
                case "create_trader_from_aade":
                {
                    // ΝΕΟ 18/08 - τα 3 αυτά tools είναι διαθέσιμα στο ΓΕΝΙΚΟ
                    // chat (όχι πίσω από κάποιο "mode" flag σαν το courier/
                    // email), οπότε ΔΕΝ υπάρχει προηγούμενο "start" gate να
                    // τα προστατέψει - ρητός έλεγχος εδώ, ΚΑΘΕ φορά, ΙΔΙΟ
                    // toolName (JARVISDOCREADER) με το standalone
                    // CREATEAADEAFM. Μαθημένο από το κενό που βρήκαμε νωρίτερα
                    // σήμερα στο HandleDrManualLookupAsync - δεν εμπιστευόμαστε
                    // κανένα προϋπάρχον flag, ελέγχουμε ρητά στο σημείο χρήσης.
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

        // Prompt caching helper - ΝΕΟ 19/08, βλ. σχόλιο στο σημείο κλήσης
        // (AskAsync, μέσα στο loop). Σημειώνει cache_control ΜΟΝΟ στο
        // ΤΕΛΕΥΤΑΙΟ tool - το Anthropic API cache-άρει το prefix ΜΕΧΡΙ ΚΑΙ
        // το breakpoint (δηλαδή ΟΛΑ τα tools πριν από αυτό), όχι ανά tool.
        // Άδειο array (isLastIteration) δεν χρειάζεται τίποτα - τίποτα να
        // cache-άρει.
        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - BUG (ζωντανό 400 error): "return new object[]
        // { arr }" τύλιγε ΟΛΟ το JArray σαν ΕΝΑ στοιχείο ΝΕΟΥ array -
        // δηλαδή έστελνε "tools": [[tool1, tool2, ...]] (array ΜΕΣΑ σε
        // array) αντί για "tools": [tool1, tool2, ...] - το Anthropic API
        // το απέρριπτε με 400 (malformed tools). Return type ΤΩΡΑ "object"
        // (ΟΧΙ "object[]") - επιστρέφουμε το JArray ΑΠΕΥΘΕΙΑΣ, το
        // JsonConvert.SerializeObject το σειριοποιεί σωστά σαν JSON array.
        private static object ToolsWithCacheBreakpoint(object[] tools)
        {
            if (tools == null || tools.Length == 0) return tools;
            JArray arr = JArray.FromObject(tools);
            if (arr.Last is JObject lastTool)
                lastTool["cache_control"] = new JObject { ["type"] = "ephemeral" };
            return arr;
        }

        // ΝΕΟ 19/08, agent-clustering restructuring (ζωντανό review χρήστη -
        // latency): v1 router για το ελεύθερο κύριο chat - ΑΠΛΟ keyword
        // heuristic, ΧΩΡΙΣ επιπλέον LLM call (θα ακύρωνε το ίδιο το latency
        // κέρδος που ψάχνουμε).
        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - BUG (ζωντανό report χρήστη, 400/λάθος
        // απάντηση): η ΠΡΩΤΗ έκδοση έψαχνε ΑΚΡΙΒΕΙΣ, ΤΟΝΙΣΜΕΝΕΣ, ΓΕΙΤΟΝΙΚΕΣ
        // φράσεις (π.χ. "άνοιξε είδος") - απέτυχε στο πραγματικό "άνοιξε
        // μου έναν *νεο* είδος με πρότυπο το 1002" (ο χρήστης έγραψε
        // "νεο" ΧΩΡΙΣ τόνο, ΚΑΙ οι λέξεις δεν ήταν γειτονικές). Τώρα:
        // (1) αφαιρούνται οι τόνοι ΚΑΙ από το κείμενο ΚΑΙ από τα keywords
        // πριν τη σύγκριση (NormalizeGreek) - καλύπτει τη ρεαλιστική
        // casual γραφή χωρίς τόνους, (2) η αντιστοίχιση είναι σε επίπεδο
        // ΛΕΞΗΣ/stem (ρήμα-πρόθεσης-δημιουργίας + ουσιαστικό-domain,
        // ΟΠΟΥΔΗΠΟΤΕ στη φράση, ΟΧΙ γειτονικά) αντί για άκαμπτες φράσεις -
        // πολύ πιο ανθεκτικό σε πραγματική ελληνική σύνταξη. Παραμένει
        // ΣΥΝΤΗΡΗΤΙΚΟ ως προς false positives: item/trader ΑΠΑΙΤΟΥΝ
        // συνδυασμό ρήμα-δημιουργίας + domain-ουσιαστικό (π.χ. ΜΟΝΟ
        // "πελάτης" σε ένα reporting ερώτημα ΔΕΝ αρκεί).
        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - ΓΕΝΙΚΕΥΣΗ (ζωντανό feedback χρήστη): "πρέπει
        // να έχει μια λίστα ανά agent με τα skill set... όταν δεν μπορεί
        // να αποφασίσει ο ίδιος να δίνει όλα τα παρεμφερή skills των
        // agents ως επιλογές". ΠΡΙΝ, όταν 2+ σήματα ταυτόχρονα έδειχναν
        // σχετικά (π.χ. "άνοιξε πελάτη Χ ΚΑΙ ένα νέο είδος Υ"), το router
        // επέστρεφε ΕΝΑ όνομα - ΟΛΑ τα υπόλοιπα χάνονταν, έπεφτε σε
        // γενικό fallback ΧΩΡΙΣ κανένα από τα σχετικά tools. Τώρα
        // επιστρέφει `RoutingDecision` με ΤΑ ΤΡΙΑ flags (Item/Trader/
        // Email) - μπορούν να είναι ΠΕΡΙΣΣΟΤΕΡΑ ΑΠΟ ΕΝΑ ταυτόχρονα (ένωση/
        // union), ΟΧΙ αποκλειστική επιλογή· βλ. `BuildRoutedTools` στο
        // tools ternary πιο κάτω, που ΤΩΡΑ προσθέτει προσθετικά (additive)
        // τα σχετικά tools σε κάθε ενεργό flag αντί να διαλέγει ΕΝΑ κλάδο.
        // Αν ΚΑΝΕΝΑ σαφές σήμα δεν βρεθεί (hitCount==0), "sticky": κρατάει
        // το mode του προηγούμενου turn (routingHint) - καλύπτει σύντομες
        // απαντήσεις συνέχειας.
        // v1, θα συνεχίσει να βελτιώνεται με βάση ζωντανή δοκιμή - README.
        private struct RoutingDecision
        {
            public bool Item;
            public bool Trader;
            public bool Email;
            // Τι αποθηκεύεται ως routingHint για το ΕΠΟΜΕΝΟ turn (sticky
            // continuation) - ΠΑΝΤΑ ΕΝΑ όνομα, ακόμα κι όταν αυτό το turn
            // ήταν ένωση πολλαπλών (ασφαλές, απλό fallback: "general").
            public string StickyLabel;
        }

        private static RoutingDecision RouteMainChatAgent(string userText, string routingHint)
        {
            string t = NormalizeGreek(userText);

            // "πρόθεση δημιουργίας/ανοίγματος" - μοιράζεται ανάμεσα σε
            // item/trader, ΠΑΝΤΑ σε συνδυασμό με domain-ουσιαστικό πιο
            // κάτω (ΟΧΙ standalone signal, πολύ γενικό μόνο του).
            // ΔΙΟΡΘΩΘΗΚΕ 19/08 (προληπτικά, πριν από ζωντανό test με
            // πολλαπλά ρήματα σε ΔΙΑΦΟΡΕΤΙΚΗ κλίση - "θέλω να στείλεις"
            // ΑΝΤΙ για προστακτική "στείλε"): STEMS αντί για ολόκληρες
            // κλιτές λέξεις - τα Ελληνικά ρήματα έχουν ΠΟΛΛΕΣ καταλήξεις
            // (προστακτική/υποτακτική/α'/β'/γ' πρόσωπο) που ΔΕΝ
            // ταιριάζουν με exact-word match. π.χ. "ανοιξ" καλύπτει
            // άνοιξε/ανοίξω/ανοίξεις/ανοίξει/ανοίξτε ΜΑΖΙ. Stems
            // επιλεγμένα αρκετά ΜΑΚΡΙΑ ώστε να ΜΗΝ πιάνουν άσχετες λέξεις
            // (false-positive risk).
            string[] createVerbs = {
                "ανοιγ", "ανοιξ", "δημιουργ", "φτιαξ", "φτιαχν",
                "καταχωρ", "εισαγ", "εισηγαγ",
                "νεο", "νεα", "νεος"
            };
            bool createVerbHit = ContainsAny(t, createVerbs);

            // Item: "ειδος/ειδη/..." standalone ΔΕΝ αρκεί (βλ. "δείξε μου
            // τα είδη..." - reporting) - χρειάζεται createVerbHit ΜΑΖΙ.
            // mtrl/τιμοκαταλογ/bulk import παραμένουν αρκετά ΜΟΝΑ τους
            // (σπάνια εμφανίζονται εκτός context δημιουργίας/εισαγωγής).
            bool itemNounHit = ContainsAny(t, new[] { "ειδος", "ειδη", "ειδων", "ειδους" });
            bool itemHit = (createVerbHit && itemNounHit)
                || t.Contains("mtrl") || t.Contains(NormalizeGreek("τιμοκατάλογ")) || t.Contains("bulk import");

            // Trader: "πελατ/προμηθευτ/συναλλασσομεν" stems χρειάζονται
            // createVerbHit (ίδιο σκεπτικό με το item). "αφμ" παραμένει
            // αρκετό ΜΟΝΟ του - σπάνιο εκτός trader-lookup/creation context.
            bool traderNounHit = t.Contains(NormalizeGreek("πελάτ")) || t.Contains(NormalizeGreek("προμηθευτ"))
                || t.Contains(NormalizeGreek("συναλλασσόμεν"));
            bool traderHit = (createVerbHit && traderNounHit) || t.Contains("αφμ");

            // Email: ρήμα αποστολής/γραφής + email/mail, Ή αυτόνομα αρκετά
            // ισχυρά standalone terms (ραντεβού/υπενθύμιση Outlook, εύρεση
            // επαφής). ΔΙΟΡΘΩΘΗΚΕ 19/08 (προληπτικά, πριν ζωντανό test με
            // "θέλω να στείλεις...αφού ελέγξεις") - STEMS αντί για μόνο
            // προστακτική, ίδιο σκεπτικό με τα createVerbs πιο πάνω. Οι
            // stems είναι ΚΑΙ present ΚΑΙ aorist όπου διαφέρουν (π.χ.
            // γράφω/έγραψα, ψάχνω/έψαξα - Ελληνικά ρήματα συχνά έχουν 2
            // ΔΙΑΦΟΡΕΤΙΚΕΣ ρίζες).
            string[] emailVerbStems = { "στειλ", "στελν", "απαντ", "γραψ", "γραφ" };
            bool emailVerbHit = ContainsAny(t, emailVerbStems);
            bool emailNounHit = t.Contains("email") || t.Contains("mail");
            // ΔΙΟΡΘΩΘΗΚΕ 19/08 (ζωντανό bug report χρήστη - "βρες από τα
            // email μου ένα του Μυλωνά..." δεν έπιασε emailHit καθόλου -
            // ο Jarvis σωστά ανέφερε ότι δεν έχει read_email διαθέσιμο,
            // ΓΙΑΤΙ έπεσε σε sticky από ΑΣΧΕΤΟ προηγούμενο "item" turn).
            // emailVerbHit πιο πάνω καλύπτει ΜΟΝΟ ρήματα ΑΠΟΣΤΟΛΗΣ -
            // χρειάζεται ΞΕΧΩΡΙΣΤΟ σήμα για ρήματα ΑΝΑΖΗΤΗΣΗΣ/ΑΝΑΓΝΩΣΗΣ/
            // ΕΛΕΓΧΟΥ (βρες/ψάξε/διάβασε/δες/κοίτα/ελέγξεις) + email/mail/
            // εισερχόμενα. STEMS, ΙΔΙΟ σκεπτικό με πιο πάνω.
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
                // ΕΝΑ σήμα -> καθαρή, σίγουρη επιλογή (StickyLabel = αυτό
                // το ΕΝΑ). ΔΥΟ+ σήματα ταυτόχρονα -> ΕΝΩΣΗ (ΟΛΑ τα σχετικά
                // flags true μαζί) - ο Jarvis παίρνει ΟΛΑ τα σχετικά tools
                // ως επιλογές αντί να χάσει τα πάντα σε γενικό fallback.
                string sticky = hitCount == 1
                    ? (itemHit ? "item" : traderHit ? "trader" : "email")
                    : "general";
                return new RoutingDecision { Item = itemHit, Trader = traderHit, Email = emailHit, StickyLabel = sticky };
            }

            // Κανένα σήμα -> sticky στο προηγούμενο mode αν υπάρχει,
            // αλλιώς γενικό (Atlas).
            string fallback = !string.IsNullOrEmpty(routingHint) ? routingHint : "general";
            return new RoutingDecision
            {
                Item = fallback == "item",
                Trader = fallback == "trader",
                Email = fallback == "email",
                StickyLabel = fallback
            };
        }

        // ΝΕΟ 19/08 - tools για το ελεύθερο κύριο chat (Atlas + routed
        // item/trader/email), καλείται ΜΟΝΟ όταν ΔΕΝ είναι browserMode/
        // isEmailCurtain/courierMode (αυτά έχουν ΔΙΚΑ τους, αμετάβλητα,
        // αποκλειστικά branches πιο πάνω στο ternary). ΠΡΟΣΘΕΤΙΚΟ
        // (additive) - ΟΧΙ αποκλειστική επιλογή ΕΝΟΣ κλάδου: το Atlas
        // base είναι ΠΑΝΤΑ παρόν, item/trader/email-extra tools
        // προστίθενται ΜΟΝΟ όταν το αντίστοιχο flag είναι true, και
        // μπορούν να είναι ΠΕΡΙΣΣΟΤΕΡΑ ΑΠΟ ΕΝΑ ταυτόχρονα (π.χ. "άνοιξε
        // ένα είδος ΚΑΙ στείλε μήνυμα στον Χ" -> itemMode=true ΚΑΙ το
        // Atlas base ΗΔΗ έχει send_email - και τα δύο διαθέσιμα στο ΙΔΙΟ
        // turn, ΧΩΡΙΣ να χαθεί κανένα). ΠΡΟΣΟΧΗ: item/trader είναι
        // ΓΝΗΣΙΑ άσχετα μεταξύ τους (ΔΕΝ ενώνονται επειδή "δεν ξέρουμε" -
        // ενώνονται ΜΟΝΟ όταν το ΙΔΙΟ κείμενο ζητάει ΚΑΙ τα δύο ρητά,
        // βλ. RouteMainChatAgent).
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
                // ΔΙΟΡΘΩΘΗΚΕ 19/08 (ζωντανό bug report - "στείλε ένα
                // μήνυμα..." ΕΠΙΤΗΔΕΣ διφορούμενο ανάμεσα σε email ΚΑΙ CRM
                // task): ΠΑΝΤΑ παρόντα εδώ, ΟΧΙ μόνο όταν emailMode=true -
                // φθηνά σε tokens (μικρά schemas, cached), το πραγματικό
                // κόστος που κόπηκε στο restructuring ήταν το ΒΑΡΥ prompt
                // ΚΕΙΜΕΝΟ (emailMode block), ΟΧΙ τα ίδια τα tool ορίσματα.
                JarvisEmailAccess.SendEmailToolDefinition,
                JarvisEmailAccess.ReplyEmailToolDefinition,
                JarvisTools.ShowContactResultsToolDefinition,
                JarvisEmailAccess.SearchOutlookContactsToolDefinition,
                // ΝΕΟ 19/08, ρητό αίτημα χρήστη - "το κουμπί PDF/CSV/
                // Excel πρέπει να είναι οδηγία για τον agent". v1: ΜΟΝΟ
                // κύριο chat (window.triggerTableExport στο index.html
                // δεν είναι ακόμα wired για κουρτίνες).
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
                // Επιπλέον πέρα από ό,τι ΗΔΗ έχει το base - μόνο όταν ο
                // router είναι ΠΡΑΓΜΑΤΙΚΑ σίγουρος για email (emailHit).
                list.Add(JarvisEmailAccess.ReadEmailToolDefinition);
                list.Add(JarvisEmailAccess.DownloadEmailAttachmentToolDefinition);
                list.Add(JarvisEmailAccess.ReadCalendarToolDefinition);
                list.Add(JarvisEmailAccess.CreateOutlookEventToolDefinition);
                // ΝΕΟ 20/08, ρητό αίτημα χρήστη - "θελω να στελνει τα
                // request στην κουρτινα": αν ο χειριστής ζητήσει ακριβές
                // φιλτράρισμα (κατάσταση ανάγνωσης/εύρος/κτλ.) από το ΚΥΡΙΟ
                // chat, ο Jarvis πλέον έχει το ΙΔΙΟ filter_email_inbox tool
                // (πριν υπήρχε ΜΟΝΟ μέσα στην κουρτίνα Email) - το
                // onFilterEmailInbox callback (βλ. JarvisShell.xaml.cs) ήδη
                // ανοίγει την κουρτίνα αν δεν είναι ήδη ανοιχτή, ώστε ο
                // χειριστής να δει ΑΜΕΣΩΣ το φιλτραρισμένο αποτέλεσμα εκεί.
                list.Add(JarvisEmailAccess.FilterEmailInboxToolDefinition);
            }
            return list.ToArray();
        }

        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - keyword literals εδώ ΔΕΝ χρειάζεται να είναι
        // ήδη normalized (γράφονται κανονικά, ευανάγνωστα) - το
        // NormalizeGreek περνάει ΚΑΙ από τα δύο μέρη τη στιγμή της
        // σύγκρισης, οπότε ταιριάζουν ανεξάρτητα από το πώς γράφτηκαν.
        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (string needle in needles)
                if (haystack.Contains(NormalizeGreek(needle))) return true;
            return false;
        }

        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - BUG #2 (ζωντανό report χρήστη): "στήλε ένα
        // email στον Χ" ΔΕΝ έπιασε emailMode - ο χρήστης έγραψε "στήλε"
        // (με ή) αντί για το ορθό "στείλε" (με ει). Στα Νέα Ελληνικά τα
        // η/ι/υ/ει/οι/υι ΠΡΟΦΕΡΟΝΤΑΙ όλα ίδια ("ι") - μόνη αφαίρεση τόνων
        // (StripGreekAccents) ΔΕΝ αρκούσε, χρειάζεται ΚΑΙ φωνητική
        // εξίσωση αυτών των homophone γραφών. NormalizeGreek =
        // ToLowerInvariant + αφαίρεση τόνων + "phonetic fold":
        // αι->ε, ει/οι/υι->ι, η/υ->ι, ω->ο (το "ου" προστατεύεται - ΔΙΚΟΣ
        // του ήχος, ΔΕΝ είναι το ίδιο με "ο"/"ι"). Εφαρμόζεται ΚΑΙ στο
        // κείμενο του χρήστη ΚΑΙ σε κάθε keyword τη στιγμή της σύγκρισης
        // (ContainsAny/inline .Contains) - ανεξάρτητα ποια ορθογραφία
        // χρησιμοποιήθηκε, καταλήγουν στην ΙΔΙΑ κανονική μορφή.
        private static string NormalizeGreek(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.ToLowerInvariant();

            // 1) Αφαίρεση τόνων.
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

            // 2) Phonetic fold - "ου" προστατεύεται (δικός του ήχος) πριν
            // τις αντικαταστάσεις, επαναφέρεται στο τέλος.
            const string OuMarker = "";
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

        // AI provider/model ownership belongs to Verilic startup Health.
        // The open Jarvis session uses one immutable snapshot; no cccParams
        // model override and no per-prompt routing/model lookup is allowed.
        private static string ResolveAgentModel(string agentName)
        {
            string model = JarvisAgentRuntimeSnapshot.ResolveModel(agentName);
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException(
                    "AI startup snapshot is unavailable for agent " +
                    (agentName ?? "<null>") + ". Restart Jarvis after a successful Health check.");
            return model;
        }

        private string BuildSystemPrompt(
            XSupport xSupport, bool forceFinalAnswer = false, bool helpMode = false,
            int reportDecimalPlaces = 2, bool browserMode = false, bool emailMode = false,
            bool courierMode = false, string extraInstructions = null,
            bool itemMode = false, bool traderMode = false, string currentUserName = null)
        {
            if (xSupport == null || xSupport.ConnectionInfo == null)
                throw new InvalidOperationException("Jarvis runtime context is unavailable.");

            var info = xSupport.ConnectionInfo;
            string mode = helpMode ? "help"
                : browserMode ? "browser"
                : courierMode ? "courier"
                : emailMode ? "email"
                : itemMode ? "item"
                : traderMode ? "trader"
                : "general";

            var context = new JObject
            {
                ["companyId"] = info.CompanyId,
                ["branchId"] = info.BranchId,
                ["currentUserId"] = info.UserId,
                ["currentUserName"] = currentUserName ?? string.Empty,
                ["mode"] = mode,
                ["reportDecimalPlaces"] = reportDecimalPlaces,
                ["forceFinalAnswer"] = forceFinalAnswer
            };

            if (!string.IsNullOrWhiteSpace(extraInstructions))
                context["administratorBusinessContext"] = extraInstructions;

            return
                "Είσαι ο Jarvis μέσα στο Soft1. Το ακόλουθο JSON είναι μόνο runtime/knowledge context. " +
                "Οι behavioral policies παρέχονται αποκλειστικά από το JARVIS_POLICY_CONTEXT.\\n" +
                context.ToString(Formatting.None);
        }

        // ── DR (Document Reader) - Στάδιο 3α: αναγνώριση ΑΦΜ εκδότη ─────────
        // Μονο-γύρος (one-shot) vision call, ΕΚΤΟΣ του κύριου multi-turn tool
        // loop πιο πάνω - ΙΔΙΟ proven prompt/JSON σχήμα με S1DocReader's
        // ProxyAgentClient.DetectAfmAsync (βλ. session notes 16/08), ΙΔΙΟ
        // proxy endpoint (/agent/vision) ΚΑΙ ΙΔΙΟ agentAccountRef με τον
        // Jarvis - ΚΑΜΙΑ ξεχωριστή AI agent (βλ. README Roadmap #6,
        // JarvisDocReader = feature-gate ΜΟΝΟ, ο Jarvis ΕΙΝΑΙ ήδη ο agent).
        // Χρησιμοποιεί το configured Jarvis model του startup snapshot - απλή οπτική
        // εξαγωγή λίγων πεδίων, όχι πολύπλοκο reasoning· σημαντικό μιας και
        // τρέχει ΑΝΑ αρχείο μέσα σε sequential loop (βλ. index.html
        // drProcessBtn) - λιγότερο latency/κόστος ανά κλήση.
        // Ίδιος περιορισμός τύπου αρχείου με το AskAsync attachment πιο πάνω
        // (μόνο PDF/εικόνα - το Anthropic API δεν δέχεται raw Excel/Word).
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
                    ["errorMessage"] = "Μη υποστηριζόμενος τύπος αρχείου για AI ανάγνωση ακόμα " +
                        "(μόνο PDF/εικόνα προς το παρόν, ίδιος περιορισμός με το κύριο chat attachment)."
                };
            }

            string prompt =
                "Κοίτα αυτό το παραστατικό και βρες ΜΟΝΟ το ΑΦΜ του ΕΚΔΟΤΗ (όχι του πελάτη/παραλήπτη).\n" +
                "Το ΑΦΜ εκδότη βρίσκεται συνήθως στο επάνω μέρος, δίπλα στην επωνυμία της εταιρείας που εκδίδει.\n" +
                "Επίσης βρες την επωνυμία του εκδότη, τον τύπο παραστατικού (π.χ. ΤΙΜ/ΤΔΑ/ΤΠΥ/ΔΑ/ΑΠΔ), " +
                "τον αριθμό παραστατικού ΚΑΙ την ημερομηνία έκδοσης (χρειάζεται για έλεγχο διπλοκαταχώρησης " +
                "ΠΡΙΝ την πλήρη εξαγωγή γραμμών, μορφή ΗΗ/ΜΜ/ΕΕΕΕ όπως εμφανίζεται).\n\n" +
                "Επέστρεψε ΜΟΝΟ JSON χωρίς καμία εξήγηση:\n" +
                "{\n" +
                "  \"issuer_afm\": \"xxxxxxxxx\",\n" +
                "  \"issuer_name\": \"επωνυμία εκδότη\",\n" +
                "  \"doc_type\": \"ΤΙΜ|ΤΔΑ|ΤΠΥ|ΔΑ|ΑΠΔ\",\n" +
                "  \"doc_number\": \"αριθμός παραστατικού\",\n" +
                "  \"doc_date\": \"ΗΗ/ΜΜ/ΕΕΕΕ\",\n" +
                "  \"confidence\": 0.95\n" +
                "}";

            var requestBody = new
            {
                model = ResolveAgentModel("Jarvis"),
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

        // ── DR - Στάδιο 4: πλήρης εξαγωγή γραμμών ────────────────────────────
        // Δεύτερη, ΠΙΟ ΒΑΘΙΑ AI κλήση με το ίδιο configured Jarvis target όπως το lightweight
        // DetectDocumentIssuerAsync - χρειάζεται ακρίβεια σε ποσά/ΦΠΑ, όχι
        // απλή αναγνώριση) - ίδιο γενικό ("generic prompt") σχήμα με
        // S1DocReader's PromptBuilder.BuildGenericPrompt/GetJsonOutputInstructions
        // (proven JSON schema) - ΧΩΡΙΣ το "learned profile"/targeted κομμάτι
        // (Nexus label-learning, βλ. session notes 16/08 - συζητήθηκε, ΟΧΙ
        // ακόμα, μπαίνει σαν βελτιστοποίηση ΜΕΤΑ που θα δουλεύει το generic
        // baseline). Το myDATA "cross-check" (ρητή διευκρίνιση χρήστη
        // 16/08) ΔΕΝ είναι live API call - είναι ΑΠΛΑ ανάγνωση/εμφάνιση του
        // ΑΝΟΙΧΤΟΥ link (χωρίς key) που τυπώνεται πάνω στο παραστατικό, αν
        // υπάρχει - ο χειριστής το ανοίγει ο ίδιος αν θέλει επιβεβαίωση.
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
                $"\nΚΡΙΣΙΜΟ: Το ΑΦΜ της δικής μας εταιρίας (παραλήπτης) είναι {companyAfm}. " +
                $"Οποιοδήποτε ΑΦΜ ΔΕΝ είναι το {companyAfm} ανήκει στον ΕΚΔΟΤΗ.\n";

            string prompt =
                "Είσαι ειδικός στην ανάγνωση ελληνικών παραστατικών. Διάβασε ΠΡΟΣΕΚΤΙΚΑ αυτό το " +
                "παραστατικό και εξήγαγε ΟΛΑ τα στοιχεία του, ειδικά τις γραμμές ειδών με ακρίβεια " +
                "στα ποσά/ΦΠΑ." + companyRule +
                "Αν υπάρχει QR code ή link myDATA τυπωμένο πάνω στο παραστατικό, γράψε το ΑΚΡΙΒΩΣ " +
                "όπως εμφανίζεται στο aade_link (είναι ΔΗΜΟΣΙΟ link, χωρίς κλειδί - ο χειριστής το " +
                "ανοίγει ο ίδιος για επιβεβαίωση, ΜΗΝ προσπαθήσεις να το επισκεφτείς).\n\n" +
                "Επέστρεψε ΜΟΝΟ JSON χωρίς καμία εξήγηση:\n" +
                "{\n" +
                "  \"issuer\": {\"afm\": \"\", \"name\": \"\", \"doy\": \"\", \"address\": \"\"},\n" +
                "  \"document_info\": {\"type\": \"ΤΙΜ|ΤΔΑ|ΤΠΥ|ΔΑ|ΑΠΔ\", \"series\": \"\", \"number\": \"\", \"date\": \"\"},\n" +
                "  \"line_items\": [\n" +
                "    { \"code\": \"\", \"description\": \"\", \"quantity\": \"\", \"unit\": \"\",\n" +
                "      \"unit_price\": \"\", \"discount\": \"\", \"net_value\": \"\",\n" +
                "      \"vat_rate\": \"\", \"vat_amount\": \"\", \"line_total\": \"\" }\n" +
                "  ],\n" +
                "  \"totals\": { \"net_total\": \"\", \"discount_total\": \"\", \"expenses_total\": \"\",\n" +
                "               \"vat_total\": \"\", \"grand_total\": \"\" },\n" +
                "  \"aade_link\": \"\",\n" +
                "  \"remarks\": \"\",\n" +
                "  \"confidence\": 0.95\n" +
                "}\n\n" +
                "Αριθμοί ΟΠΩΣ εμφανίζονται στο έντυπο (ελληνική μορφή, π.χ. 1.234,56). " +
                "Κενά πεδία -> \"\" (ΟΧΙ null). \"code\" στις γραμμές = ο κωδικός είδους ΤΟΥ ΕΚΔΟΤΗ " +
                "(όχι δικός μας), όπως τυπώνεται στο παραστατικό.";

            var requestBody = new
            {
                model = ResolveAgentModel("Jarvis"),
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

        // Το Claude μερικές φορές τυλίγει το JSON σε ```json fences παρόλο
        // που το prompt λέει "ΜΟΝΟ JSON" - ίδιο cleanup με S1DocReader's
        // ProxyAgentClient.CleanJson, proven pattern.
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
