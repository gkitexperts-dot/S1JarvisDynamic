using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using S1Jarvis.Access;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    /// <summary>
    /// Deterministic UAT harness for Jarvis workbooks.
    ///
    /// A workbook that contains the "Τρέχον UAT" sheet and the expected
    /// test-plan headers is recognised while it travels through the existing
    /// read_office_document upload path. The workbook is already parsed locally
    /// by DocumentReaders; the LLM is not asked to decide whether it can open
    /// Excel files.
    ///
    /// Each independent central-licence test is executed as a fresh Jarvis
    /// conversation so normal client routing chooses Atlas/Forge/Compass/Echo
    /// from the actual test prompt. Tests that need a previous user turn,
    /// physical UI interaction or a licence that is not active are not faked:
    /// they are reported as MANUAL/BLOCKED. Results are shown in the chat and
    /// exported to a new xlsx under LocalAppData\S1Jarvis\UAT.
    /// </summary>
    public partial class JarvisShell
    {
        private bool _uatWebMessageHookInstalled;
        private bool _uatWebMessageHookInstalling;
        private bool _uatRunning;

        static JarvisShell()
        {
            // Keeps this feature isolated in a partial class: no brittle edit of
            // the large JarvisShell.xaml.cs WebMessageReceived switch is needed.
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_UatClassLoaded));
        }

        private static void JarvisShell_UatClassLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null)
                shell.InstallUatWebMessageHookWhenReady();
        }

        private async void InstallUatWebMessageHookWhenReady()
        {
            if (_uatWebMessageHookInstalled || _uatWebMessageHookInstalling)
                return;

            _uatWebMessageHookInstalling = true;
            try
            {
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    if (webView != null && webView.CoreWebView2 != null)
                        break;
                    await Task.Delay(50);
                }

                if (webView == null || webView.CoreWebView2 == null || _uatWebMessageHookInstalled)
                    return;

                webView.CoreWebView2.WebMessageReceived += UatWebMessageReceived;
                _uatWebMessageHookInstalled = true;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[uat] hook install failed: " + ex);
            }
            finally
            {
                _uatWebMessageHookInstalling = false;
            }
        }

        private async void UatWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_uatRunning)
                return;

            JObject cmd;
            try
            {
                cmd = JObject.Parse(e.TryGetWebMessageAsString());
            }
            catch
            {
                return;
            }

            if (!string.Equals((string)cmd["type"], "read_office_document", StringComparison.Ordinal))
                return;

            string name = (string)cmd["name"] ?? string.Empty;
            if (!name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                return;

            string base64 = (string)cmd["base64"];
            if (string.IsNullOrWhiteSpace(base64))
                return;

            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                string workbookText = await Task.Run(() =>
                    DocumentReaders.ReadOfficeDocumentAsText(bytes, (string)cmd["mimeType"], name));

                List<UatTestCase> tests = ParseCurrentUatSheet(workbookText);
                if (tests.Count == 0)
                    return; // ordinary xlsx: leave the normal attachment flow untouched.

                _uatRunning = true;
                await RunUatWorkbookAsync(name, tests);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[uat] workbook detection/run failed: " + ex);
                PostUatMessage("✖ UAT runner: " + ex.Message);
            }
            finally
            {
                _uatRunning = false;
            }
        }

        private async Task RunUatWorkbookAsync(string sourceName, List<UatTestCase> tests)
        {
            if (string.IsNullOrWhiteSpace(_agentAccountRef))
            {
                PostUatMessage("✖ UAT runner: δεν υπάρχει ενεργή κεντρική άδεια/AI agent.");
                return;
            }

            PostUatMessage(
                "### JARVIS UAT RUNNER\n\n" +
                "Αναγνώρισα το **" + EscapeMarkdown(sourceName) + "** και " +
                "βρήκα **" + tests.Count + "** tests στο φύλλο `Τρέχον UAT`. " +
                "Θα εκτελέσω αυτόματα μόνο όσα μπορούν να τρέξουν με την " +
                "κεντρική άδεια και χωρίς να προσποιηθώ ανθρώπινη επιβεβαίωση.");

            var results = new List<UatTestResult>();
            int ordinal = 0;
            foreach (UatTestCase test in tests)
            {
                ordinal++;
                UatTestResult result;

                if (!IsCentralLicence(test.Licence))
                {
                    result = UatTestResult.Skipped(test, "BLOCKED", "Απαιτεί διαφορετική/μη ενεργή άδεια.");
                }
                else if (!IsCentralChat(test.Location))
                {
                    result = UatTestResult.Skipped(test, "MANUAL", "Απαιτεί ειδική κουρτίνα ή UI mode.");
                }
                else if (RequiresPreviousTurnOrManualUi(test.Prompt))
                {
                    result = UatTestResult.Skipped(test, "MANUAL", "Εξαρτάται από προηγούμενο turn ή χειροκίνητη UI ενέργεια.");
                }
                else if (string.Equals(test.Prompt.Trim(), "HEALTH", StringComparison.OrdinalIgnoreCase) ||
                         test.Prompt.IndexOf("HEALTH", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result = await RunHealthUatAsync(test);
                }
                else
                {
                    result = await RunConversationUatAsync(test, ordinal, tests.Count);
                }

                results.Add(result);
                PostUatProgress(result, ordinal, tests.Count);
            }

            string resultPath = SaveUatResults(sourceName, results);
            PostUatSummary(results, resultPath);
        }

        private async Task<UatTestResult> RunConversationUatAsync(UatTestCase test, int ordinal, int total)
        {
            try
            {
                // Fresh history is deliberate: each spreadsheet row must test
                // the router from its own real prompt, without sticky context
                // leaking from another UAT row.
                var history = new List<JObject>();
                string answer = await _agentClient.AskAsync(
                    _agentAccountRef,
                    _xSupport,
                    history,
                    test.Prompt,
                    onProgress: t => PostUatStatus(ordinal, total, t));

                string status = LooksLikeTechnicalFailure(answer) ? "FAIL" : "PASS";
                string reason = status == "PASS"
                    ? "Το Jarvis ολοκλήρωσε το ανεξάρτητο test turn χωρίς τεχνικό σφάλμα."
                    : "Το Jarvis επέστρεψε τεχνική αποτυχία.";

                // Expected-result semantics can include business/UI assertions
                // that cannot be proven from a plain reply. Flag those for human
                // review instead of claiming a false automatic PASS.
                if (status == "PASS" && NeedsHumanAssertion(test.ExpectedResult))
                {
                    status = "REVIEW";
                    reason = "Το turn εκτελέστηκε, αλλά το αναμενόμενο αποτέλεσμα περιέχει UI/business assertion που χρειάζεται οπτικό έλεγχο.";
                }

                return new UatTestResult(test, status, reason, answer);
            }
            catch (Exception ex)
            {
                return new UatTestResult(test, "FAIL", "Exception: " + ex.Message, string.Empty);
            }
        }

        private async Task<UatTestResult> RunHealthUatAsync(UatTestCase test)
        {
            try
            {
                JarvisRuntimeAccessResult runtime = await Task.Run(() =>
                    JarvisLicenseGuard.CheckRuntimeAccessSilent(_xSupport));

                if (runtime == null || runtime.AgentRouting == null || !runtime.AgentRouting.Available)
                    return new UatTestResult(test, "FAIL", "Το signed runtime routing δεν είναι διαθέσιμο.", string.Empty);

                string model = runtime.AgentRouting.Model;
                if (string.IsNullOrWhiteSpace(model))
                    return new UatTestResult(test, "FAIL", "Δεν υπάρχει configured default model.", string.Empty);

                var probe = new JarvisAgentHealthProbe();
                JarvisAgentHealthResult health = await probe.ProbeAsync(
                    _xSupport,
                    runtime.AgentRouting.AgentAccountRef,
                    model);

                if (health == null || !health.Ready)
                    return new UatTestResult(test, "FAIL", "HEALTH απέτυχε: " + (health?.ReasonCode ?? "unknown"), string.Empty);

                int targetCount = health.Targets == null ? 0 : health.Targets.Count;
                bool allReady = targetCount > 0 && health.Targets.All(x => x.Ready);
                string status = allReady ? "PASS" : "REVIEW";
                string detail = allReady
                    ? "Όλοι οι " + targetCount + " effective AI targets είναι Connected."
                    : "Το top-level HEALTH είναι Connected, αλλά δεν είναι όλοι οι per-agent targets ready.";

                return new UatTestResult(test, status, detail, BuildHealthTargetSummary(health));
            }
            catch (Exception ex)
            {
                return new UatTestResult(test, "FAIL", "HEALTH exception: " + ex.Message, string.Empty);
            }
        }

        private static string BuildHealthTargetSummary(JarvisAgentHealthResult health)
        {
            if (health?.Targets == null || health.Targets.Count == 0)
                return health?.ReasonCode ?? string.Empty;

            return string.Join("; ", health.Targets.Select(x =>
                (x.Agent ?? "—") + "=" +
                (x.Ready ? "Connected" : (x.ReasonCode ?? "Unavailable")) +
                "/" + (x.Provider ?? "—") +
                "/" + (x.Model ?? "—") +
                "/" + (x.Inherited ? "Inherited" : "Dedicated")));
        }

        private void PostUatProgress(UatTestResult result, int ordinal, int total)
        {
            string icon = result.Status == "PASS" ? "✓" :
                result.Status == "FAIL" ? "✖" :
                result.Status == "BLOCKED" ? "⊘" : "◐";

            var sb = new StringBuilder();
            sb.Append("**UAT ").Append(ordinal).Append('/').Append(total).Append(" · #")
              .Append(result.Test.Id).Append(" · ").Append(result.Test.ExpectedAgent).Append("** — ")
              .Append(icon).Append(' ').Append(result.Status).Append("\n\n")
              .Append(result.Reason);

            if (!string.IsNullOrWhiteSpace(result.Answer))
                sb.Append("\n\n> ").Append(OneLine(TruncateUat(result.Answer, 300)));

            PostUatMessage(sb.ToString());
        }

        private void PostUatStatus(int ordinal, int total, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            PostUatMessage("`UAT " + ordinal + "/" + total + "` " + TruncateUat(text, 180));
        }

        private void PostUatSummary(List<UatTestResult> results, string resultPath)
        {
            int pass = results.Count(x => x.Status == "PASS");
            int fail = results.Count(x => x.Status == "FAIL");
            int review = results.Count(x => x.Status == "REVIEW" || x.Status == "MANUAL");
            int blocked = results.Count(x => x.Status == "BLOCKED");

            var sb = new StringBuilder();
            sb.AppendLine("### UAT RESULTS");
            sb.AppendLine();
            sb.Append("**PASS:** ").Append(pass)
              .Append(" · **FAIL:** ").Append(fail)
              .Append(" · **REVIEW/MANUAL:** ").Append(review)
              .Append(" · **BLOCKED:** ").Append(blocked).AppendLine();
            sb.AppendLine();
            sb.AppendLine("| ID | Status | Expected agent | Priority | Result |");
            sb.AppendLine("|---:|---:|---:|---:|---:|");
            foreach (UatTestResult r in results)
            {
                sb.Append("| ").Append(EscapeTable(r.Test.Id))
                  .Append(" | ").Append(EscapeTable(r.Status))
                  .Append(" | ").Append(EscapeTable(r.Test.ExpectedAgent))
                  .Append(" | ").Append(EscapeTable(r.Test.Priority))
                  .Append(" | ").Append(EscapeTable(r.Reason))
                  .AppendLine(" |");
            }

            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                sb.AppendLine();
                sb.Append("[Άνοιγμα αναλυτικού UAT Excel](").Append(resultPath).Append(')');
            }

            PostUatMessage(sb.ToString());
        }

        private string SaveUatResults(string sourceName, List<UatTestResult> results)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "S1Jarvis", "UAT");
                Directory.CreateDirectory(dir);

                string stem = Path.GetFileNameWithoutExtension(sourceName);
                foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
                string path = Path.Combine(dir,
                    stem + "_Results_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx");

                var rows = new List<string[]>
                {
                    new[] { "ID", "Status", "Πού γράφω", "Prompt", "Expected Agent", "Priority", "Reason", "Jarvis Answer" }
                };
                foreach (UatTestResult r in results)
                {
                    rows.Add(new[]
                    {
                        r.Test.Id,
                        r.Status,
                        r.Test.Location,
                        r.Test.Prompt,
                        r.Test.ExpectedAgent,
                        r.Test.Priority,
                        r.Reason,
                        r.Answer ?? string.Empty
                    });
                }

                XlsxWriter.Write(path, rows);
                return path;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[uat] result xlsx write failed: " + ex);
                return null;
            }
        }

        private void PostUatMessage(string text)
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                    webView.CoreWebView2.PostWebMessageAsString(text ?? string.Empty);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[uat] PostUatMessage failed: " + ex);
            }
        }

        private static List<UatTestCase> ParseCurrentUatSheet(string workbookText)
        {
            var tests = new List<UatTestCase>();
            if (string.IsNullOrWhiteSpace(workbookText)) return tests;

            string[] lines = workbookText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inSheet = false;
            bool headerSeen = false;

            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.StartsWith("### Φύλλο:", StringComparison.Ordinal))
                {
                    string sheetName = line.Substring("### Φύλλο:".Length).Trim();
                    inSheet = string.Equals(sheetName, "Τρέχον UAT", StringComparison.OrdinalIgnoreCase);
                    headerSeen = false;
                    continue;
                }
                if (!inSheet || string.IsNullOrWhiteSpace(line)) continue;

                string[] cells = SplitUatLine(line);
                if (!headerSeen)
                {
                    if (cells.Length >= 6 &&
                        cells.Any(x => x.IndexOf("Prompt", StringComparison.OrdinalIgnoreCase) >= 0) &&
                        cells.Any(x => x.IndexOf("Αναμενόμενος agent", StringComparison.OrdinalIgnoreCase) >= 0))
                        headerSeen = true;
                    continue;
                }

                if (cells.Length < 6) continue;
                string id = Cell(cells, 0);
                if (string.IsNullOrWhiteSpace(id)) continue;

                tests.Add(new UatTestCase
                {
                    Id = id,
                    Location = Cell(cells, 1),
                    Prompt = Cell(cells, 2),
                    ExpectedAgent = Cell(cells, 3),
                    Check = Cell(cells, 4),
                    ExpectedResult = Cell(cells, 5),
                    Licence = Cell(cells, 6),
                    Priority = Cell(cells, 7)
                });
            }

            return tests.Where(x => !string.IsNullOrWhiteSpace(x.Prompt)).ToList();
        }

        private static string[] SplitUatLine(string line) =>
            line.Split(new[] { " | " }, StringSplitOptions.None);

        private static string Cell(string[] cells, int index) =>
            index >= 0 && index < cells.Length ? (cells[index] ?? string.Empty).Trim() : string.Empty;

        private static bool IsCentralLicence(string value) =>
            string.IsNullOrWhiteSpace(value) ||
            value.IndexOf("Κεντρ", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsCentralChat(string value) =>
            string.IsNullOrWhiteSpace(value) ||
            value.IndexOf("Κεντρ", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool RequiresPreviousTurnOrManualUi(string prompt)
        {
            string p = (prompt ?? string.Empty).Trim();
            return p.IndexOf("Στο draft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("Μετά το draft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("Κάν' το Excel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("πάτησε Stop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("πατήσε Stop", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeTechnicalFailure(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer)) return true;
            string a = answer.TrimStart();
            return a.StartsWith("✖", StringComparison.Ordinal) ||
                   a.StartsWith("Σφάλμα", StringComparison.OrdinalIgnoreCase) ||
                   a.IndexOf("Άγνωστο σφάλμα", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool NeedsHumanAssertion(string expected)
        {
            if (string.IsNullOrWhiteSpace(expected)) return false;
            string e = expected.ToLowerInvariant();
            return e.Contains("ui") || e.Contains("πίνακ") || e.Contains("εμφαν") ||
                   e.Contains("link") || e.Contains("να μη") || e.Contains("καμία") ||
                   e.Contains("draft") || e.Contains("επιβεβαί");
        }

        private static string EscapeMarkdown(string value) =>
            (value ?? string.Empty).Replace("*", "\\*").Replace("_", "\\_");

        private static string EscapeTable(string value) =>
            OneLine(value ?? string.Empty).Replace("|", "\\|");

        private static string OneLine(string value) =>
            (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

        private static string TruncateUat(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? string.Empty;
            return value.Substring(0, max) + "…";
        }

        private sealed class UatTestCase
        {
            public string Id;
            public string Location;
            public string Prompt;
            public string ExpectedAgent;
            public string Check;
            public string ExpectedResult;
            public string Licence;
            public string Priority;
        }

        private sealed class UatTestResult
        {
            public readonly UatTestCase Test;
            public readonly string Status;
            public readonly string Reason;
            public readonly string Answer;

            public UatTestResult(UatTestCase test, string status, string reason, string answer)
            {
                Test = test;
                Status = status;
                Reason = reason;
                Answer = answer;
            }

            public static UatTestResult Skipped(UatTestCase test, string status, string reason) =>
                new UatTestResult(test, status, reason, string.Empty);
        }
    }
}
