using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Softone;
using S1Jarvis.Access;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell : UserControl
    {
        private readonly XSupport _xSupport;
        private readonly JarvisAgentClient _agentClient = new JarvisAgentClient();
        private readonly List<JObject> _conversation = new List<JObject>();

        // Ξεχωριστό conversation history για το Help mode (βλ. README) - ΔΕΝ
        // μοιράζεται με το κύριο _conversation, μηδενίζεται σε κάθε νέα
        // "help" συνεδρία (βλ. "help_start" στο CoreWebView2_WebMessageReceived).
        private readonly List<JObject> _helpConversation = new List<JObject>();

        // Ξεχωριστό conversation history για το Browser mode (βλ. README) -
        // ίδιο σκεπτικό με _helpConversation.
        private readonly List<JObject> _browserConversation = new List<JObject>();

        // Ξεχωριστό conversation history για την κουρτίνα "Email" (ΝΕΟ
        // 17/08, ρητό αίτημα χρήστη - βλ. README Roadmap #1, index.html
        // #emailCurtain) - ίδιο σκεπτικό με _browserConversation, αλλά
        // ΧΩΡΙΣ native pane (καθαρό WebView2 curtain, ίδιο idiom με
        // Dashboard/Help/DR - όχι Browser).
        private readonly List<JObject> _emailConversation = new List<JObject>();

        // Ξεχωριστό conversation history για την κουρτίνα "Courier" (ΝΕΟ
        // 17/08, ρητό αίτημα χρήστη - JARVISCOURIER feature). ΙΔΙΟ
        // entitlement idiom με το DR (_drAllowed) - lazy έλεγχος ΜΟΝΟ όταν
        // ανοίξει η κουρτίνα, ΟΧΙ στο αρχικό NavigationCompleted.
        private readonly List<JObject> _courierConversation = new List<JObject>();
        private bool _courierAllowed;

        // ΝΕΟ 19/08, agent-clustering restructuring: "sticky" routing state
        // ΜΟΝΟ για το κύριο (ελεύθερο) chat - το mode ("item"/"trader"/
        // "email"/"general") που διάλεξε ο router (JarvisAgentClient.
        // RouteMainChatAgent) στο ΤΕΛΕΥΤΑΙΟ turn, ώστε ένα ασαφές follow-up
        // μήνυμα (π.χ. "ναι", "1000") να ΜΗΝ ξαναγυρίσει σε "general" και
        // χάσει τα item/trader/email tools ενώ η συζήτηση συνεχίζεται στο
        // ΙΔΙΟ domain. Καμία άλλη κουρτίνα δεν χρειάζεται τέτοιο state -
        // ΗΔΗ ξέρουν το mode τους ρητά (helpMode/browserMode/emailMode/
        // courierMode flags).
        private string _lastMainChatMode;

        // Γεμίζει μετά από επιτυχή έλεγχο άδειας (JarvisLicenseGuard) - το
        // περνάμε στο AgentClient για τις κλήσεις /agent/vision.
        // ΠΟΤΕ key, μόνο opaque δείκτης.
        private string _agentAccountRef;

        // ΝΕΟ 15/08 - true μετά από επιτυχή entitlement check για το DR
        // feature (βλ. README Roadmap #6, toolName JARVISDOCREADER). ΔΙΟΡ-
        // ΘΩΘΗΚΕ 15/08: ΟΧΙ ξεχωριστό agentAccountRef (καθαρή σημαία
        // license/feature-gate) - οι AI κλήσεις του DR δρομολογούνται μέσω
        // του ΗΔΗ υπάρχοντος _agentAccountRef, ο ίδιος ο Jarvis είναι ο
        // agent, όχι ξεχωριστός. Ελέγχεται lazy (ΜΟΝΟ όταν ανοίξει η DR
        // κουρτίνα, ΟΧΙ στο αρχικό NavigationCompleted).
        private bool _drAllowed;

        // Reused ΚΑΙ για το browserView (δεύτερο WebView2, βλ. Browser mode
        // παρακάτω) - ίδιο environment/user-data-folder, ένα browser process
        // backend για τα δύο controls, όχι δύο ξεχωριστά.
        private Microsoft.Web.WebView2.Core.CoreWebView2Environment _webView2Env;
        private bool _browserViewInitialized = false;

        // ΝΕΟ 20/08 - βλ. σχόλιο στο JarvisShell_Loaded. Εικονικό domain
        // (ΔΕΝ χρειάζεται να υπάρχει πραγματικά, ούτε καν internet - το
        // WebResourceRequested filter το πιάνει πριν βγει καθόλου δίκτυο)
        // κάτω από το οποίο σερβίρονται τα embedded resources.
        private const string EmbeddedWebOrigin = "https://s1jarvis.local/";

        public JarvisShell(XSupport xSupport)
        {
            InitializeComponent();
            _xSupport = xSupport;
            Loaded += JarvisShell_Loaded;
        }

        // ΝΕΟ 20/08, ρητό αίτημα χρήστη - ενσωμάτωση index.html/vendor JS
        // ΜΕΣΑ στο S1Jarvis.dll (EmbeddedResource, βλ. csproj) αντί για
        // loose αρχεία δίπλα του, ώστε όλο το deploy να είναι ένα αρχείο.
        // Χαρτογραφεί path -> LogicalName του embedded resource (βλ.
        // <LogicalName> στο csproj - ρητά ορισμένο, ΟΧΙ βασισμένο σε
        // namespace-inference, ώστε να μην σπάει αν αλλάξει ποτέ το
        // RootNamespace). Deferral γιατί το event μπορεί να έρθει σε
        // background thread (SDK-documented pattern).
        private void ServeEmbeddedWebResource(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebResourceRequestedEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                var requestUri = new Uri(e.Request.Uri);
                // "/index.html", "/vendor/chart.umd.min.js" κ.ο.κ.
                string relativePath = requestUri.AbsolutePath.TrimStart('/');

                string resourceName;
                string contentType;
                switch (relativePath)
                {
                    case "index.html":
                        resourceName = "S1Jarvis.web.index.html";
                        contentType = "text/html; charset=utf-8";
                        break;
                    case "vendor/chart.umd.min.js":
                        resourceName = "S1Jarvis.web.vendor.chart.umd.min.js";
                        contentType = "application/javascript; charset=utf-8";
                        break;
                    default:
                        resourceName = null;
                        contentType = null;
                        break;
                }

                var asm = Assembly.GetExecutingAssembly();
                Stream stream = resourceName != null ? asm.GetManifestResourceStream(resourceName) : null;

                if (stream != null)
                {
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        stream, 200, "OK", "Content-Type: " + contentType);
                }
                else
                {
                    DebugLog.Log("ServeEmbeddedWebResource: 404 για " + relativePath +
                        " (resourceName=" + (resourceName ?? "null") + ")");
                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 404, "Not Found", "");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("ServeEmbeddedWebResource EXCEPTION: " + ex);
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 500, "Internal Server Error", "");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void JarvisShell_Loaded(object sender, RoutedEventArgs e)
        {
            DebugLog.Init(_xSupport);

            try
            {
                // ΠΡΟΣΟΧΗ: default το WebView2 φτιάχνει το user-data folder
                // ΔΙΠΛΑ στο host exe (Xplorer.exe, μέσα στο Program Files) -
                // χρειάζεται admin δικαιώματα εκεί και σκάει. Δίνουμε ρητά
                // ένα writable path στο LocalAppData του χρήστη.
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "S1Jarvis", "WebView2");

                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);
                _webView2Env = env; // reused αργότερα από το browserView (Browser mode, lazy init)

                await webView.EnsureCoreWebView2Async(env);

                // ── Lockdown του κύριου WebView2 - ρητό αίτημα χρήστη 16/08:
                //    ΚΑΝΕΝΑ "Inspect" στο δεξί-κλικ context menu, ΚΑΝΕΝΑ
                //    devtools (F12/Ctrl+Shift+I/J/C, ΚΑΙ αν ανοιχτεί εξωτερικά).
                //    ΕΠΙΤΗΔΕΣ ΜΟΝΟ στο κύριο webView - ΟΧΙ στο browserView
                //    (Browser mode, ΕΚΕΙ θέλουμε πραγματική browsing εμπειρία,
                //    βλ. README).
                //    ΣΗΜΕΙΩΣΗ: ο χρήστης θέλει ΕΠΙΣΗΣ block σε Ctrl+P/Ctrl+U/
                //    F5/Ctrl+R (print/view-source/reload) ΔΙΑΤΗΡΩΝΤΑΣ zoom ΚΑΙ
                //    Ctrl+F (εύρεση στη συζήτηση) ενεργά - ΔΕΝ γίνεται με το
                //    απλό AreBrowserAcceleratorKeysEnabled=false (μπλοκάρει
                //    ΚΑΙ zoom ΚΑΙ Ctrl+F μαζί, όλα-ή-τίποτα). Χρειάζεται
                //    CoreWebView2Controller.AcceleratorKeyPressed (per-key
                //    IsBrowserAcceleratorKeyEnabled) - η πρόσβαση στο
                //    Controller από το WPF WebView2 control ΔΕΝ επιβεβαιώθηκε
                //    ακόμα (η αντανάκλαση στο πραγματικό εγκατεστημένο DLL
                //    απέτυχε λόγω missing dependencies) - ΕΠΙΤΗΔΕΣ ΔΕΝ
                //    μαντεύουμε το API, βλ. task tracker #25/memory
                //    s1jarvis-webview2-keyboard-lockdown.
                // 20/08: ήταν προσωρινά true για το "μαύρο πλαίσιο" debugging -
                // βρέθηκε η αιτία (desktop/DWM artifact, ΕΚΤΟΣ S1Jarvis, βλ.
                // README) - γυρίζει πίσω σε false, ρητό αίτημα χρήστη 16/08
                // να είναι κλειδωμένο μόνιμα.
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;

                // ΝΕΟ 20/08, ρητό αίτημα χρήστη - "ολο το deploy να ειναι
                // ενα αρχειο": το index.html/vendor JS ΔΕΝ αντιγράφονται πια
                // σαν loose αρχεία δίπλα στο DLL (Content/
                // CopyToOutputDirectory) - είναι EmbeddedResource ΜΕΣΑ στο
                // ίδιο το S1Jarvis.dll (βλ. S1Jarvis.csproj). Το WebView2
                // δεν μπορεί να navigate απευθείας σε embedded resource -
                // σερβίρονται μέσω WebResourceRequested σε ένα εικονικό
                // https://s1jarvis.local/ domain (βλ. ServeEmbeddedWebResource
                // παρακάτω). ΙΔΙΟ idiom με το documented SetVirtualHostName*/
                // WebResourceRequested pattern του ίδιου του WebView2 SDK.
                webView.CoreWebView2.AddWebResourceRequestedFilter(
                    EmbeddedWebOrigin + "*",
                    Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
                webView.CoreWebView2.WebResourceRequested += ServeEmbeddedWebResource;

                webView.Source = new Uri(EmbeddedWebOrigin + "index.html");

                // Browser mode - native address bar (βλ. README, JarvisShell.xaml)
                // - wiring σε C#, ίδιο idiom με τα υπόλοιπα events σε αυτή τη
                // μέθοδο (όχι Click="..." μέσα στο XAML).
                browserGoBtn.Click += (s2, e2) => NavigateBrowserFromAddressBar();
                browserAddressBar.KeyDown += (s2, e2) =>
                {
                    if (e2.Key == System.Windows.Input.Key.Enter) NavigateBrowserFromAddressBar();
                };
                browserBackBtn.Click += (s2, e2) =>
                {
                    if (browserView.CoreWebView2 != null && browserView.CoreWebView2.CanGoBack)
                        browserView.CoreWebView2.GoBack();
                };
                browserCloseBtn.Click += (s2, e2) => CloseBrowserPane();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Αδυναμία αρχικοποίησης WebView2: " + ex.Message,
                    "S1Jarvis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ΚΡΙΣΙΜΟ (ίδιος λόγος με το σχόλιο πάνω από το
        // CoreWebView2_WebMessageReceived παρακάτω): αυτό είναι async void
        // (το WebView2 event το απαιτεί) - ΟΠΟΙΑΔΗΠΟΤΕ exception διαφύγει
        // από εδώ ΔΕΝ πιάνεται από κανένα εξωτερικό try/catch, ρίχνει
        // ΟΛΟΚΛΗΡΟ το Soft1 (EExternalException, όχι καθαρό .NET crash
        // dialog). ΒΡΕΘΗΚΕ 17/08 σε "καθαρό" μηχάνημα: αν το _xSupport
        // είναι null (π.χ. διπλό-φορτωμένο S1Jarvis.dll από δύο paths -
        // JarvisCore.XSupport γεμίζει στο ένα αντίγραφο assembly, το
        // JarvisShell στιγματοποιείται από το άλλο), το
        // JarvisLicenseGuard.CheckAccessSilent σκάει με NullReferenceException
        // ΠΡΙΝ καν φτάσει στο Nexus (xSupport.ConnectionInfo, όχι το HTTP
        // call - εκείνο έχει ήδη δικό του try/catch μέσα στο DoCheck).
        private async void CoreWebView2_NavigationCompleted(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                if (!e.IsSuccess) return;

                if (_xSupport == null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(
                        "window.setDisabled(\"Ο Jarvis δεν μπόρεσε να αρχικοποιηθεί σωστά " +
                        "(λείπει το context του Soft1). Δοκιμάστε επανεκκίνηση του Soft1 " +
                        "ή επικοινωνήστε με τον διαχειριστή.\");");
                    return;
                }

                // ── Έλεγχος άδειας (Nexus) πριν ενεργοποιηθεί το chat ──────────
                // Task.Run wrap: το JarvisLicenseGuard.CheckAccessSilent είναι
                // ΣΚΟΠΙΜΑ blocking/sync (ίδιο μοτίβο με S1Courier), αλλά εδώ
                // είμαστε ήδη σε async context, οπότε δεν θέλουμε να παγώσει το
                // UI thread όσο περιμένει το Nexus να απαντήσει.
                var access = await Task.Run(() => JarvisLicenseGuard.CheckAccessSilent(_xSupport));

                if (!access.Allowed)
                {
                    string denyMsg = JarvisLicenseGuard.BuildMessage(access);
                    string escapedDeny = JsEscape(denyMsg);
                    await webView.CoreWebView2.ExecuteScriptAsync($"window.setDisabled(\"{escapedDeny}\");");
                    return;
                }

                _agentAccountRef = access.AgentAccountRef;

                string name = GetDisplayName();
                string greeting = name != null
                    ? $"Γεια σου, {name}! Πώς μπορώ να βοηθήσω;"
                    : "Πώς μπορώ να βοηθήσω;";

                await webView.CoreWebView2.ExecuteScriptAsync($"window.setGreeting(\"{JsEscape(greeting)}\");");
            }
            catch (Exception ex)
            {
                try
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(
                        $"window.setDisabled(\"{JsEscape("Σφάλμα αρχικοποίησης Jarvis: " + ex.Message)}\");");
                }
                catch { /* αν σκάσει κι αυτό, δεν έχουμε πού αλλού να το δείξουμε - απλά μην αφήσεις να ξεφύγει */ }
            }
        }

        // JSON-escape το ελάχιστο απαραίτητο (quotes/backslashes/newlines)
        // ώστε να περάσει με ασφάλεια σαν JS string literal μέσα σε ExecuteScriptAsync.
        private static string JsEscape(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
                     .Replace("\r", "").Replace("\n", "\\n");

        // Sentinel string (όχι κανονικό chat text) που στέλνει το JS όταν ο
        // χρήστης πατήσει το κουμπί "Stop" ενώ περιμένει - βλ. index.html
        // stopThinking(). Πρέπει να ταιριάζει ακριβώς με το JS.
        private const string StopSentinel = "__JARVIS_STOP__";

        // ΚΡΙΣΙΜΟ: αυτός είναι async void (το WebView2 event το απαιτεί) - μια
        // exception που διαφεύγει από async void ΔΕΝ μπορεί να πιαστεί από
        // ΚΑΝΕΝΑ εξωτερικό try/catch (δεν υπάρχει Task να την κουβαλήσει).
        // Σε αυτό το hosting stack (Delphi VCL host → in-process .NET DLL) το
        // αποτέλεσμα δεν είναι ένα καθαρό .NET crash dialog αλλά ένα ωμό
        // "External exception"/EExternalException από το ίδιο το Soft1 -
        // δηλαδή σκάει ΟΛΟΚΛΗΡΟ το Soft1, όχι μόνο ο Jarvis. Γι' αυτό ΟΛΟ το
        // σώμα είναι μέσα σε ένα outer try/catch-all (belt-and-suspenders
        // πάνω από τα πιο ειδικά try/catch παρακάτω) - καμία exception δεν
        // πρέπει ΠΟΤΕ να ξεφύγει από εδώ.
        private async void CoreWebView2_WebMessageReceived(object sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string userText = e.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(userText)) return;

                if (userText == StopSentinel)
                {
                    // ΣΗΜΑΝΤΙΚΟ: αυτό το event handler τρέχει ΠΑΡΑΛΛΗΛΑ με το
                    // προηγούμενο invocation που ακόμα κάνει await στο
                    // AskAsync (async void handler, το WebView2 message pump
                    // δεν μπλοκάρει όσο το προηγούμενο περιμένει HTTP
                    // response) - το CancelCurrent() ακυρώνει το token
                    // εκείνου του in-flight call, που το πιάνει το
                    // catch(OperationCanceledException) στο AskAsync και
                    // επιστρέφει φιλικό μήνυμα κανονικά μέσω
                    // PostWebMessageAsString.
                    _agentClient.CancelCurrent();
                    return;
                }

                // Δομημένη εντολή (JSON, βλ. index.html postCommand) -
                // ξεχωρίζει από απλό chat text (που ο χρήστης πληκτρολογεί,
                // ποτέ δεν ξεκινάει με '{') με το πεδίο "type". Αποτυχία
                // parse ή άγνωστο "type" -> πέφτει κανονικά σε chat text.
                if (userText[0] == '{')
                {
                    JObject cmd = null;
                    try { cmd = JObject.Parse(userText); } catch { /* όχι JSON, συνέχισε σαν chat */ }

                    if (cmd != null && (string)cmd["type"] == "export")
                    {
                        await HandleExportAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "open_file")
                    {
                        OpenFile((string)cmd["path"]);
                        return;
                    }
                    // ── DR Στάδιο 4 - myDATA link (βλ. index.html
                    //    renderDrLinesPanel) - ΞΕΧΩΡΙΣΤΟ από open_file (εκείνο
                    //    ελέγχει File.Exists, ΔΕΝ κάνει για URL). ΝΕΟ 16/08.
                    if (cmd != null && (string)cmd["type"] == "open_external_url")
                    {
                        OpenExternalUrl((string)cmd["url"]);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "open_document")
                    {
                        OpenDocument(cmd);
                        return;
                    }
                    // ── DR - "trader:sodType:trdrId" link (βλ. renderInlineLinks
                    //    στο index.html) - ΞΕΧΩΡΙΣΤΟ μηχανισμό από το
                    //    open_document/"doc:" (ο συναλλασσόμενος ΔΕΝ περνάει
                    //    από SOSOURCE) - ΝΕΟ 16/08. ────────────────────────
                    if (cmd != null && (string)cmd["type"] == "open_trader")
                    {
                        OpenTrader(cmd);
                        return;
                    }
                    // ── "item:mtrlId" link (βλ. renderInlineLinks στο
                    //    index.html) - ΝΕΟ 19/08, ζωντανό bug report χρήστη
                    //    ("δεν μου έδωσε το link να ανοίξω το είδος"). ──
                    if (cmd != null && (string)cmd["type"] == "open_item")
                    {
                        OpenItem(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "dashboard_query")
                    {
                        if (string.IsNullOrEmpty(_agentAccountRef))
                        {
                            webView.CoreWebView2.PostWebMessageAsString(
                                JsonDashboardResult(
                                    "✖ Δεν υπάρχει ενεργή άδεια/agent — ξανάνοιξε τον Jarvis.",
                                    cmd["requestId"]));
                            return;
                        }
                        await HandleDashboardQueryAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "chat_with_attachment")
                    {
                        if (string.IsNullOrEmpty(_agentAccountRef))
                        {
                            webView.CoreWebView2.PostWebMessageAsString(
                                "✖ Δεν υπάρχει ενεργή άδεια/agent — ξανάνοιξε τον Jarvis.");
                            return;
                        }
                        await HandleChatWithAttachmentAsync(cmd);
                        return;
                    }
                    // "read_office_document" - ΝΕΟ 18/08, ρητό αίτημα χρήστη
                    // ("πρέπει να καταφέρει να διαβάζει word, excel..."). Το
                    // .xlsx/.docx είναι δυαδικό (ZIP) - το JS δεν μπορεί να
                    // το διαβάσει σαν κείμενο (σε αντίθεση με .txt/.csv/
                    // .json/.xml, βλ. isTextAttachmentFile στο index.html) -
                    // εδώ αποκωδικοποιούμε το base64, τρέχουμε το
                    // DocumentReaders parser, και επιστρέφουμε ΚΑΘΑΡΟ κείμενο
                    // πίσω - το JS το χειρίζεται ΜΕΤΑ ΑΚΡΙΒΩΣ σαν text
                    // attachment (ίδιο pendingAttachment.isText μονοπάτι).
                    if (cmd != null && (string)cmd["type"] == "read_office_document")
                    {
                        await HandleReadOfficeDocumentAsync(cmd);
                        return;
                    }
                    // ── Help mode (βλ. README "Help mode" ροή) - ξεχωριστός
                    //    καμβός/conversation από το κύριο chat. ─────────────
                    if (cmd != null && (string)cmd["type"] == "help_start")
                    {
                        // Καθαρά client-driven "νέα συνεδρία" - καμία κλήση
                        // API εδώ (ο πρώτος χαιρετισμός στον καμβό είναι
                        // στατικό κείμενο, βλ. index.html openHelp()).
                        _helpConversation.Clear();
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "help_message")
                    {
                        if (string.IsNullOrEmpty(_agentAccountRef))
                        {
                            webView.CoreWebView2.PostWebMessageAsString(
                                JsonHelpReply(
                                    "✖ Δεν υπάρχει ενεργή άδεια/agent — ξανάνοιξε τον Jarvis."));
                            return;
                        }
                        await HandleHelpMessageAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "help_rate")
                    {
                        HandleHelpRate(cmd);
                        return;
                    }
                    // ΝΕΟ 17/08, ρητό αίτημα χρήστη - rating για το
                    // create_order prompt log, "όπως στο Help" - ΙΔΙΟ
                    // RateQaLogSoAction (ήδη γενικό, δεν χρειάστηκε νέα
                    // backend μέθοδο), απλά νέο command type/handler.
                    if (cmd != null && (string)cmd["type"] == "rate_order_prompt")
                    {
                        HandleRateOrderPrompt(cmd);
                        return;
                    }
                    // ── Browser mode (βλ. README "Browser mode" ροή) - δεύτερο
                    //    WebView2 (browserView) αριστερά + δικός του καμβός
                    //    δεξιά (#browserCurtain, μέσα στο ΙΔΙΟ webView που
                    //    απλά συρρικνώνεται σε 30% πλάτος). ─────────────────
                    if (cmd != null && (string)cmd["type"] == "browser_open")
                    {
                        // ΝΕΟ 18/08 - προαιρετικό "url" (π.χ. από το κουμπί
                        // "🔍 Tracking" του Courier voucher modal, βλ.
                        // CCCTRACKINGURL στο CCCCRPROV) - ανοίγει ΚΑΤΕΥΘΕΙΑΝ
                        // στη σελίδα tracking αντί για άδειο/προηγούμενο URL.
                        OpenBrowserPane((string)cmd["url"]);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "browser_close")
                    {
                        CloseBrowserPane();
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "browser_start")
                    {
                        _browserConversation.Clear();
                        return;
                    }
                    // ── DR (Document Reader) mode - βλ. README Roadmap #6 ────
                    //    Στάδιο 1: μόνο trigger + entitlement check, καμία
                    //    upload/εξαγωγή λογική ακόμα. ─────────────────────────
                    if (cmd != null && (string)cmd["type"] == "dr_start")
                    {
                        await HandleDrStartAsync();
                        return;
                    }
                    // ── DR - Στάδιο 3α: ταυτοποίηση εκδότη (βλ. index.html
                    //    drProcessBtn) - ΝΕΟ 16/08. ─────────────────────────
                    if (cmd != null && (string)cmd["type"] == "dr_identify_issuer")
                    {
                        await HandleDrIdentifyIssuerAsync(cmd);
                        return;
                    }
                    // ── DR - Στάδιο 3γ: ΑΑΔΕ auto-create όταν ΔΕΝ βρέθηκε
                    //    συναλλασσόμενος (βλ. index.html "Δημιουργία νέου
                    //    Προμηθευτή" κουμπί) - ΝΕΟ 16/08. ─────────────────
                    if (cmd != null && (string)cmd["type"] == "dr_lookup_aade")
                    {
                        await HandleDrLookupAadeAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "dr_create_trader")
                    {
                        await HandleDrCreateTraderAsync(cmd);
                        return;
                    }
                    // ── CREATEAADEAFM - standalone εντολή (βλ. index.html
                    //    send()) - ΝΕΟ 16/08. ────────────────────────────
                    if (cmd != null && (string)cmd["type"] == "dr_manual_lookup")
                    {
                        await HandleDrManualLookupAsync(cmd);
                        return;
                    }
                    // ── DR - Στάδιο 4: εξαγωγή γραμμών (βλ. index.html
                    //    "Εξαγωγή γραμμών" κουμπί) - ΝΕΟ 16/08. ──────────
                    if (cmd != null && (string)cmd["type"] == "dr_extract_lines")
                    {
                        await HandleDrExtractLinesAsync(cmd);
                        return;
                    }
                    // ── DR - Στάδιο 5 (#22): καταχώρηση παραστατικού + αυτόματο
                    //    άνοιγμα (βλ. index.html "Καταχώρηση" κουμπί) - ΝΕΟ 16/08.
                    if (cmd != null && (string)cmd["type"] == "dr_register_document")
                    {
                        await HandleDrRegisterDocumentAsync(cmd);
                        return;
                    }
                    // ── DR - semi-manual οδηγός (βλ. index.html
                    //    renderDrManualWizard) - ΝΕΟ 16/08. Τρία lookups, ίδιο
                    //    idiom με τα υπόλοιπα dr_* commands.
                    if (cmd != null && (string)cmd["type"] == "dr_get_series_for_sosource")
                    {
                        await HandleDrGetSeriesForSosourceAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "dr_search_items")
                    {
                        await HandleDrSearchItemsAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "dr_get_trader_known_items")
                    {
                        await HandleDrGetTraderKnownItemsAsync(cmd);
                        return;
                    }
                    // ── TASK wizard - φόρμα δημιουργίας CRM task, ΕΚΤΟΣ chat/AI
                    //    (deterministic, βλ. session notes 15/08). ─────────────
                    if (cmd != null && (string)cmd["type"] == "task_search_trader")
                    {
                        HandleTaskSearchTrader(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "task_search_user")
                    {
                        HandleTaskSearchUser(cmd);
                        return;
                    }
                    // ΝΕΟ 16/08, ρητό αίτημα χρήστη - Εγκατάσταση/Έργο.
                    if (cmd != null && (string)cmd["type"] == "task_search_inst")
                    {
                        HandleTaskSearchInst(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "task_search_prjc")
                    {
                        HandleTaskSearchPrjc(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "task_create")
                    {
                        HandleTaskCreate(cmd);
                        return;
                    }
                    // ── TASKS wizard (πολλαπλοί τύποι CRM) - ΝΕΟ 15/08 ───────
                    if (cmd != null && (string)cmd["type"] == "task_get_series")
                    {
                        HandleTaskGetSeries(cmd);
                        return;
                    }
                    // ── Dashboard "Tasks - Εργασίες" σελίδα - ΝΕΟ 16/08. ΔΙΟΡΘΩΘΗΚΕ
                    //    16/08 (ζωντανό ContextSwitchDeadlock, χρήστης εντόπισε):
                    //    το XSupport call πρέπει σε Task.Run (background thread),
                    //    ΟΧΙ συγχρονισμένο στο UI thread - ίδιο idiom με
                    //    HandleDrExtractLinesAsync. ──────────────────────────
                    if (cmd != null && (string)cmd["type"] == "dashboard_get_my_tasks")
                    {
                        await HandleDashboardGetMyTasksAsync();
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "dashboard_complete_task")
                    {
                        await HandleDashboardCompleteTaskAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "dashboard_open_crm_action")
                    {
                        HandleDashboardOpenCrmAction(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "task_create_advanced")
                    {
                        HandleTaskCreateAdvanced(cmd);
                        return;
                    }
                    // ── CLEAR/CLEARS - καθαρισμός κύριου chat (client-side
                    //    εντολές, βλ. index.html send()) - ΝΕΟ 16/08.
                    //    "CLEARS" στέλνει ΠΡΩΤΑ save_transcript (JS build-άρει
                    //    το markdown από το ήδη-ορατό transcript, βλ. εκεί),
                    //    ΜΕΤΑ chat_clear - και τα δύο commands, ΟΧΙ ένα μαζί,
                    //    ώστε το "CLEAR" απλό να μην αγγίζει καθόλου το δίσκο. ─
                    if (cmd != null && (string)cmd["type"] == "save_transcript")
                    {
                        HandleSaveTranscript(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "chat_clear")
                    {
                        _conversation.Clear();
                        DebugLog.Log("[chat] chat_clear - η ιστορία μηδενίστηκε.");
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "browser_message")
                    {
                        if (string.IsNullOrEmpty(_agentAccountRef))
                        {
                            webView.CoreWebView2.PostWebMessageAsString(
                                JsonBrowserReply(
                                    "✖ Δεν υπάρχει ενεργή άδεια/agent — ξανάνοιξε τον Jarvis."));
                            return;
                        }
                        await HandleBrowserMessageAsync(cmd);
                        return;
                    }
                    // ── Email curtain (βλ. README Roadmap #1, index.html
                    //    #emailCurtain) - ΚΑΘΑΡΟ WebView2 curtain, ΧΩΡΙΣ native
                    //    pane (ΟΧΙ σαν Browser mode) - ίδιο idiom με
                    //    Dashboard/Help/DR. ΝΕΟ 17/08. ───────────────────────
                    if (cmd != null && (string)cmd["type"] == "email_start")
                    {
                        _emailConversation.Clear();
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "email_message")
                    {
                        if (string.IsNullOrEmpty(_agentAccountRef))
                        {
                            webView.CoreWebView2.PostWebMessageAsString(
                                JsonEmailReply(
                                    "✖ Δεν υπάρχει ενεργή άδεια/agent — ξανάνοιξε τον Jarvis."));
                            return;
                        }
                        await HandleEmailMessageAsync(cmd);
                        return;
                    }
                    // Calendar tab (email curtain) - ΝΕΟ 17/08, βλ. README
                    // Roadmap #1 (task #32/#33). Deterministic UI, ΟΧΙ AI -
                    // ίδιο idiom με dashboard_get_my_tasks.
                    if (cmd != null && (string)cmd["type"] == "email_get_calendar")
                    {
                        await HandleEmailGetCalendarAsync(cmd);
                        return;
                    }
                    // Email tab (email curtain) - ΝΕΟ 17/08, βλ. README
                    // Roadmap #1 (task #35). Deterministic UI (date filter +
                    // "Ανανέωση"), ΟΧΙ AI - ίδιο idiom με email_get_calendar.
                    if (cmd != null && (string)cmd["type"] == "email_get_inbox")
                    {
                        await HandleEmailGetInboxAsync(cmd);
                        return;
                    }
                    // Email tab - double-click "σαν να είναι Outlook" Modal
                    // (ΝΕΟ 17/08, ρητό αίτημα χρήστη). Deterministic, ΟΧΙ AI.
                    if (cmd != null && (string)cmd["type"] == "email_get_detail")
                    {
                        await HandleEmailGetDetailAsync(cmd);
                        return;
                    }
                    // Email tab - "⬇ Όλα"/κλικ σε ΕΝΑ συνημμένο (ΝΕΟ 17/08,
                    // ρητό αίτημα χρήστη) - ΞΑΝΑΧΡΗΣΙΜΟΠΟΙΕΙ ΑΥΤΟΥΣΙΟ το ήδη
                    // υπάρχον JarvisEmailAccess.ExecuteDownloadEmailAttachment
                    // (ίδιο ΑΚΡΙΒΩΣ tool με το chat) - ΕΔΩ όμως καλείται
                    // ΑΠΕΥΘΕΙΑΣ (deterministic, χωρίς Claude/AskAsync round-
                    // trip) - ΙΔΙΟ input σχήμα (messageId/attachmentName).
                    if (cmd != null && (string)cmd["type"] == "email_download_attachment")
                    {
                        await HandleEmailDownloadAttachmentDirectAsync(cmd);
                        return;
                    }
                    // "✎ Νέο email" / "↩ Απάντηση" - ΝΕΟ 18/08, ρητό αίτημα
                    // χρήστη ("θα πρέπει να το βάλουμε να στέλνει email",
                    // "τα tools θα πρέπει να δουλεύουν και μέσα από την
                    // κουρτίνα με κουμπιά ... και βασικά ακόμα και χωρίς
                    // εντολή"). Deterministic - ΧΩΡΙΣ Claude/AskAsync
                    // ενδιάμεσο, ίδιο idiom με email_download_attachment. Η
                    // ΙΔΙΑ η φόρμα (συμπλήρωση + κλικ "Αποστολή") ΕΙΝΑΙ η
                    // επιβεβαίωση - καμία επιπλέον, σε αντίθεση με το
                    // send_email/reply_email chat tool (βλ. JarvisAgentClient
                    // BuildSystemPrompt, υποχρεωτική επιβεβαίωση ΕΚΕΙ).
                    if (cmd != null && (string)cmd["type"] == "email_compose_send")
                    {
                        await HandleEmailComposeSendAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "email_reply_send")
                    {
                        await HandleEmailReplySendAsync(cmd);
                        return;
                    }
                    // ── Courier curtain (ΝΕΟ 17/08, ρητό αίτημα χρήστη -
                    //    JARVISCOURIER feature) - ίδιο entitlement idiom με
                    //    DR (lazy check στο courier_start), ίδιο chat idiom
                    //    με Email (courierMode: true). ─────────────────────
                    if (cmd != null && (string)cmd["type"] == "courier_start")
                    {
                        await HandleCourierStartAsync();
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "courier_message")
                    {
                        if (!_courierAllowed)
                        {
                            webView.CoreWebView2.PostWebMessageAsString(
                                JsonCourierReply("✖ Δεν έχεις πρόσβαση σε αυτό το εργαλείο."));
                            return;
                        }
                        if (string.IsNullOrEmpty(_agentAccountRef))
                        {
                            webView.CoreWebView2.PostWebMessageAsString(
                                JsonCourierReply("✖ Δεν υπάρχει ενεργή άδεια/agent — ξανάνοιξε τον Jarvis."));
                            return;
                        }
                        await HandleCourierMessageAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "courier_open_document")
                    {
                        HandleCourierOpenDocument(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "courier_load_voucher_form")
                    {
                        await HandleCourierLoadVoucherFormAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "courier_create_voucher")
                    {
                        await HandleCourierCreateVoucherAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "courier_get_voucher_pdf")
                    {
                        await HandleCourierGetVoucherPdfAsync(cmd);
                        return;
                    }
                    if (cmd != null && (string)cmd["type"] == "courier_cancel_voucher")
                    {
                        await HandleCourierCancelVoucherAsync(cmd);
                        return;
                    }
                }

                if (string.IsNullOrEmpty(_agentAccountRef))
                {
                    // PostWebMessageAsString είναι sync/void (fire-and-forget) -
                    // δεν επιστρέφει Task, δεν κάνει await.
                    webView.CoreWebView2.PostWebMessageAsString(
                        "✖ Δεν υπάρχει ενεργή άδεια/agent — ξανάνοιξε τον Jarvis.");
                    return;
                }

                try
                {
                    string answer = await _agentClient.AskAsync(
                        _agentAccountRef, _xSupport, _conversation, userText,
                        onProgress: t => webView.CoreWebView2.PostWebMessageAsString(JsonThinkingUpdate(t)),
                        // ΝΕΟ 18/08, ρητό αίτημα χρήστη ("εντολή που θα δουλεύει
                        // περιγραφικά ... θα επιστρέφει σε modal τα στοιχεία της
                        // επαφής") - postMessage στο κύριο παράθυρο, ΟΧΙ στο chat.
                        onShowContactResults: contacts => webView.CoreWebView2.PostWebMessageAsString(
                            new JObject { ["type"] = "show_contact_results_data", ["contacts"] = contacts }.ToString(Formatting.None)),
                        // ΝΕΟ 18/08, ρητό αίτημα χρήστη - "bulk import ειδών από
                        // αρχείο" (π.χ. τιμοκατάλογος xlsx με πολλές γραμμές,
                        // βλ. index.html loadTextAttachment/read_office_document)
                        // χρειάζεται πολλά tool calls στη σειρά - ParamCode
                        // 500028, default 40 (ΧΩΡΙΣ κόστος για κανονικές
                        // συζητήσεις, βλ. σχόλιο στο AskAsync).
                        maxIterations: JarvisTools.GetCrmTaskOptionalParam(_xSupport, 500028, 40),
                        // ΝΕΟ 19/08, agent-clustering restructuring: sticky
                        // routing - βλ. σχόλιο στο _lastMainChatMode.
                        routingHint: _lastMainChatMode,
                        onModeChosen: mode => _lastMainChatMode = mode,
                        // ΔΙΟΡΘΩΘΗΚΕ 19/08 - ζωντανή διευκρίνιση χρήστη: "σε
                        // εκείνο το σημείο έχει φτιάξει το αρχείο και ξέρει
                        // και σε ποιο path" - ΠΡΑΓΜΑΤΙΚΟ round-trip πλέον
                        // (ΟΧΙ fire-and-forget όπως πριν). window.
                        // triggerTableExport επιστρέφει Promise<string|null>
                        // (το path, ή null αν απέτυχε/δεν υπήρχε πίνακας) -
                        // το ExecuteScriptAsync ΠΕΡΙΜΕΝΕΙ την Promise και
                        // επιστρέφει το JSON-serialized αποτέλεσμα
                        // (WebView2 native συμπεριφορά για async JS
                        // functions). Ξετυλίγουμε το JSON string πίσω σε
                        // κανονικό C# string πριν το επιστρέψουμε στον
                        // Jarvis (βλ. JarvisTools.ExecuteExportShownTable).
                        // ΝΕΟ 19/08, ρητό αίτημα χρήστη ("επιλογή γραμμών μέσω
                        // οδηγίας") - rowIndices (int[]/null) περνάει σαν JSON
                        // array literal στο JS (π.χ. "[0,1,2]" ή "null") -
                        // ασφαλές, ο Jarvis ΔΕΝ ελέγχει το κείμενο εδώ (πάντα
                        // ακέραιοι μέσα από το JSON schema του tool).
                        onExportShownTable: async (format, rowIndices) =>
                        {
                            try
                            {
                                string rowIndicesJson = rowIndices != null
                                    ? JsonConvert.SerializeObject(rowIndices) : "null";
                                string raw = await webView.CoreWebView2.ExecuteScriptAsync(
                                    $"window.triggerTableExport(\"{JsEscape(format)}\", {rowIndicesJson})");
                                return JsonConvert.DeserializeObject<string>(raw);
                            }
                            catch (Exception ex)
                            {
                                DebugLog.Log("[export_shown_table] EXCEPTION: " + ex);
                                return null;
                            }
                        });
                    webView.CoreWebView2.PostWebMessageAsString(answer);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[chat] EXCEPTION: " + ex);
                    webView.CoreWebView2.PostWebMessageAsString("✖ Σφάλμα: " + ex.Message);
                }
            }
            catch (Exception outerEx)
            {
                // Τελευταία γραμμή άμυνας - βλ. σχόλιο πάνω από τη μέθοδο.
                // Ακόμα κι αυτό το PostWebMessageAsString τυλίγεται, ώστε να
                // ΜΗΝ ξαναπετάξει exception που θα ξέφευγε τελικά.
                DebugLog.Log("[WebMessageReceived] UNHANDLED (καταπιέστηκε): " + outerEx);
                try
                {
                    webView.CoreWebView2.PostWebMessageAsString(
                        "✖ Απρόσμενο σφάλμα - δοκίμασε ξανά ή ξανάνοιξε τον Jarvis.");
                }
                catch { /* ακόμα κι αυτό αν σκάσει, δεν το αφήνουμε να ξεφύγει */ }
            }
        }

        // ── Export (xlsx/csv/pdf) - βλ. index.html exportBlocks/requestExport ──
        //
        // ΙΣΤΟΡΙΚΟ (μη το ξαναδοκιμάσεις χωρίς λόγο): η πρώτη υλοποίηση
        // άνοιγε SaveFileDialog (πρώτα WPF/Microsoft.Win32, μετά WinForms) για
        // να διαλέξει ο χρήστης τοποθεσία - ΚΑΙ ΤΑ ΔΥΟ έκαναν crash ολόκληρο
        // το Soft1 (native "External exception/EExternalException" από το
        // Delphi host, βλ. session notes). Δύο διαφορετικές υλοποιήσεις
        // dialog, ίδιο αποτέλεσμα -> το κοινό τους σημείο (ShowDialog() μέσα
        // στο WebView2 WebMessageReceived callback, πιθανό COM/RPC
        // reentrancy conflict με το native message loop) είναι το πρόβλημα,
        // όχι το συγκεκριμένο dialog API. Λύση: ΚΑΝΕΝΑ modal dialog εδώ -
        // αποθήκευση απευθείας σε σταθερό φάκελο (Έγγραφα\Jarvis Exports),
        // η τοποθεσία αναφέρεται στο chat.
        //
        // xlsx/csv: το JS στέλνει ήδη δομημένες γραμμές (string[][], κάρτα +
        //   πίνακας ενοποιημένα) - γράφουμε απευθείας (XlsxWriter/CsvWriter,
        //   καθαρό .NET, χωρίς εξωτερικό NuGet).
        // pdf: το JS έχει ήδη γεμίσει το κρυφό #printArea με το ίδιο styled
        //   HTML (κάρτα+πίνακας) - το CoreWebView2.PrintToPdfAsync τυπώνει τη
        //   ΤΡΕΧΟΥΣΑ σελίδα μέσω Chromium print pipeline, που με το @media
        //   print CSS δείχνει ΜΟΝΟ το #printArea (βλ. index.html). Καμία
        //   PDF βιβλιοθήκη δεν χρειάζεται - το WebView2 το κάνει έτοιμο.
        // ΔΙΟΡΘΩΘΗΚΕ 18/08, ρητή αναφορά χρήστη ("όταν τα πατάω δεν
        // εμφανίζει Link με το αρχείο"): πριν έστελνε ΩΜΟ κείμενο - το
        // γενικό fallback του index.html message listener οδηγεί ΚΑΘΕ
        // μη-typed μήνυμα ΠΑΝΤΑ στο ΚΥΡΙΟ chat, ΑΝΕΞΑΡΤΗΤΑ από ποια
        // κουρτίνα ζήτησε το export (Browser/Email/Courier/Help) - το
        // link "χανόταν" πίσω από την κουρτίνα. Τώρα JSON-typed
        // ("export_result" + source), βλ. index.html routeMap.
        private async Task HandleExportAsync(JObject cmd)
        {
            string format = (string)cmd["format"];
            string filename = (string)cmd["filename"];
            if (string.IsNullOrWhiteSpace(filename)) filename = "Jarvis_export";
            // "main" default - συμβατό ΚΑΙ με τυχόν παλιότερο caller χωρίς
            // 'source' (δεν υπάρχει σήμερα, αλλά defensive).
            string source = (string)cmd["source"] ?? "main";
            // ΝΕΟ 19/08 - βλ. window.triggerTableExport/pendingExportRequests
            // στο index.html: όταν το export ξεκίνησε από tool-call (ΟΧΙ
            // κλικ κουμπιού), ταξιδεύει πίσω ΑΥΤΟΥΣΙΟ ώστε το JS να
            // resolve-άρει τη σωστή Promise με το πραγματικό path.
            string requestId = (string)cmd["requestId"];

            DebugLog.Log($"[export] format={format} filename={filename} source={source} requestId={requestId}");

            try
            {
                string ext = format == "xlsx" ? ".xlsx" : format == "csv" ? ".csv"
                    : format == "pdf" ? ".pdf" : null;
                if (ext == null) return; // άγνωστο format, αγνόησε αθόρυβα

                string path = BuildExportPath(filename, ext);

                switch (format)
                {
                    case "csv":
                        CsvWriter.Write(path, ParseExportRows(cmd));
                        break;
                    case "xlsx":
                        XlsxWriter.Write(path, ParseExportRows(cmd));
                        break;
                    case "pdf":
                        var printSettings = webView.CoreWebView2.Environment.CreatePrintSettings();
                        await webView.CoreWebView2.PrintToPdfAsync(path, printSettings);
                        break;
                }

                DebugLog.Log("[export] OK -> " + path);
                // Mini-markdown link "[όνομα](path)" - το index.html το
                // ρεντεράρει σαν clickable <a>, click -> postCommand
                // {"type":"open_file","path":...} -> OpenFile() παρακάτω.
                PostExportResult(source, $"✅ Αποθηκεύτηκε: [{Path.GetFileName(path)}]({path})", path, requestId);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[export] EXCEPTION: " + ex);
                PostExportResult(source, "✖ Αποτυχία εξαγωγής: " + ex.Message, null, requestId);
            }
        }

        private void PostExportResult(string source, string text, string path = null, string requestId = null) =>
            webView.CoreWebView2.PostWebMessageAsString(new JObject
            {
                ["type"] = "export_result",
                ["source"] = source,
                ["text"] = text,
                ["path"] = path,
                ["requestId"] = requestId
            }.ToString(Formatting.None));

        // Άνοιγμα του exported αρχείου με το προεπιλεγμένο πρόγραμμα του
        // λειτουργικού (Excel/Adobe/κ.λπ.) - βλ. index.html .file-link click.
        // ΣΗΜΑΝΤΙΚΟ (σε αντίθεση με τα SaveFileDialog που έκαναν crash, βλ.
        // ιστορικό export πιο πάνω): το Process.Start ΔΕΝ ανοίγει δικό του
        // nested message loop μέσα στο thread μας - απλά ξεκινάει ΝΕΟ
        // process (CreateProcess/ShellExecuteEx) και επιστρέφει αμέσως, άρα
        // δεν έχει το ίδιο ρίσκο COM/reentrancy conflict μέσα στο WebView2
        // callback. Παρόλα αυτά - να επιβεβαιωθεί ζωντανά πριν θεωρηθεί
        // δεδομένο (ήταν ήδη σημειωμένο ως προσοχή στο README backlog).
        private void OpenFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    webView.CoreWebView2.PostWebMessageAsString(
                        "✖ Το αρχείο δεν βρέθηκε: " + path);
                    return;
                }

                DebugLog.Log("[open_file] " + path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DebugLog.Log("[open_file] EXCEPTION: " + ex);
                try
                {
                    webView.CoreWebView2.PostWebMessageAsString(
                        "✖ Αδυναμία ανοίγματος αρχείου: " + ex.Message);
                }
                catch { /* δεν το αφήνουμε να ξεφύγει */ }
            }
        }

        // myDATA link (βλ. DR Στάδιο 4, "aade_link" στο extraction JSON) -
        // ΞΕΧΩΡΙΣΤΟ από OpenFile (εκείνο ελέγχει File.Exists, ΔΕΝ κάνει για
        // URL). Το link προέρχεται από AI εξαγωγή (ΟΧΙ απευθείας από τον
        // χειριστή) - ρητός έλεγχος scheme (ΜΟΝΟ http/https, absolute URI)
        // ΠΡΙΝ το Process.Start(UseShellExecute=true), ώστε ΚΑΝΕΝΑ
        // hallucinated/adversarial "link" (π.χ. τοπικό .exe path,
        // javascript:, κ.λπ.) να μην μπορεί ποτέ να εκτελεστεί μέσω shell
        // association - ίδιο σκεπτικό ασφάλειας με τον έλεγχο τύπου
        // αρχείου στο AskAsync attachment.
        private void OpenExternalUrl(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    webView.CoreWebView2.PostWebMessageAsString(
                        "✖ Μη έγκυρο ή μη ασφαλές link: " + url);
                    return;
                }

                DebugLog.Log("[open_external_url] " + uri);
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DebugLog.Log("[open_external_url] EXCEPTION: " + ex);
                try
                {
                    webView.CoreWebView2.PostWebMessageAsString(
                        "✖ Αδυναμία ανοίγματος link: " + ex.Message);
                }
                catch { /* δεν το αφήνουμε να ξεφύγει */ }
            }
        }

        // Κλικ σε .doc-link (βλ. index.html renderInlineLinks/postCommand
        // open_document, "doc:SOSOURCE:ID" links) - ανοίγει ΑΠΕΥΘΕΙΑΣ την
        // οθόνη ενός ΥΠΑΡΧΟΝΤΟΣ παραστατικού μέσα στο Soft1. mode=locate
        // ΠΑΝΤΑ εδώ (το κλικ έρχεται από ήδη υπάρχουσα εγγραφή σε λίστα,
        // ΟΧΙ από δημιουργία νέας) - reuse ΤΟΥ ΙΔΙΟΥ JarvisTools.
        // ExecuteOpenDocument που χρησιμοποιεί και το AI tool, μία πηγή
        // αλήθειας για το SOSOURCE -> object name mapping.
        //
        // ΔΙΟΡΘΩΘΗΚΕ 15/08 (hang - χρειάστηκε kill από Task Manager): το
        // ExecS1Command ανοίγει ΝΑΤΙΒΟ Soft1 παράθυρο (Designer object) - αν
        // κληθεί ΣΥΓΧΡΟΝΑ μέσα στο ΙΔΙΟ WebMessageReceived callback (ΧΩΡΙΣ
        // κανένα await ανάμεσα, σε αντίθεση με το AI tool-call path που
        // περνάει πρώτα από await CallProxyAsync), είναι ΑΚΡΙΒΩΣ το ίδιο
        // reentrancy πρόβλημα με το ιστορικό SaveFileDialog crash (βλ.
        // σχόλιο στο HandleExportAsync πιο κάτω - "ΚΑΝΕΝΑ modal dialog εδώ",
        // COM/RPC conflict με το native message loop του Soft1/Delphi host)
        // - απλά εδώ εκδηλώνεται σαν hang αντί για exception (το log
        // επιβεβαιώνει: το DebugLog.Log ΜΕΤΑ το ExecS1Command στο
        // JarvisTools.ExecuteOpenDocument ΔΕΝ προλάβαινε ποτέ να τυπωθεί).
        // Dispatcher.BeginInvoke αναβάλλει την κλήση ΜΕΤΑ την επιστροφή από
        // το τρέχον callback, ώστε το native message loop να "ανασάνει"
        // πρώτα - ίδιο σκεπτικό, διαφορετικό εργαλείο από το Process.Start
        // (νέο process) που έλυσε το ιστορικό πρόβλημα του export.
        private void OpenDocument(JObject cmd)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var input = new JObject
                    {
                        ["sosource"] = cmd["sosource"],
                        ["mode"] = "locate",
                        ["id"] = cmd["id"]
                    };
                    JarvisTools.ExecuteOpenDocument(_xSupport, input);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[open_document] EXCEPTION: " + ex);
                    try
                    {
                        webView.CoreWebView2.PostWebMessageAsString(
                            "✖ Αδυναμία ανοίγματος παραστατικού: " + ex.Message);
                    }
                    catch { /* δεν το αφήνουμε να ξεφύγει */ }
                }
            }));
        }

        // Άνοιγμα κάρτας συναλλασσόμενου (βλ. "trader:" link, index.html
        // renderInlineLinks) - ΙΔΙΟ Dispatcher.BeginInvoke reentrancy fix με
        // το OpenDocument πιο πάνω (ίδιο ExecS1Command call, ίδιος κίνδυνος
        // hang αν κληθεί συγχρονισμένα μέσα στο WebMessageReceived).
        private void OpenTrader(JObject cmd)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    string objectName = (string)cmd["objectName"];
                    int trdrId = (int?)cmd["trdrId"] ?? 0;
                    JarvisTools.ExecuteOpenTrader(_xSupport, objectName, trdrId);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[open_trader] EXCEPTION: " + ex);
                    try
                    {
                        webView.CoreWebView2.PostWebMessageAsString(
                            "✖ Αδυναμία ανοίγματος συναλλασσόμενου: " + ex.Message);
                    }
                    catch { /* δεν το αφήνουμε να ξεφύγει */ }
                }
            }));
        }

        // ΝΕΟ 19/08, ζωντανό bug report χρήστη ("δεν μου έδωσε το link να
        // ανοίξω το είδος") - το ITEM Designer object (ΙΔΙΟ object που
        // χρησιμοποιεί το create_item, βλ. JarvisItems.cs) υποστηρίζει
        // AUTOLOCATE ΑΚΡΙΒΩΣ όπως τα trader objects (CUSTOMER/SUPPLIER) -
        // ΙΔΙΟ idiom με OpenTrader πιο πάνω, ΑΠΛΟΥΣΤΕΡΟ (ΕΝΑ πάντα σταθερό
        // object name "ITEM", καμία ανάγκη για objectName param όπως στο
        // trader). Βλ. "item:mtrlId" link, index.html renderInlineLinks/
        // ITEM_LINK_RE.
        private void OpenItem(JObject cmd)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    int mtrlId = (int?)cmd["mtrlId"] ?? 0;
                    JarvisItems.ExecuteOpenItem(_xSupport, mtrlId);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[open_item] EXCEPTION: " + ex);
                    try
                    {
                        webView.CoreWebView2.PostWebMessageAsString(
                            "✖ Αδυναμία ανοίγματος είδους: " + ex.Message);
                    }
                    catch { /* δεν το αφήνουμε να ξεφύγει */ }
                }
            }));
        }

        // ── Chat με επισυναπτόμενη εικόνα/PDF - βλ. index.html attachBtn/
        //    paste (Ctrl+V εικόνας μέσα στο composer) ────────────────────
        // Ίδιο ΚΑΝΑΛΙ απάντησης με το κανονικό chat (plain string, πάει
        // κατευθείαν στο transcript) - ΟΧΙ σαν το dashboard/export που
        // απαντάνε αλλιώς. Χρησιμοποιεί το ΚΟΙΝΟ _conversation ιστορικό
        // (σε αντίθεση με το dashboard) - μια εικόνα που στέλνει ο χρήστης
        // είναι μέρος της κανονικής συζήτησης, θέλουμε ο Jarvis να τη
        // θυμάται στα επόμενα μηνύματα.
        private async Task HandleChatWithAttachmentAsync(JObject cmd)
        {
            string text = (string)cmd["text"] ?? "";
            string base64 = (string)cmd["base64"];
            string mimeType = (string)cmd["mimeType"];

            try
            {
                string answer = await _agentClient.AskAsync(
                    _agentAccountRef, _xSupport, _conversation, text, base64, mimeType,
                    onProgress: t => webView.CoreWebView2.PostWebMessageAsString(JsonThinkingUpdate(t)));
                webView.CoreWebView2.PostWebMessageAsString(answer);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[attachment] EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString("✖ Σφάλμα: " + ex.Message);
            }
        }

        // Deterministic (ΧΩΡΙΣ Claude/AskAsync) - ΝΕΟ 18/08, βλ.
        // DocumentReaders.cs. Το JS περιμένει το αποτέλεσμα ασύγχρονα (ίδιο
        // idiom με email_get_detail κλπ) και μετά το χειρίζεται ΣΑΝ text
        // attachment (pendingAttachment.isText).
        // ΔΙΟΡΘΩΘΗΚΕ 18/08 (ζωντανό bug report χρήστη - "E-Abort", ρητά η
        // ΙΔΙΑ κατηγορία με το ήδη τεκμηριωμένο ιστορικό SaveFileDialog
        // crash πιο πάνω - "COM/RPC reentrancy conflict με το native
        // message loop"): η ΠΡΩΤΗ υλοποίηση ήταν sync `void`, ΧΩΡΙΣ await
        // πριν την κλήση της - το ZIP/XML parsing ενός πραγματικού .xlsx
        // έτρεχε ΣΥΓΧΡΟΝΑ πάνω στο ΙΔΙΟ (UI) thread του
        // CoreWebView2_WebMessageReceived callback (async void) - μπλόκαρε
        // το UI thread όσο διαρκούσε το parsing, ΜΕΣΑ σε COM callback από
        // τον native Delphi/Soft1 host -> ο host δεν έπαιρνε έλεγχο πίσω
        // έγκαιρα -> COM timeout/abort ("E-Abort"). Το `Task.Run` εδώ
        // μεταφέρει το ΠΡΑΓΜΑΤΙΚΟ parsing σε background thread - το
        // WebMessageReceived callback επιστρέφει (yield) στο message pump
        // ΑΜΕΣΩΣ, ίδιο σκεπτικό με ΟΛΟΥΣ τους άλλους `await HandleXAsync`
        // handlers σε αυτή την αλυσίδα (ΠΟΤΕ ξανά sync/blocking call εδώ
        // μέσα, ΑΚΟΜΑ κι αν "φαίνεται γρήγορο" - το μάθημα ισχύει ΓΕΝΙΚΑ).
        private async Task HandleReadOfficeDocumentAsync(JObject cmd)
        {
            string name = (string)cmd["name"] ?? "attachment";
            string mimeType = (string)cmd["mimeType"];
            string base64 = (string)cmd["base64"];
            try
            {
                byte[] bytes = Convert.FromBase64String(base64 ?? "");
                string text = await Task.Run(() => DocumentReaders.ReadOfficeDocumentAsText(bytes, mimeType, name));
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "office_document_text_result",
                    ["success"] = true,
                    ["name"] = name,
                    ["text"] = text
                }.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[office-doc] HandleReadOfficeDocumentAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "office_document_text_result",
                    ["success"] = false,
                    ["name"] = name,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // ── Help mode ("κουρτίνα"/καμβός) - βλ. README "Help mode" ροή,
        //    index.html #helpCurtain ────────────────────────────────────────
        // Ξεχωριστό conversation history (_helpConversation) - ΔΕΝ αγγίζει το
        // κύριο _conversation. ΔΙΟΡΘΩΘΗΚΕ 15/08: το onProgress ΤΩΡΑ
        // συνδέεται (πριν δεν συνδεόταν - λάθος σκεπτικό "το κεντρικό orb
        // είναι κρυμμένο πίσω από την κουρτίνα, άχρηστο"· ο χρήστης θέλει
        // ΤΟ ΙΔΙΟ visual feedback ΚΑΙ μέσα στον καμβό) - στέλνει
        // 'help_status' (JsonHelpStatus, ΞΕΧΩΡΙΣΤΟ type από το κεντρικό
        // 'thinking_update') στη ΔΙΚΗ ΤΗΣ μικρή σφαίρα/Legend στην
        // τίτλος-γραμμή του καμβά (#helpOrbWrap/#helpStatusCaption).
        private async Task HandleHelpMessageAsync(JObject cmd)
        {
            string text = (string)cmd["text"] ?? "";

            try
            {
                string answer = await _agentClient.AskAsync(
                    _agentAccountRef, _xSupport, _helpConversation, text, helpMode: true,
                    onProgress: t => webView.CoreWebView2.PostWebMessageAsString(JsonHelpStatus(t)));

                // Ο Jarvis έκλεισε με το marker block -> η λύση είναι
                // πλήρης. Καταγραφή στο SOACTION (learned-Q&A tier, βλ.
                // JarvisTools.CreateQaLogSoAction) και μόνο ΤΟΤΕ ενημέρωση
                // του UI με "help_solution" (πυροδοτεί ⭐ + lock chatbox).
                if (JarvisTools.TryParseQaMarker(answer, out var qa))
                {
                    try
                    {
                        int soactionId = JarvisTools.CreateQaLogSoAction(
                            _xSupport, qa.Keywords, qa.RequestSummary, qa.SolutionSteps);
                        webView.CoreWebView2.PostWebMessageAsString(
                            JsonHelpSolution(qa.SolutionSteps, soactionId));
                    }
                    catch (Exception ex)
                    {
                        // Η λύση φτάνει στον χειριστή ΚΑΝΟΝΙΚΑ ακόμα κι αν η
                        // καταγραφή στο SOACTION αποτύχει - δεν μπλοκάρουμε
                        // τη βοήθεια για αυτό (soactionId=0 -> το UI δεν
                        // δείχνει ⭐, βλ. index.html help_solution handler).
                        DebugLog.Log("[help] SOACTION INSERT EXCEPTION: " + ex);
                        webView.CoreWebView2.PostWebMessageAsString(
                            JsonHelpSolution(qa.SolutionSteps, 0));
                    }
                }
                else
                {
                    // Ενδιάμεση ερώτηση/απάντηση - το loop συνεχίζει κανονικά.
                    webView.CoreWebView2.PostWebMessageAsString(JsonHelpReply(answer));
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[help] EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonHelpReply("✖ Σφάλμα: " + ex.Message));
            }
        }

        // Βαθμολόγηση (⭐ 1-5) - UPDATE πάνω στο ήδη καταχωρημένο SOACTION id
        // (βλ. JarvisTools.RateQaLogSoAction). Σιωπηλή αποτυχία σε σφάλμα -
        // δεν αξίζει να διακόψουμε τον χειριστή για μια αποτυχημένη
        // βαθμολόγηση, μόνο DebugLog.
        private void HandleHelpRate(JObject cmd)
        {
            int soactionId = (int?)cmd["soactionId"] ?? 0;
            int rating = (int?)cmd["rating"] ?? 0;
            if (soactionId <= 0 || rating < 1 || rating > 5) return;

            try
            {
                JarvisTools.RateQaLogSoAction(_xSupport, soactionId, rating);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[help] RATE EXCEPTION: " + ex);
            }
        }

        // ΝΕΟ 17/08, ρητό αίτημα χρήστη - "να βάλουμε rating όπως στο Help".
        // ΙΔΙΟ idiom με HandleHelpRate πιο πάνω - fire-and-forget, ΔΕΝ
        // στέλνει απάντηση πίσω στο UI (τα αστέρια κλειδώνουν client-side
        // αμέσως μόλις πατηθούν, βλ. index.html .rate-star handler).
        private void HandleRateOrderPrompt(JObject cmd)
        {
            int soactionId = (int?)cmd["soactionId"] ?? 0;
            int rating = (int?)cmd["rating"] ?? 0;
            if (soactionId <= 0 || rating < 1 || rating > 5) return;

            try
            {
                JarvisTools.RateQaLogSoAction(_xSupport, soactionId, rating);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[order_entry] RATE EXCEPTION: " + ex);
            }
        }

        private static string JsonHelpReply(string text) =>
            new JObject { ["type"] = "help_reply", ["text"] = text }.ToString(Formatting.None);

        // "Legend" της μικρής σφαίρας στην τίτλος-γραμμή του Help καμβά
        // (index.html #helpOrbWrap/#helpStatusCaption) - ίδιο πνεύμα με το
        // JsonThinkingUpdate του κεντρικού chat, ξεχωριστό type ώστε να μην
        // μπερδεύεται με το κεντρικό #orbCaption.
        private static string JsonHelpStatus(string text) =>
            new JObject { ["type"] = "help_status", ["text"] = text }.ToString(Formatting.None);

        // soactionId=0 σημαίνει "η καταγραφή απέτυχε" - το UI δεν δείχνει ⭐
        // σε αυτή την περίπτωση (τίποτα να βαθμολογηθεί), βλ. index.html.
        private static string JsonHelpSolution(string text, int soactionId) =>
            new JObject
            {
                ["type"] = "help_solution",
                ["text"] = text,
                ["soactionId"] = soactionId
            }.ToString(Formatting.None);

        // ── Dashboard ("κουρτίνα") - βλ. index.html requestDashboardData ───
        //
        // Ξαναχρησιμοποιεί το ΙΔΙΟ tool-use loop (_agentClient.AskAsync) με
        // το κανονικό chat - ο Jarvis μαθαίνει το schema (top πελάτες/είδη,
        // τιμοκατάλογος) ζωντανά μέσω query_data, όπως ήδη έμαθε το
        // TRDR/FINDOC σήμερα. Η μόνη διαφορά: η απάντηση γυρνάει sealed σε
        // {"type":"dashboard_result",...} ώστε το JS να την οδηγήσει στο
        // dashboard panel αντί στο κανονικό chat transcript (βλ. message
        // listener στο index.html).
        private async Task HandleDashboardQueryAsync(JObject cmd)
        {
            string date = (string)cmd["date"];
            // Το requestId ταξιδεύει αμετάβλητο πίσω στο JS - βλ.
            // requestDashboardData/renderDashboardResult στο index.html: το
            // "Ανανέωση" κάνει ΟVERRIDE (ακυρώνει το τρέχον και ξεκινάει νέο
            // αμέσως, βλ. σχόλιο εκεί), οπότε μπορεί να υπάρχουν 2+
            // επικαλυπτόμενες κλήσεις εδώ ταυτόχρονα - το requestId λέει στο
            // JS ποια απάντηση είναι η ΤΕΛΕΥΤΑΙΑ ζητηθείσα, ώστε μια
            // καθυστερημένη απάντηση από μια ήδη ακυρωμένη κλήση να
            // αγνοηθεί αντί να αντικαταστήσει σωστά δεδομένα.
            JToken requestId = cmd["requestId"];
            DebugLog.Log($"[dashboard] date={date} requestId={requestId}");

            try
            {
                // ΑΛΛΑΓΗ 20/08, ρητό αίτημα χρήστη - "το dashboard δε θα
                // καλεί καθόλου agent παρά θα τρέχει τα queries": πριν
                // στελνόταν prompt στον πλήρη AI agent loop (BuildDashboardPrompt,
                // πολλαπλά tool-use round-trips - αργό). Τώρα: DashboardPanels
                // διαβάζει 20 cccParams slots (500040-500059, βλ. εκεί) και
                // τρέχει το SQL ΚΑΘΕ ενός απευθείας - καμία κλήση στο Claude
                // API. GetSQLDataSet είναι sync - Task.Run ώστε να ΜΗΝ
                // μπλοκάρει το UI thread όσο τρέχουν τα (έως 20) queries.
                string answer = await Task.Run(() => DashboardPanels.BuildDashboardText(_xSupport, date));

                DebugLog.Log("[dashboard] OK");
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonDashboardResult(answer ?? "", requestId));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dashboard] EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonDashboardResult("✖ Σφάλμα φόρτωσης dashboard: " + ex.Message, requestId));
            }
        }

        // ΑΦΑΙΡΕΘΗΚΕ 20/08 - BuildDashboardPrompt (το παλιό AI-prompt για τα
        // 4 dashboard panels) δεν χρησιμοποιείται πια, βλ. DashboardPanels.cs/
        // HandleDashboardQueryAsync πιο πάνω (deterministic SQL, όχι agent).
        // Το ΠΕΡΙΕΧΟΜΕΝΟ του (τι υπολογίζει κάθε panel) μεταφέρθηκε στο
        // PARAMS.md σαν οδηγός για το SQL των ParamCode 500040-500043.

        private static string JsonDashboardResult(string text, JToken requestId) =>
            new JObject { ["type"] = "dashboard_result", ["text"] = text, ["requestId"] = requestId }
                .ToString(Formatting.None);

        // ΑΦΑΙΡΕΘΗΚΕ 20/08 - JsonDashboardStatus (dashboard_status message)
        // δεν χρησιμοποιείται πια: το dashboard δεν καλεί agent (βλ.
        // DashboardPanels.cs), άρα δεν υπάρχουν πια ενδιάμεσα "thinking"
        // βήματα να αναφερθούν - το query τρέχει synchronous/γρήγορα.

        // "Legend" κάτω από το orb (index.html #orbCaption) - καλείται σαν
        // onProgress callback του AgentClient.AskAsync, ΜΙΑ φορά ανά
        // ενδιάμεσο iteration του tool-use loop (βλ. JarvisAgentClient.
        // BuildProgressCaption). PostWebMessageAsString είναι sync/void,
        // ασφαλές να καλείται από μέσα στο ίδιο το loop.
        private static string JsonThinkingUpdate(string text) =>
            new JObject { ["type"] = "thinking_update", ["text"] = text }.ToString(Formatting.None);

        private static List<string[]> ParseExportRows(JObject cmd)
        {
            var rows = new List<string[]>();
            foreach (var rowToken in (cmd["rows"] as JArray) ?? new JArray())
            {
                var arr = (rowToken as JArray)?
                    .Select(v => v?.ToString() ?? string.Empty)
                    .ToArray() ?? new string[0];
                rows.Add(arr);
            }
            return rows;
        }

        // "Έγγραφα\Jarvis Exports\{filename}_{timestamp}.{ext}" - timestamp
        // ώστε διαδοχικά exports να μην αντικαθιστούν το ένα το άλλο σιωπηλά
        // (δεν υπάρχει πλέον dialog να ρωτήσει "να αντικατασταθεί;").
        // ── CLEARS - αποθήκευση συζήτησης σε .md ΠΡΙΝ καθαρίσει (βλ. σχόλιο
        //    στο WebMessageReceived dispatch). Το markdown έρχεται ΗΔΗ έτοιμο
        //    από το JS (buildTranscriptMarkdown, βλ. index.html) - το C# απλά
        //    γράφει το αρχείο, ΧΩΡΙΑ dialog (ίδιο ιστορικό ρίσκο με τα
        //    SaveFileDialog crashes, βλ. σχόλιο πάνω από HandleExportAsync) -
        //    ίδιο σταθερό φάκελο (BuildExportPath, "Έγγραφα\Jarvis Exports").
        private void HandleSaveTranscript(JObject cmd)
        {
            try
            {
                string markdown = (string)cmd["markdown"] ?? "";
                if (string.IsNullOrWhiteSpace(markdown))
                {
                    webView.CoreWebView2.PostWebMessageAsString(
                        JsonChatNotice("Δεν υπάρχει συζήτηση προς αποθήκευση."));
                    return;
                }

                string path = BuildExportPath("Jarvis_Συζήτηση", ".md");
                File.WriteAllText(path, markdown, new UTF8Encoding(true));

                DebugLog.Log("[chat] save_transcript OK -> " + path);
                // Mini-markdown link, ίδιο μηχανισμό με το export success
                // message - ρεντεράρεται σαν clickable <a> (βλ. renderInlineLinks/
                // open_file στο index.html).
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonChatNotice($"✅ Η συζήτηση αποθηκεύτηκε: [{Path.GetFileName(path)}]({path})"));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[chat] HandleSaveTranscript EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonChatNotice("✖ Αποτυχία αποθήκευσης συζήτησης: " + ex.Message));
            }
        }

        // "chat_notice" - ΞΕΧΩΡΙΣΤΟ type από το κανονικό plain-string chat
        // reply (βλ. τελευταίο fallback branch στο index.html message
        // listener) - ΕΠΙΤΗΔΕΣ, ώστε το JS να μπορεί να καλέσει
        // startConversation() πριν το addMessage (το CLEAR/CLEARS έχει ήδη
        // μηδενίσει το started flag - μια plain-string απάντηση θα
        // κατέληγε "αόρατη" μέχρι το επόμενο πραγματικό μήνυμα, μιας και
        // το transcript είναι κρυμμένο όσο app.active είναι false).
        private static string JsonChatNotice(string text) =>
            new JObject { ["type"] = "chat_notice", ["text"] = text }.ToString(Formatting.None);

        private static string BuildExportPath(string filename, string ext)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Jarvis Exports");
            Directory.CreateDirectory(dir);

            string safeName = string.Join("_", filename.Split(Path.GetInvalidFileNameChars()));
            string stamped = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
            return Path.Combine(dir, stamped);
        }

        // Ίδιο fallback chain με πριν (PRSN.NAME -> USERS.NAME -> null),
        // επιβεβαιωμένο πάνω στο πραγματικό schema (S1Jetoil_TableInfo.csv).
        private string GetDisplayName()
        {
            int userId = _xSupport.ConnectionInfo.UserId;

            try
            {
                XTable prsn = _xSupport.GetSQLDataSet(
                    "SELECT NAME FROM PRSN WHERE USERS = :1", userId);

                var prsnName = prsn?.Current["NAME"]?.ToString();
                if (!string.IsNullOrWhiteSpace(prsnName))
                    return prsnName.Trim();
            }
            catch
            {
                // Αγνόησε, δοκίμασε το επόμενο επίπεδο fallback.
            }

            try
            {
                XTable users = _xSupport.GetSQLDataSet(
                    "SELECT NAME FROM USERS WHERE USERS = :1", userId);

                var usersName = users?.Current["NAME"]?.ToString();
                if (!string.IsNullOrWhiteSpace(usersName))
                    return usersName.Trim();
            }
            catch
            {
                // Αγνόησε, μένουμε στο γενικό μήνυμα.
            }

            return null;
        }

        // ══════════════════════════════════════════════════════════════════
        // Browser mode (νέο 15/08, βλ. README) - πραγματικό browsing σε
        // ΔΕΥΤΕΡΟ, ξεχωριστό WebView2 (browserView, βλ. JarvisShell.xaml),
        // ΟΧΙ iframe μέσα στο index.html (πολλά sites μπλοκάρουν
        // X-Frame-Options). Αριστερά (70%): native address bar + browserView.
        // Δεξιά (30%): το ΙΔΙΟ webView/index.html, απλά συρρικνωμένο -
        // #browserCurtain εκεί μέσα είναι ο "καμβός" συζήτησης.
        // ══════════════════════════════════════════════════════════════════

        private async Task EnsureBrowserViewInitializedAsync()
        {
            if (_browserViewInitialized) return;
            // Reuse ΤΟ ΙΔΙΟ environment με το κύριο webView (βλ.
            // JarvisShell_Loaded) - δύο WebView2 controls πάνω στο ίδιο
            // environment μοιράζονται browser process, καμία σύγκρουση.
            await browserView.EnsureCoreWebView2Async(_webView2Env);
            // Συγχρονίζει την address bar όταν ο χειριστής κλικάρει link
            // ΜΕΣΑ στο browserView (όχι μόνο όταν γράφει ο ίδιος) - ίδια
            // συμπεριφορά με κανονικό browser.
            browserView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                browserAddressBar.Text = browserView.CoreWebView2.Source;
            };
            _browserViewInitialized = true;
        }

        private const double BrowserPaneWidthFraction = 0.7; // 70% - βλ. README
        private static readonly TimeSpan BrowserSlideDuration = TimeSpan.FromMilliseconds(320);

        // Άνοιγμα - βλ. index.html showBrowserBar/openBrowser (▲ κλικ, ίδιο
        // 2-βημα pattern με Dashboard/Help: πρώτα συμπτυγμένη λωρίδα, μετά
        // άνοιγμα). Το animation (TranslateTransform slide-in) είναι
        // ΚΑΘΑΡΑ οπτικό - αν ποτέ αποδειχθεί προβληματικό ζωντανά, το
        // FALLBACK είναι να αφαιρεθεί ΜΟΝΟ το BeginAnimation() call, όλα τα
        // υπόλοιπα (πλάτος στήλης/ορατότητα) ήδη δουλεύουν σωστά χωρίς αυτό.
        private async void OpenBrowserPane(string url = null)
        {
            try
            {
                await EnsureBrowserViewInitializedAsync();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[browser] EnsureBrowserViewInitializedAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonBrowserReply("✖ Αδυναμία αρχικοποίησης browser: " + ex.Message));
                return;
            }

            if (!string.IsNullOrWhiteSpace(url))
                NavigateBrowserView(url);

            double totalWidth = rootGrid.ActualWidth;
            double targetWidth = Math.Max(200, totalWidth * BrowserPaneWidthFraction);

            // Functional state ΑΜΕΣΩΣ (όχι εξαρτημένο από το animation).
            browserColumn.Width = new GridLength(targetWidth, GridUnitType.Pixel);
            browserPane.Visibility = Visibility.Visible;

            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = -targetWidth,
                To = 0,
                Duration = new Duration(BrowserSlideDuration),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            browserPaneTransform.BeginAnimation(
                System.Windows.Media.TranslateTransform.XProperty, anim);
        }

        private void CloseBrowserPane()
        {
            if (browserPane.Visibility != Visibility.Visible) return; // ήδη κλειστό

            double currentWidth = browserColumn.ActualWidth;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = -currentWidth,
                Duration = new Duration(BrowserSlideDuration),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                }
            };
            anim.Completed += (s, e) =>
            {
                browserPane.Visibility = Visibility.Collapsed;
                browserColumn.Width = new GridLength(0);
                // Καθάρισε το animation - ξαναγίνεται στατικό 0, έτοιμο για
                // το επόμενο OpenBrowserPane().
                browserPaneTransform.BeginAnimation(
                    System.Windows.Media.TranslateTransform.XProperty, null);
                // Ειδοποίησε το index.html - χρειάζεται όταν το κλείσιμο
                // ξεκίνησε από το native ✕ (το JS δεν το ξέρει ακόμα εκεί).
                // Αβλαβές/idempotent αν το JS το ξέρει ήδη (κλείσιμο από ▼).
                webView.CoreWebView2.PostWebMessageAsString(JsonBrowserClosed());
            };
            browserPaneTransform.BeginAnimation(
                System.Windows.Media.TranslateTransform.XProperty, anim);
        }

        private void NavigateBrowserFromAddressBar()
        {
            string url = NormalizeUrl(browserAddressBar.Text);
            if (url == null) return;
            browserAddressBar.Text = url;
            NavigateBrowserView(url);
        }

        // Καλείται ΚΑΙ από την address bar (χειριστής) ΚΑΙ από το
        // open_url tool (agent, βλ. HandleBrowserMessageAsync) - μία πηγή
        // αλήθειας για το πώς γίνεται navigate + ενημέρωση address bar.
        private void NavigateBrowserView(string url)
        {
            browserAddressBar.Text = url;
            try
            {
                browserView.CoreWebView2?.Navigate(url);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[browser] Navigate EXCEPTION: " + ex);
            }
        }

        // Δέχεται "google.com" (χωρίς scheme) - προσθέτει https:// αυτόματα,
        // ίδια λογική με κάθε κανονικό browser address bar. Κείμενο που
        // ΔΕΝ μοιάζει URL (κενά/χωρίς τελεία) γίνεται αναζήτηση Google.
        private static string NormalizeUrl(string input)
        {
            string s = (input ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return null;
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return s;
            if (s.Contains(" ") || !s.Contains("."))
                return "https://www.google.com/search?q=" + Uri.EscapeDataString(s);
            return "https://" + s;
        }

        // Ξεχωριστός καμβός (#browserCurtain, δεξιά 30%) - ίδιο σκεπτικό με
        // το Help mode, ΝΕΟ `open_url` tool (βλ. JarvisTools) αντί για
        // marker/SOACTION logic. onNavigate callback (νέο param στο
        // AskAsync) - καλείται όταν ο Claude καλέσει το open_url tool,
        // κάνει ΤΟ ΙΔΙΟ navigate+address-bar-update με τον χειριστή.
        private async Task HandleBrowserMessageAsync(JObject cmd)
        {
            string text = (string)cmd["text"] ?? "";

            try
            {
                string answer = await _agentClient.AskAsync(
                    _agentAccountRef, _xSupport, _browserConversation, text,
                    browserMode: true,
                    onNavigate: url => NavigateBrowserView(url),
                    onReadPage: ReadBrowserPageContentAsync,
                    onExtractPageTables: ExtractBrowserPageTablesAsync,
                    onProgress: t => webView.CoreWebView2.PostWebMessageAsString(JsonBrowserStatus(t)),
                    onShowContactResults: contacts => webView.CoreWebView2.PostWebMessageAsString(
                        new JObject { ["type"] = "show_contact_results_data", ["contacts"] = contacts }.ToString(Formatting.None)),
                    // ΝΕΟ 18/08, ρητό αίτημα χρήστη - "στην Browser καρτέλα...
                    // θα πρέπει και από εκεί να εισάγουμε είδη" (bulk import
                    // από scraped δεδομένα σελίδας) - ίδιο ParamCode 500028
                    // με το γενικό chat.
                    maxIterations: JarvisTools.GetCrmTaskOptionalParam(_xSupport, 500028, 40));
                webView.CoreWebView2.PostWebMessageAsString(JsonBrowserReply(answer));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[browser] EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonBrowserReply("✖ Σφάλμα: " + ex.Message));
            }
        }

        // read_page_content tool (βλ. JarvisTools.ExecuteReadPageContent) -
        // διαβάζει το ΟΡΑΤΟ κείμενο (document.body.innerText, ΟΧΙ raw HTML)
        // της σελίδας που είναι ΤΩΡΑ φορτωμένη στο browserView.
        // ΠΡΟΣΟΧΗ: το ExecuteScriptAsync επιστρέφει το αποτέλεσμα JSON-
        // encoded (ένα JS string "Hello" γυρνάει ΚΥΡΙΟΛΕΚΤΙΚΑ ως
        // "\"Hello\"") - χρειάζεται JSON deserialize για το πραγματικό
        // κείμενο, όχι απευθείας χρήση του raw string.
        private async Task<string> ReadBrowserPageContentAsync()
        {
            if (browserView.CoreWebView2 == null) return "";
            try
            {
                string raw = await browserView.CoreWebView2.ExecuteScriptAsync(
                    "document.body ? document.body.innerText : ''");
                return JsonConvert.DeserializeObject<string>(raw) ?? "";
            }
            catch (Exception ex)
            {
                DebugLog.Log("[browser] ReadBrowserPageContentAsync EXCEPTION: " + ex);
                return "";
            }
        }

        // extract_page_tables tool (βλ. JarvisTools.ExecuteExtractPageTables) -
        // ΝΕΟ 18/08, ρητό αίτημα χρήστη "scraping δεδομένων από ιστοσελίδες".
        // Διαβάζει τα πραγματικά <table> elements της σελίδας (DOM query
        // ΜΕΣΑ στο ίδιο το browserView, ΟΧΙ κάτι δικό μας HTML parsing) -
        // ΧΩΡΙΣ tableIndex: ΜΟΝΟ περίληψη (index/rowCount/colCount/header)
        // για ΟΛΟΥΣ τους πίνακες, ώστε ο Claude να διαλέξει χωρίς να
        // ξοδέψει context σε τεράστιους/άσχετους πίνακες (π.χ. layout
        // tables). ΜΕ tableIndex: πλήρη δεδομένα (header+rows, κομμένα στις
        // πρώτες 200 γραμμές) ΜΟΝΟ για εκείνον τον πίνακα. ΙΔΙΟ
        // JSON-encoding gotcha με το ReadBrowserPageContentAsync πιο πάνω -
        // το ExecuteScriptAsync γυρνάει το αποτέλεσμα JSON-encoded ΣΑΝ
        // STRING (το script κάνει το ΔΙΚΟ ΤΟΥ JSON.stringify από μέσα),
        // χρειάζεται DeserializeObject<string> πρώτα για να πάρουμε πίσω
        // το πραγματικό JSON text.
        private async Task<string> ExtractBrowserPageTablesAsync(int? tableIndex)
        {
            if (browserView.CoreWebView2 == null) return "[]";
            try
            {
                string idxLiteral = tableIndex.HasValue ? tableIndex.Value.ToString() : "null";
                string script =
                    "(function(){" +
                    "var idxFilter=" + idxLiteral + ";" +
                    "var MAX_ROWS=200;" +
                    "var tables=Array.prototype.slice.call(document.querySelectorAll('table'));" +
                    "function cellsText(tr){return Array.prototype.slice.call(tr.querySelectorAll('th,td')).map(function(c){return (c.innerText||'').trim();});}" +
                    "var result=[];" +
                    "for(var idx=0;idx<tables.length;idx++){" +
                    "if(idxFilter!==null&&idx!==idxFilter)continue;" +
                    "var trs=Array.prototype.slice.call(tables[idx].querySelectorAll('tr'));" +
                    "if(trs.length===0)continue;" +
                    "var header=cellsText(trs[0]);" +
                    "var dataRows=trs.slice(1).map(cellsText).filter(function(r){return r.some(function(c){return c.length>0;});});" +
                    "if(dataRows.length===0)continue;" +
                    "if(idxFilter===null){" +
                    "result.push({index:idx,rowCount:dataRows.length,colCount:header.length,header:header});" +
                    "}else{" +
                    "var truncated=dataRows.length>MAX_ROWS;" +
                    "result.push({index:idx,rowCount:dataRows.length,colCount:header.length,header:header,rows:truncated?dataRows.slice(0,MAX_ROWS):dataRows,truncated:truncated});" +
                    "}" +
                    "}" +
                    "return JSON.stringify(result);" +
                    "})()";
                string raw = await browserView.CoreWebView2.ExecuteScriptAsync(script);
                return JsonConvert.DeserializeObject<string>(raw) ?? "[]";
            }
            catch (Exception ex)
            {
                DebugLog.Log("[browser] ExtractBrowserPageTablesAsync EXCEPTION: " + ex);
                return "[]";
            }
        }

        private static string JsonBrowserReply(string text) =>
            new JObject { ["type"] = "browser_reply", ["text"] = text }.ToString(Formatting.None);

        // "Legend" της μικρής σφαίρας στο Browser mode titlebar - ίδιο
        // πνεύμα με το JsonHelpStatus/JsonDashboardStatus.
        private static string JsonBrowserStatus(string text) =>
            new JObject { ["type"] = "browser_status", ["text"] = text }.ToString(Formatting.None);

        // ── Email curtain mode - ΝΕΟ 17/08 (βλ. README Roadmap #1) ─────────
        // Ίδιο idiom με HandleBrowserMessageAsync, ΧΩΡΙΣ onNavigate/onReadPage
        // (άσχετα εδώ - δεν υπάρχει native browser pane).
        private async Task HandleEmailMessageAsync(JObject cmd)
        {
            string text = (string)cmd["text"] ?? "";

            try
            {
                string answer = await _agentClient.AskAsync(
                    _agentAccountRef, _xSupport, _emailConversation, text,
                    emailMode: true,
                    onProgress: t => webView.CoreWebView2.PostWebMessageAsString(JsonEmailStatus(t)),
                    // ΝΕΟ 17/08, ρητό αίτημα χρήστη - filter_email_inbox/
                    // filter_calendar (βλ. JarvisEmailAccess) ενημερώνουν
                    // ΑΠΕΥΘΕΙΑΣ το κύριο παράθυρο (ΟΧΙ το chat) μέσω αυτών
                    // των 2 postMessage side-channels - το index.html τα
                    // πιάνει και ξανακαλεί το ΙΔΙΟ deterministic fetch που
                    // ήδη χρησιμοποιεί το toolbar (email_get_inbox/
                    // email_get_calendar).
                    onFilterEmailInbox: (sinceDate, searchText, insight, filters) => webView.CoreWebView2.PostWebMessageAsString(
                        new JObject { ["type"] = "email_set_inbox_filter", ["sinceDate"] = sinceDate, ["searchText"] = searchText, ["insight"] = insight, ["filters"] = filters }.ToString(Formatting.None)),
                    onFilterCalendar: (date, searchText, insight) => webView.CoreWebView2.PostWebMessageAsString(
                        new JObject { ["type"] = "email_set_calendar_filter", ["date"] = date, ["searchText"] = searchText, ["insight"] = insight }.ToString(Formatting.None)),
                    // ΝΕΟ 17/08, ρητό αίτημα χρήστη (4ο fix - "θέλουμε να
                    // εξαιρεί αυτός με τις οδηγίες που παίρνει, το μόνο
                    // πρόβλημα είναι να είναι εντός του Main παραθύρου") -
                    // show_calendar_entries: ο Claude υπολογίζει το
                    // αποτέλεσμα ΜΟΝΟΣ του (query_data, οποιαδήποτε λογική)
                    // και το ΣΤΕΛΝΕΙ ΑΠΕΥΘΕΙΑΣ εδώ, ΧΩΡΙΣ re-fetch/re-filter.
                    onShowCalendarEntries: (date, entries) => webView.CoreWebView2.PostWebMessageAsString(
                        new JObject { ["type"] = "email_set_calendar_results", ["date"] = date, ["entries"] = entries }.ToString(Formatting.None)),
                    // ΝΕΟ 18/08, ρητό αίτημα χρήστη - "εντολή που θα δουλεύει
                    // περιγραφικά ... θα επιστρέφει σε modal τα στοιχεία της
                    // επαφής" - postMessage στο κύριο παράθυρο, ΟΧΙ στο chat.
                    onShowContactResults: contacts => webView.CoreWebView2.PostWebMessageAsString(
                        new JObject { ["type"] = "show_contact_results_data", ["contacts"] = contacts }.ToString(Formatting.None)));
                webView.CoreWebView2.PostWebMessageAsString(JsonEmailReply(answer));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email] EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonEmailReply("✖ Σφάλμα: " + ex.Message));
            }
        }

        private static string JsonEmailReply(string text) =>
            new JObject { ["type"] = "email_reply", ["text"] = text }.ToString(Formatting.None);

        private static string JsonEmailStatus(string text) =>
            new JObject { ["type"] = "email_status", ["text"] = text }.ToString(Formatting.None);

        // Calendar tab (email curtain) - συγχωνεύει Outlook calendar events
        // (JarvisEmailAccess.GetCalendarEventsAsync, Graph API) ΜΕ ανοιχτές
        // SOACTION εργασίες (JarvisTools.GetSoactionCalendarEntries, ACTOR=
        // τρέχων χρήστης) για το ΙΔΙΟ εύρος ημερομηνιών - ΝΕΟ 17/08, βλ.
        // README Roadmap #1 (task #32/#33). Deterministic UI, ΟΧΙ AI/tool
        // call - ίδιο idiom με HandleDashboardGetMyTasksAsync (XSupport call
        // ΠΡΕΠΕΙ σε Task.Run, ΟΧΙ συγχρονισμένο στο UI thread).
        //
        // Το Outlook sync ΔΕΝ μπλοκάρει την εμφάνιση των Soft1 εργασιών αν
        // αποτύχει (π.χ. λείπει ακόμα το Calendars.Read permission) -
        // επιστρέφει άδεια Outlook λίστα + ξεχωριστό outlookError string,
        // το JS δείχνει προειδοποίηση ΧΩΡΙΣ να κρύψει τα Soft1 events.
        private async Task HandleEmailGetCalendarAsync(JObject cmd)
        {
            DateTime start = DateTime.TryParse((string)cmd["startDate"], out var s) ? s.Date : DateTime.Today;
            DateTime end = DateTime.TryParse((string)cmd["endDate"], out var e) ? e.Date : start.AddDays(1);
            if (end <= start) end = start.AddDays(1);
            // ΝΕΟ 17/08, ρητό αίτημα χρήστη - προαιρετικό searchText, ΜΑΖΙ
            // με το date (βλ. filter_calendar/JarvisAgentClient emailMode).
            string searchText = (string)cmd["searchText"];

            try
            {
                JArray soft1Entries = await Task.Run(() => JarvisTools.GetSoactionCalendarEntries(_xSupport, start, end, searchText));

                JArray outlookEntries = new JArray();
                string outlookError = null;
                try
                {
                    outlookEntries = await JarvisEmailAccess.GetCalendarEventsAsync(_xSupport, start, end, searchText);
                }
                catch (Exception exOutlook)
                {
                    DebugLog.Log("[email] calendar Outlook sync EXCEPTION: " + exOutlook);
                    outlookError = exOutlook.Message;
                }

                webView.CoreWebView2.PostWebMessageAsString(
                    JsonEmailCalendarResult(soft1Entries, outlookEntries, outlookError));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email] HandleEmailGetCalendarAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_calendar_result",
                    ["success"] = false,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        private static string JsonEmailCalendarResult(JArray soft1Entries, JArray outlookEntries, string outlookError) =>
            new JObject
            {
                ["type"] = "email_calendar_result",
                ["success"] = true,
                ["soft1Entries"] = soft1Entries,
                ["outlookEntries"] = outlookEntries,
                ["outlookError"] = outlookError
            }.ToString(Formatting.None);

        // Email tab (email curtain) - ΝΕΟ 17/08, βλ. README Roadmap #1 (task
        // #35). Αδιάβαστα emails από sinceDate (default τελευταία εβδομάδα -
        // το default ζει στο JS, βλ. requestEmailInbox) μέχρι τώρα.
        private async Task HandleEmailGetInboxAsync(JObject cmd)
        {
            DateTime since = DateTime.TryParse((string)cmd["sinceDate"], out var s) ? s.Date : DateTime.Today.AddDays(-7);
            // ΝΕΟ 17/08, ρητό αίτημα χρήστη - προαιρετικό searchText, ΜΑΖΙ
            // με το sinceDate (βλ. filter_email_inbox/JarvisAgentClient emailMode).
            string searchText = (string)cmd["searchText"];

            try
            {
                JArray emails = await JarvisEmailAccess.GetInboxEmailsAsync(_xSupport, since, searchText);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_inbox_result",
                    ["success"] = true,
                    ["emails"] = emails
                }.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email] HandleEmailGetInboxAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_inbox_result",
                    ["success"] = false,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // Πλήρες περιεχόμενο email (double-click, "σαν να είναι Outlook") -
        // ΝΕΟ 17/08, βλ. README Roadmap #1.
        private async Task HandleEmailGetDetailAsync(JObject cmd)
        {
            string messageId = (string)cmd["messageId"];
            try
            {
                if (string.IsNullOrWhiteSpace(messageId))
                    throw new Exception("Λείπει το messageId.");
                JObject detail = await JarvisEmailAccess.GetEmailDetailAsync(_xSupport, messageId);
                detail["type"] = "email_detail_result";
                detail["success"] = true;
                webView.CoreWebView2.PostWebMessageAsString(detail.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email] HandleEmailGetDetailAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_detail_result",
                    ["success"] = false,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // Deterministic "download attachment" (ΝΕΟ 17/08) - καλεί ΑΠΕΥΘΕΙΑΣ
        // το ΙΔΙΟ JarvisEmailAccess.ExecuteDownloadEmailAttachment που
        // χρησιμοποιεί ΚΑΙ το download_email_attachment tool (chat) - εδώ
        // ΧΩΡΙΣ Claude/AskAsync ενδιάμεσο, ίδιο idiom με τα υπόλοιπα
        // deterministic email_* commands (email_get_calendar/email_get_inbox).
        private async Task HandleEmailDownloadAttachmentDirectAsync(JObject cmd)
        {
            try
            {
                JObject parsed = JObject.Parse(await JarvisEmailAccess.ExecuteDownloadEmailAttachment(_xSupport, cmd));
                parsed["type"] = "email_download_result";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email] HandleEmailDownloadAttachmentDirectAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_download_result",
                    ["success"] = false,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // Deterministic "✎ Νέο email" (ΝΕΟ 18/08) - καλεί ΑΠΕΥΘΕΙΑΣ
        // JarvisEmailAccess.SendEmailAsync (ΙΔΙΟ backend με το send_email
        // chat tool) - εδώ ΧΩΡΙΣ Claude/AskAsync ενδιάμεσο, η φόρμα του
        // compose modal ΕΙΝΑΙ η επιβεβαίωση.
        private async Task HandleEmailComposeSendAsync(JObject cmd)
        {
            try
            {
                string to = (string)cmd["to"];
                string subject = (string)cmd["subject"];
                string body = (string)cmd["body"];
                string cc = (string)cmd["cc"];
                await JarvisEmailAccess.SendEmailAsync(_xSupport, to, subject, body, cc: cc);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_compose_result",
                    ["success"] = true
                }.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email] HandleEmailComposeSendAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_compose_result",
                    ["success"] = false,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // Deterministic "↩ Απάντηση" (ΝΕΟ 18/08) - καλεί ΑΠΕΥΘΕΙΑΣ
        // JarvisEmailAccess.ReplyEmailAsync (ΙΔΙΟ backend με το reply_email
        // chat tool) - πραγματικό Graph reply (σωστό threading), ΟΧΙ νέο
        // email με χειροκίνητο "RE:" prefix.
        private async Task HandleEmailReplySendAsync(JObject cmd)
        {
            try
            {
                string messageId = (string)cmd["messageId"];
                string body = (string)cmd["body"];
                string cc = (string)cmd["cc"];
                await JarvisEmailAccess.ReplyEmailAsync(_xSupport, messageId, body, cc);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_reply_result",
                    ["success"] = true
                }.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[email] HandleEmailReplySendAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "email_reply_result",
                    ["success"] = false,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // Courier curtain - ΝΕΟ 17/08, ρητό αίτημα χρήστη (JARVISCOURIER
        // feature, βλ. Core/JarvisCourier.cs). v1 scope: ΜΟΝΟ μεμονωμένη
        // έκδοση voucher (ΟΧΙ μαζική) - εύρεση παραστατικών ΜΕΣΩ chat
        // (courierMode), έκδοση voucher deterministic (modal, ΟΧΙ chat).
        // ══════════════════════════════════════════════════════════════════

        private async Task HandleCourierStartAsync()
        {
            _courierConversation.Clear();
            try
            {
                var access = await Task.Run(
                    () => JarvisLicenseGuard.CheckAccessSilent(_xSupport, AccessConfig.CourierToolName));

                if (!access.Allowed)
                {
                    string denyMsg = JarvisLicenseGuard.BuildMessage(access);
                    DebugLog.Log($"[courier] entitlement DENIED (toolName={AccessConfig.CourierToolName}): {denyMsg}");
                    webView.CoreWebView2.PostWebMessageAsString(JsonCourierAccessResult(false, denyMsg));
                    return;
                }

                _courierAllowed = true;
                DebugLog.Log($"[courier] entitlement ALLOWED (toolName={AccessConfig.CourierToolName})");
                webView.CoreWebView2.PostWebMessageAsString(JsonCourierAccessResult(true, null));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier] HandleCourierStartAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonCourierAccessResult(false, "Απρόσμενο σφάλμα ελέγχου άδειας: " + ex.Message));
            }
        }

        private static string JsonCourierAccessResult(bool allowed, string message) =>
            new JObject { ["type"] = "courier_access_result", ["allowed"] = allowed, ["message"] = message }
                .ToString(Formatting.None);

        private async Task HandleCourierMessageAsync(JObject cmd)
        {
            string text = (string)cmd["text"] ?? "";
            try
            {
                string answer = await _agentClient.AskAsync(
                    _agentAccountRef, _xSupport, _courierConversation, text,
                    courierMode: true,
                    onProgress: t => webView.CoreWebView2.PostWebMessageAsString(JsonCourierStatus(t)),
                    // ΝΕΟ 17/08 - ο Claude βρίσκει τα παραστατικά (query_data)
                    // και τα ΣΤΕΛΝΕΙ εδώ - postMessage στο κύριο παράθυρο,
                    // ΟΧΙ στο chat (ίδιο μάθημα με το Email/Calendar tab).
                    onShowCourierDocuments: entries => webView.CoreWebView2.PostWebMessageAsString(
                        new JObject { ["type"] = "courier_set_documents", ["entries"] = entries }.ToString(Formatting.None)));
                webView.CoreWebView2.PostWebMessageAsString(JsonCourierReply(answer));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier] HandleCourierMessageAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(JsonCourierReply("✖ Σφάλμα: " + ex.Message));
            }
        }

        private static string JsonCourierReply(string text) =>
            new JObject { ["type"] = "courier_reply", ["text"] = text }.ToString(Formatting.None);

        private static string JsonCourierStatus(string text) =>
            new JObject { ["type"] = "courier_status", ["text"] = text }.ToString(Formatting.None);

        // "Εμφάνιση εγγραφής" - deterministic, ΞΑΝΑΧΡΗΣΙΜΟΠΟΙΕΙ ΑΥΤΟΥΣΙΟ το
        // ήδη υπάρχον JarvisTools.ExecuteOpenDocument (ΙΔΙΟ μηχανισμό με το
        // open_document tool του κύριου chat). Dispatcher.BeginInvoke ΕΔΩ
        // (ΟΧΙ στο chat-driven μονοπάτι) - καλείται ΑΠΕΥΘΕΙΑΣ από κλικ
        // κουμπιού, ΧΩΡΙΣ προηγούμενο await boundary σαν του chat tool-loop
        // (πολλαπλά network calls πριν φτάσει στο ExecS1Command εκεί) - ΙΔΙΟ
        // defensive idiom με HandleDashboardOpenCrmAction (βλ. εκεί - ίδιο
        // reentrancy θέμα με το ιστορικό SaveFileDialog crash).
        private void HandleCourierOpenDocument(JObject cmd)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    JarvisTools.ExecuteOpenDocument(_xSupport, cmd);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[courier] HandleCourierOpenDocument EXCEPTION: " + ex);
                    try
                    {
                        webView.CoreWebView2.PostWebMessageAsString(
                            "✖ Αδυναμία ανοίγματος παραστατικού: " + ex.Message);
                    }
                    catch { /* δεν το αφήνουμε να ξεφύγει */ }
                }
            }));
        }

        // "Δημιουργία Voucher" κλικ - φορτώνει το modal (ΙΔΙΟ πεδία με
        // CourierControl.PopulateFromRequest) - findocId=null -> ΑΔΕΙΑ φόρμα
        // (v1, ρητό αίτημα χρήστη: "έκδοση voucher ΧΩΡΙΣ παραστατικό").
        private async Task HandleCourierLoadVoucherFormAsync(JObject cmd)
        {
            int? findocId = (int?)cmd?["findocId"];
            try
            {
                JObject request = findocId.HasValue && findocId.Value > 0
                    ? await Task.Run(() => JarvisCourier.BuildRequestFromFindoc(_xSupport, findocId.Value))
                    : new JObject();
                JArray providers = await Task.Run(() => JarvisCourier.LoadActiveProviders(_xSupport));

                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "courier_voucher_form_result",
                    ["success"] = true,
                    ["request"] = request,
                    ["providers"] = providers
                }.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier] HandleCourierLoadVoucherFormAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "courier_voucher_form_result",
                    ["success"] = false,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // "Έκδοση Voucher" κλικ (μέσα στο modal) - port CourierControl.
        // btnCreate_Click. Server-side validation ΕΠΙΠΛΕΟΝ του client-side.
        private async Task HandleCourierCreateVoucherAsync(JObject cmd)
        {
            try
            {
                JObject result = await JarvisCourier.CreateVoucherAsync(_xSupport, cmd);
                result["type"] = "courier_create_voucher_result";
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier] HandleCourierCreateVoucherAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "courier_create_voucher_result",
                    ["success"] = false,
                    ["errorMessage"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // Δεύτερο tab του modal - λήψη PDF (port btnPrintVoucher_Click, ΑΛΛΑ
        // base64 αντί για WebBrowser.Navigate σε temp file - το JS δείχνει
        // το PDF σε iframe/embed με data: URI).
        private async Task HandleCourierGetVoucherPdfAsync(JObject cmd)
        {
            string providerCode = (string)cmd["providerCode"];
            string shipmentNumber = (string)cmd["shipmentNumber"];
            try
            {
                JObject result = await JarvisCourier.GetVoucherPdfAsync(_xSupport, providerCode, shipmentNumber);
                result["type"] = "courier_voucher_pdf_result";
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier] HandleCourierGetVoucherPdfAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "courier_voucher_pdf_result",
                    ["success"] = false,
                    ["error"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // "Ακύρωση Voucher" κλικ (μέσα στο modal, όταν υπάρχει ήδη ενεργή
        // αποστολή) - port CourierControl.btnCancelShipment_Click. ΠΡΟΣΟΧΗ:
        // περνάμε providerNAME (ΟΧΙ code) - βλ. σχόλιο πάνω από
        // JarvisCourier.GetProviderConfigByName για το γιατί.
        private async Task HandleCourierCancelVoucherAsync(JObject cmd)
        {
            try
            {
                string providerName = (string)cmd["providerName"];
                string shipmentNumber = (string)cmd["shipmentNumber"];
                string jobId = (string)cmd["jobId"];
                int? findocId = (int?)cmd["findocId"];

                JObject result = await JarvisCourier.CancelVoucherAsync(_xSupport, providerName, shipmentNumber, jobId, findocId);
                result["type"] = "courier_cancel_voucher_result";
                webView.CoreWebView2.PostWebMessageAsString(result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[courier] HandleCourierCancelVoucherAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "courier_cancel_voucher_result",
                    ["success"] = false,
                    ["errorMessage"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // Ειδοποιεί το index.html ότι το native browser pane έκλεισε -
        // χρειάζεται όταν το κλείσιμο ξεκινάει από τη NATIVE πλευρά (το ✕
        // στην address bar, βλ. JarvisShell.xaml) αντί από το ▼ της
        // κουρτίνας (εκεί το JS ήδη ξέρει, δεν χρειάζεται ειδοποίηση) - το
        // index.html συγχρονίζει την κουρτίνα του ώστε να μη μείνει
        // ορατή χωρίς το πραγματικό browser δίπλα της.
        private static string JsonBrowserClosed() =>
            new JObject { ["type"] = "browser_closed" }.ToString(Formatting.None);

        // ── DR (Document Reader) mode - βλ. README Roadmap #6 ──────────────
        //
        // Στάδιο 1/6: μόνο το entitlement check + ενημέρωση του UI. Καλείται
        // όταν ανοίγει η DR κουρτίνα (index.html openDr -> postCommand
        // dr_start) - lazy, ΟΧΙ στο αρχικό NavigationCompleted σαν το βασικό
        // S1JARVIS check, μιας και το DR είναι προαιρετικό/ξεχωριστό
        // entitlement (βλ. AccessConfig.DocReaderToolName).
        private async Task HandleDrStartAsync()
        {
            try
            {
                var access = await Task.Run(
                    () => JarvisLicenseGuard.CheckAccessSilent(_xSupport, AccessConfig.DocReaderToolName));

                if (!access.Allowed)
                {
                    string denyMsg = JarvisLicenseGuard.BuildMessage(access);
                    DebugLog.Log($"[dr] entitlement DENIED (toolName={AccessConfig.DocReaderToolName}): {denyMsg}");
                    webView.CoreWebView2.PostWebMessageAsString(JsonDrAccessResult(false, denyMsg));
                    return;
                }

                _drAllowed = true;
                DebugLog.Log($"[dr] entitlement ALLOWED (toolName={AccessConfig.DocReaderToolName})");
                webView.CoreWebView2.PostWebMessageAsString(JsonDrAccessResult(true, null));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrStartAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    JsonDrAccessResult(false, "Απρόσμενο σφάλμα ελέγχου άδειας: " + ex.Message));
            }
        }

        private static string JsonDrAccessResult(bool allowed, string message) =>
            new JObject { ["type"] = "dr_access_result", ["allowed"] = allowed, ["message"] = message }
                .ToString(Formatting.None);

        // ── DR - Στάδιο 3α: ταυτοποίηση εκδότη + αναζήτηση συναλλασσόμενου
        //    (ΝΕΟ 16/08, βλ. README Roadmap #6 - ΡΗΤΑ περιορισμένο σκοπείο,
        //    ιστορικό σειράς/ΑΑΔΕ/Αγορά-Δαπάνη είναι επόμενο βήμα). Ροή ανά
        //    αρχείο: 1) one-shot AI vision call (JarvisAgentClient.
        //    DetectDocumentIssuerAsync - ΕΚΤΟΣ του κύριου chat/tool-loop,
        //    ΔΕΝ αγγίζει το _conversation) 2) αναζήτηση TRDR με το ΑΦΜ που
        //    βρέθηκε (JarvisTools.ExecuteFindTraderByAfm, ΧΩΡΙΣ φίλτρο
        //    SODTYPE - βλ. εκεί). ΔΕΝ χρειάζεται Dispatcher.BeginInvoke εδώ
        //    (σε αντίθεση με OpenDocument/OpenTrader) - δεν καλεί
        //    ExecS1Command, μόνο HTTP + SQL read, ήδη μέσα σε async method
        //    με await boundary πριν από οποιαδήποτε native κλήση. ─────────
        private async Task HandleDrIdentifyIssuerAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                string base64 = (string)cmd["base64"];
                string mimeType = (string)cmd["mimeType"];

                var detection = await _agentClient.DetectDocumentIssuerAsync(_agentAccountRef, base64, mimeType);
                bool detected = detection["success"]?.Value<bool>() == true;
                if (!detected)
                {
                    SendDrIssuerResult(fileId, false, null, null, null, null,
                        detection["errorMessage"]?.ToString() ?? "Δεν αναγνωρίστηκε ΑΦΜ εκδότη.");
                    return;
                }

                string issuerAfm = detection["issuerAfm"]?.ToString() ?? "";
                var trader = JObject.Parse(JarvisTools.ExecuteFindTraderByAfm(_xSupport, issuerAfm));

                // Στάδιο 3β/5 - ιστορικό σειράς + duplicate-check, ΜΟΝΟ αν
                // βρέθηκε συναλλασσόμενος - ΙΔΙΟ round-trip, ΔΕΝ χρειάζεται
                // ξεχωριστό postCommand από το JS. Το duplicate-check
                // ΜΕΤΑΚΙΝΗΘΗΚΕ εδώ 16/08 (ρητή απόφαση χρήστη - "μετά την
                // ταυτοποίηση συναλλασσόμενου"): ΓΙΑ ΝΕΟ συναλλασσόμενο
                // (trader.found=false) είναι ΛΟΓΙΚΑ αδύνατο να υπάρχει ήδη
                // το παραστατικό - παραλείπεται εντελώς, ΚΑΙ γλυτώνει το
                // ακριβό (Opus) full-extraction call του Σταδίου 4 αν
                // αποδειχτεί διπλότυπο.
                JObject seriesHistory = null;
                JObject duplicateCheck = null;
                if (trader["found"]?.Value<bool>() == true)
                {
                    int trdrId = trader["trdrId"].Value<int>();
                    string docType = detection["docType"]?.ToString();
                    seriesHistory = JObject.Parse(JarvisTools.ExecuteFindTraderSeriesHistory(_xSupport, trdrId, docType));

                    string docNumber = detection["docNumber"]?.ToString();
                    string docDate = detection["docDate"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(docNumber) && !string.IsNullOrWhiteSpace(docDate))
                    {
                        duplicateCheck = JObject.Parse(
                            JarvisTools.ExecuteCheckDuplicateDocument(_xSupport, trdrId, docNumber, docDate));
                    }
                }

                SendDrIssuerResult(fileId, true, detection, trader, seriesHistory, duplicateCheck, null);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrIdentifyIssuerAsync EXCEPTION: " + ex);
                SendDrIssuerResult(fileId, false, null, null, null, null, "Απρόσμενο σφάλμα: " + ex.Message);
            }
        }

        private void SendDrIssuerResult(string fileId, bool detected, JObject detection, JObject trader,
            JObject seriesHistory, JObject duplicateCheck, string errorMessage)
        {
            var payload = new JObject
            {
                ["type"] = "dr_issuer_result",
                ["fileId"] = fileId,
                ["detected"] = detected,
                ["errorMessage"] = errorMessage,
                ["detection"] = detection,
                ["trader"] = trader,
                ["seriesHistory"] = seriesHistory,
                ["duplicateCheck"] = duplicateCheck
            };
            webView.CoreWebView2.PostWebMessageAsString(payload.ToString(Formatting.None));
        }

        // ── DR - Στάδιο 3γ: ΑΑΔΕ auto-create (βλ. index.html "Δημιουργία
        //    νέου Προμηθευτή" κουμπί στη γραμμή αρχείου) - ΝΕΟ 16/08.
        //    Task.Run - ίδιο πνεύμα με HandleDrStartAsync (το κάλεσμα προς
        //    GsisCmpAfmData είναι πραγματικό network call προς την ΑΑΔΕ,
        //    δεν πρέπει να μπλοκάρει το UI thread). ─────────────────────
        private async Task HandleDrLookupAadeAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                string afm = (string)cmd["afm"];
                int sodType = (int?)cmd["sodType"] ?? 12;
                string result = await Task.Run(() => JarvisTools.ExecuteGetAadeData(_xSupport, afm, sodType));
                JObject parsed = JObject.Parse(result);
                parsed["type"] = "dr_aade_result";
                parsed["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrLookupAadeAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_aade_result", ["fileId"] = fileId,
                    ["success"] = false, ["message"] = "Απρόσμενο σφάλμα: " + ex.Message
                }.ToString(Formatting.None));
            }
        }

        // ── CREATEAADEAFM - standalone εντολή, ΝΕΟ 16/08 (βλ. index.html
        //    startManualAadeLookup) - ΑΝΕΞΑΡΤΗΤΗ από το DR file-upload
        //    flow, ΑΛΛΑ επαναχρησιμοποιεί ΑΥΤΟΥΣΙΟ το ΙΔΙΟ αποτέλεσμα-σχήμα
        //    (SendDrIssuerResult/"dr_issuer_result") ώστε το JS να
        //    ξαναχρησιμοποιήσει το ΙΔΙΟ applyDrIssuerResult/AADE panel
        //    ΧΩΡΙΣ καμία νέα rendering λογική. Διαφορά από το
        //    HandleDrIdentifyIssuerAsync: ΔΕΝ καλεί AI (το ΑΦΜ το δίνει ήδη
        //    ο χειριστής) ΚΑΙ η αναζήτηση TRDR είναι SCOPED στο δηλωμένο
        //    sodType (ExecuteFindTraderByAfmAndSodType, ΟΧΙ το γενικό
        //    ExecuteFindTraderByAfm). ─────────────────────────────────────
        private async Task HandleDrManualLookupAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                // ΝΕΟ 18/08, διόρθωση κενού ασφαλείας: το CREATEAADEAFM είναι
                // standalone εντολή από το ΚΥΡΙΟ chat (index.html send()) -
                // μπορεί να κληθεί ΧΩΡΙΣ να έχει ανοίξει ποτέ η κουρτίνα DR,
                // οπότε το _drAllowed (γεμίζει ΜΟΝΟ στο HandleDrStartAsync)
                // μπορεί να είναι ακόμα false ενώ ο χρήστης ΔΕΝ έχει καν
                // δοκιμάσει να ανοίξει την κουρτίνα - ΑΛΛΑ το αντίστροφο
                // (καμία εξάρτηση από _drAllowed) σήμαινε ότι η εντολή
                // έτρεχε ΧΩΡΙΣ ΚΑΝΕΝΑΝ έλεγχο άδειας. Ρητός, ανεξάρτητος
                // έλεγχος εδώ - ΙΔΙΟ idiom με HandleDrStartAsync, ΔΕΝ
                // εμπιστευόμαστε το ήδη υπάρχον _drAllowed flag (θα μπορούσε
                // να είναι stale/ποτέ μη-οριστεί).
                var access = await Task.Run(
                    () => JarvisLicenseGuard.CheckAccessSilent(_xSupport, AccessConfig.DocReaderToolName));
                if (!access.Allowed)
                {
                    string denyMsg = JarvisLicenseGuard.BuildMessage(access);
                    DebugLog.Log($"[dr] CREATEAADEAFM entitlement DENIED (toolName={AccessConfig.DocReaderToolName}): {denyMsg}");
                    SendDrIssuerResult(fileId, false, null, null, null, null, denyMsg);
                    return;
                }

                string afm = (string)cmd["afm"];
                // ΝΕΟ 16/08: sodType ΤΩΡΑ nullable - null σημαίνει "ασαφές"
                // (CREATEAADEAFM χωρίς CUS, βλ. index.html send()) -> ΓΕΝΙΚΗ
                // αναζήτηση (οποιοδήποτε SODTYPE, ExecuteFindTraderByAfm),
                // ΟΧΙ πια σιωπηλό default σε 12/Προμηθευτή. Με ρητό sodType
                // -> scoped αναζήτηση (ExecuteFindTraderByAfmAndSodType).
                int? sodType = (int?)cmd["sodType"];

                var trader = sodType.HasValue
                    ? JObject.Parse(JarvisTools.ExecuteFindTraderByAfmAndSodType(_xSupport, afm, sodType.Value))
                    : JObject.Parse(JarvisTools.ExecuteFindTraderByAfm(_xSupport, afm));

                JObject seriesHistory = null;
                if (trader["found"]?.Value<bool>() == true)
                {
                    int trdrId = trader["trdrId"].Value<int>();
                    seriesHistory = JObject.Parse(
                        JarvisTools.ExecuteFindTraderSeriesHistory(_xSupport, trdrId, null));
                }

                // ΧΩΡΙΣ duplicateCheck εδώ - το CREATEAADEAFM δεν έχει
                // πραγματικό έγγραφο (χειροκίνητη αναζήτηση ΑΦΜ), τίποτα να
                // ελεγχθεί για διπλοκαταχώρηση.
                var detection = new JObject { ["issuerAfm"] = afm, ["issuerName"] = trader["name"] };
                SendDrIssuerResult(fileId, true, detection, trader, seriesHistory, null, null);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrManualLookupAsync EXCEPTION: " + ex);
                SendDrIssuerResult(fileId, false, null, null, null, null, "Απρόσμενο σφάλμα: " + ex.Message);
            }
        }

        // ── DR - Στάδιο 4: εξαγωγή γραμμών + αντιστοίχιση με δικό μας
        //    κατάλογο (βλ. index.html "Εξαγωγή γραμμών" κουμπί) - ΝΕΟ
        //    16/08. Συνδυάζει ΔΥΟ βήματα σε ΕΝΑ round-trip (ίδιο idiom με
        //    HandleDrIdentifyIssuerAsync): AI εξαγωγή (JarvisAgentClient.
        //    ExtractDocumentLinesAsync) -> αν επιτύχει, αντιστοίχιση κάθε
        //    γραμμής με MTRSUPCODE (JarvisTools.ExecuteMatchExtractedItems). ─
        private async Task HandleDrExtractLinesAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                string base64 = (string)cmd["base64"];
                string mimeType = (string)cmd["mimeType"];
                int trdrId = (int?)cmd["trdrId"] ?? 0;

                string companyAfm = await Task.Run(() => JarvisTools.GetCompanyAfm(_xSupport));
                var extraction = await _agentClient.ExtractDocumentLinesAsync(
                    _agentAccountRef, base64, mimeType, companyAfm);

                if (extraction["success"]?.Value<bool>() != true)
                {
                    SendDrExtractLinesResult(fileId, false, null, extraction["errorMessage"]?.ToString());
                    return;
                }

                JArray lineItems = extraction["line_items"] as JArray ?? new JArray();
                JObject matched = trdrId > 0
                    ? JObject.Parse(await Task.Run(() =>
                        JarvisTools.ExecuteMatchExtractedItems(_xSupport, trdrId, lineItems)))
                    : new JObject { ["results"] = lineItems };

                extraction["line_items"] = matched["results"];
                // ΣΗΜΕΙΩΣΗ 16/08: το duplicate-check ΔΕΝ γίνεται πια εδώ -
                // μετακινήθηκε στο HandleDrIdentifyIssuerAsync (Στάδιο 3β),
                // ρητή απόφαση χρήστη ("μετά την ταυτοποίηση συναλλασσόμενου")
                // - τρέχει ΝΩΡΙΤΕΡΑ, πριν καν φτάσουμε σε αυτό το (ακριβό,
                // Opus) full-extraction call.
                SendDrExtractLinesResult(fileId, true, extraction, null);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrExtractLinesAsync EXCEPTION: " + ex);
                SendDrExtractLinesResult(fileId, false, null, "Απρόσμενο σφάλμα: " + ex.Message);
            }
        }

        private void SendDrExtractLinesResult(string fileId, bool success, JObject extraction, string errorMessage)
        {
            var payload = new JObject
            {
                ["type"] = "dr_extract_lines_result",
                ["fileId"] = fileId,
                ["success"] = success,
                ["errorMessage"] = errorMessage,
                ["extraction"] = extraction
            };
            webView.CoreWebView2.PostWebMessageAsString(payload.ToString(Formatting.None));
        }

        // ── DR - Στάδιο 5 (#22): καταχώρηση + αυτόματο άνοιγμα - ΝΕΟ 16/08.
        //    Ο χειριστής έχει ήδη δει το review preview στο UI και πάτησε
        //    ρητά "Καταχώρηση ✓" πριν φτάσει εδώ (βλ. index.html) - το
        //    ExecuteRegisterDrDocument ΔΕΝ ξαναρωτάει, γράφει κατευθείαν.
        //    Μετά από επιτυχές PostData(), ΑΝΟΙΓΟΥΜΕ αυτόματα το νέο
        //    παραστατικό - ίδιο μηχανισμό/idiom με το OpenDocument
        //    (open_document), reuse ΧΩΡΙΣ αντιγραφή λογικής.
        private async Task HandleDrRegisterDocumentAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                string result = await Task.Run(() => JarvisTools.ExecuteRegisterDrDocument(_xSupport, cmd));
                JObject parsed = JObject.Parse(result);
                parsed["type"] = "dr_register_document_result";
                parsed["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));

                if (parsed["success"]?.Value<bool>() == true)
                {
                    OpenDocument(new JObject
                    {
                        ["sosource"] = parsed["sosource"],
                        ["id"] = parsed["findocId"]
                    });
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrRegisterDocumentAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_register_document_result",
                    ["fileId"] = fileId,
                    ["success"] = false,
                    ["errorMessage"] = "Απρόσμενο σφάλμα: " + ex.Message
                }.ToString(Formatting.None));
            }
        }

        // ── DR - semi-manual οδηγός lookups (βλ. renderDrManualWizard στο
        //    index.html) - ΝΕΟ 16/08. Τρία απλά read-only queries, ίδιο
        //    idiom με HandleDrCreateTraderAsync (Task.Run + JObject.Parse +
        //    ["type"]/["fileId"] + post πίσω).
        private async Task HandleDrGetSeriesForSosourceAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                int sosource = (int?)cmd["sosource"] ?? 0;
                string result = await Task.Run(() => JarvisTools.ExecuteGetSeriesForSosource(_xSupport, sosource));
                JObject parsed = JObject.Parse(result);
                parsed["type"] = "dr_get_series_for_sosource_result";
                parsed["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrGetSeriesForSosourceAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_get_series_for_sosource_result", ["fileId"] = fileId, ["series"] = new JArray()
                }.ToString(Formatting.None));
            }
        }

        private async Task HandleDrSearchItemsAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            string requestId = cmd?["requestId"]?.ToString();
            try
            {
                string query = cmd["query"]?.ToString();
                string result = await Task.Run(() => JarvisTools.ExecuteSearchItems(_xSupport, query));
                JObject parsed = JObject.Parse(result);
                parsed["type"] = "dr_search_items_result";
                parsed["fileId"] = fileId;
                parsed["requestId"] = requestId;
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrSearchItemsAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_search_items_result", ["fileId"] = fileId, ["requestId"] = requestId, ["items"] = new JArray()
                }.ToString(Formatting.None));
            }
        }

        private async Task HandleDrGetTraderKnownItemsAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                int trdrId = (int?)cmd["trdrId"] ?? 0;
                int sosource = (int?)cmd["sosource"] ?? 0;
                string result = await Task.Run(() => JarvisTools.ExecuteGetTraderKnownItems(_xSupport, trdrId, sosource));
                JObject parsed = JObject.Parse(result);
                parsed["type"] = "dr_get_trader_known_items_result";
                parsed["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrGetTraderKnownItemsAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_get_trader_known_items_result", ["fileId"] = fileId, ["items"] = new JArray()
                }.ToString(Formatting.None));
            }
        }

        private async Task HandleDrCreateTraderAsync(JObject cmd)
        {
            string fileId = cmd?["fileId"]?.ToString();
            try
            {
                string result = await Task.Run(() => JarvisTools.ExecuteCreateTraderFromAade(_xSupport, cmd));
                JObject parsed = JObject.Parse(result);
                parsed["type"] = "dr_create_trader_result";
                parsed["fileId"] = fileId;
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dr] HandleDrCreateTraderAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dr_create_trader_result", ["fileId"] = fileId,
                    ["success"] = false, ["message"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // ── TASK wizard - φόρμα δημιουργίας CRM task, ΕΚΤΟΣ chat/AI - ρητή
        //    απόφαση του χρήστη 15/08 (deterministic φόρμα, όχι AI). Τα
        //    handlers εδώ απλά καλούν το JarvisTools (business logic πάνω
        //    σε XSupport) και προσθέτουν το "type" στην JSON απάντηση - όλη
        //    η ουσιαστική λογική (SQL/write) ζει στο JarvisTools, ίδιο
        //    idiom με τα υπόλοιπα tools. ─────────────────────────────────
        private void HandleTaskSearchTrader(JObject cmd)
        {
            try
            {
                string text = (string)cmd["text"] ?? "";
                int sodType = (int?)cmd["sodType"] ?? 0;
                JObject parsed = JObject.Parse(JarvisTools.ExecuteTaskSearchTrader(_xSupport, text, sodType));
                parsed["type"] = "task_trader_results";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[task] HandleTaskSearchTrader EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    new JObject { ["type"] = "task_trader_results", ["results"] = new JArray() }.ToString(Formatting.None));
            }
        }

        private void HandleTaskSearchUser(JObject cmd)
        {
            try
            {
                string text = (string)cmd["text"] ?? "";
                JObject parsed = JObject.Parse(JarvisTools.ExecuteTaskSearchUser(_xSupport, text));
                parsed["type"] = "task_user_results";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[task] HandleTaskSearchUser EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    new JObject { ["type"] = "task_user_results", ["results"] = new JArray() }.ToString(Formatting.None));
            }
        }

        // ΝΕΟ 16/08, ρητό αίτημα χρήστη - Εγκατάσταση/Έργο, ΙΔΙΟ idiom με
        // HandleTaskSearchTrader/User πιο πάνω.
        private void HandleTaskSearchInst(JObject cmd)
        {
            try
            {
                string text = (string)cmd["text"] ?? "";
                JObject parsed = JObject.Parse(JarvisTools.ExecuteTaskSearchInst(_xSupport, text));
                parsed["type"] = "task_inst_results";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[task] HandleTaskSearchInst EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    new JObject { ["type"] = "task_inst_results", ["results"] = new JArray() }.ToString(Formatting.None));
            }
        }

        private void HandleTaskSearchPrjc(JObject cmd)
        {
            try
            {
                string text = (string)cmd["text"] ?? "";
                JObject parsed = JObject.Parse(JarvisTools.ExecuteTaskSearchPrjc(_xSupport, text));
                parsed["type"] = "task_prjc_results";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[task] HandleTaskSearchPrjc EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    new JObject { ["type"] = "task_prjc_results", ["results"] = new JArray() }.ToString(Formatting.None));
            }
        }

        // ΔΕΝ περνάει από το AI tool-use loop - η φόρμα έχει ήδη όλα τα
        // δομημένα δεδομένα (title/description/actorUserId/κ.λπ.), οπότε
        // καλεί το ΙΔΙΟ JarvisTools.ExecuteCreateCrmTask ΑΠΕΥΘΕΙΑΣ (cmd
        // ταιριάζει ήδη με το input schema του tool - βλ. index.html
        // submitTaskWizard).
        private void HandleTaskCreate(JObject cmd)
        {
            try
            {
                JObject parsed = JObject.Parse(JarvisTools.ExecuteCreateCrmTask(_xSupport, cmd));
                parsed["type"] = "task_create_result";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[task] HandleTaskCreate EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    new JObject { ["type"] = "task_create_result", ["success"] = false, ["message"] = ex.Message }
                        .ToString(Formatting.None));
            }
        }

        // ── TASKS wizard (πολλαπλοί τύποι CRM) - ΝΕΟ 15/08, βλ. session
        //    notes. Ίδιο idiom με τα Task* handlers πιο πάνω. ─────────────
        private void HandleTaskGetSeries(JObject cmd)
        {
            try
            {
                int soredir = (int?)cmd["soredir"] ?? -1;
                JObject parsed = JObject.Parse(JarvisTools.ExecuteGetCrmSeriesForType(_xSupport, soredir));
                parsed["type"] = "task_series_results";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[task] HandleTaskGetSeries EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    new JObject { ["type"] = "task_series_results", ["results"] = new JArray() }.ToString(Formatting.None));
            }
        }

        // ── Dashboard "Tasks - Εργασίες" σελίδα - ΝΕΟ 16/08. ─────────────────
        private async Task HandleDashboardGetMyTasksAsync()
        {
            try
            {
                string result = await Task.Run(() => JarvisTools.ExecuteGetMyAssignedTasks(_xSupport));
                JObject parsed = JObject.Parse(result);
                parsed["type"] = "dashboard_my_tasks_result";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dashboard_tasks] HandleDashboardGetMyTasksAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    new JObject { ["type"] = "dashboard_my_tasks_result", ["tasks"] = new JArray() }.ToString(Formatting.None));
            }
        }

        private async Task HandleDashboardCompleteTaskAsync(JObject cmd)
        {
            int soactionId = (int?)cmd["soactionId"] ?? 0;
            try
            {
                int soredir = (int?)cmd["soredir"] ?? -1;
                string note = (string)cmd["note"]; // προαιρετική σημείωση από το dialog, βλ. index.html
                await Task.Run(() => JarvisTools.ExecuteCompleteCrmTask(_xSupport, soredir, soactionId, note));
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dashboard_complete_task_result", ["success"] = true, ["soactionId"] = soactionId
                }.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[dashboard_tasks] HandleDashboardCompleteTaskAsync EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(new JObject
                {
                    ["type"] = "dashboard_complete_task_result", ["success"] = false,
                    ["soactionId"] = soactionId, ["message"] = ex.Message
                }.ToString(Formatting.None));
            }
        }

        // Ίδιο idiom με το OpenTrader (Dispatcher.BeginInvoke - UI thread,
        // ίδιο σκεπτικό με τα ήδη υπάρχοντα open_document/open_trader).
        private void HandleDashboardOpenCrmAction(JObject cmd)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    int soredir = (int?)cmd["soredir"] ?? -1;
                    int soactionId = (int?)cmd["soactionId"] ?? 0;
                    JarvisTools.ExecuteOpenCrmAction(_xSupport, soredir, soactionId);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[dashboard_tasks] HandleDashboardOpenCrmAction EXCEPTION: " + ex);
                    try
                    {
                        webView.CoreWebView2.PostWebMessageAsString(
                            "✖ Αδυναμία ανοίγματος εργασίας: " + ex.Message);
                    }
                    catch { /* δεν το αφήνουμε να ξεφύγει */ }
                }
            }));
        }

        private void HandleTaskCreateAdvanced(JObject cmd)
        {
            try
            {
                JObject parsed = JObject.Parse(JarvisTools.ExecuteCreateCrmRecord(_xSupport, cmd));
                parsed["type"] = "task_create_result";
                webView.CoreWebView2.PostWebMessageAsString(parsed.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                DebugLog.Log("[task] HandleTaskCreateAdvanced EXCEPTION: " + ex);
                webView.CoreWebView2.PostWebMessageAsString(
                    new JObject { ["type"] = "task_create_result", ["success"] = false, ["message"] = ex.Message }
                        .ToString(Formatting.None));
            }
        }
    }
}
