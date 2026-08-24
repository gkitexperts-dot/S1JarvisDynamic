using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private bool _uatDeferredHookInstalled;
        private bool _uatDeferredHookInstalling;

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            // IMPORTANT: disable the older UAT WebMessage hook. That hook parsed
            // and then executed the full UAT while the original WebView callback
            // was still alive, which can trigger E_ABORT/COM re-entrancy in the
            // Soft1 host. The deferred hook below only captures the upload data,
            // returns immediately, and does all work after the callback unwinds.
            _uatWebMessageHookInstalled = true;
            Loaded += JarvisShell_UatDeferredLoaded;
        }

        private void JarvisShell_UatDeferredLoaded(object sender, RoutedEventArgs e)
        {
            InstallDeferredUatHookWhenReady();
        }

        private async void InstallDeferredUatHookWhenReady()
        {
            if (_uatDeferredHookInstalled || _uatDeferredHookInstalling)
                return;

            _uatDeferredHookInstalling = true;
            try
            {
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    if (webView != null && webView.CoreWebView2 != null)
                        break;
                    await Task.Delay(50);
                }

                if (webView == null || webView.CoreWebView2 == null || _uatDeferredHookInstalled)
                    return;

                webView.CoreWebView2.WebMessageReceived += DeferredUatWebMessageReceived;
                _uatDeferredHookInstalled = true;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[uat] deferred hook install failed: " + ex);
            }
            finally
            {
                _uatDeferredHookInstalling = false;
            }
        }

        private void DeferredUatWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
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
            string mimeType = (string)cmd["mimeType"];
            if (string.IsNullOrWhiteSpace(base64))
                return;

            // Capture immutable values only. DO NOT parse, await, post UI, or call
            // the agent from this WebView callback. Returning immediately is the
            // core E_ABORT safety rule for the Soft1/Delphi host.
            _ = RunDeferredUatCandidateAsync(name, mimeType, base64);
        }

        private async Task RunDeferredUatCandidateAsync(string name, string mimeType, string base64)
        {
            try
            {
                // First visible acknowledgement happens only AFTER the original
                // WebView callback has unwound. This avoids the old E_ABORT pattern,
                // while making the UI feel alive immediately instead of "freezing".
                await Task.Delay(100);
                await Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        if (webView != null && webView.CoreWebView2 != null)
                            await webView.CoreWebView2.ExecuteScriptAsync("startConversation();");
                    }
                    catch (Exception layoutEx)
                    {
                        DebugLog.Log("[uat] startConversation failed: " + layoutEx);
                    }

                    PostUatMessage(
                        "📎 **Έλαβα το Excel** `" + EscapeMarkdown(name) + "`.\n\n" +
                        "Το διαβάζω τώρα και ελέγχω αν είναι Jarvis UAT workbook. Περίμενε λίγο — δεν χρειάζεται να πατήσεις Send.");
                });

                // Let the normal read_office_document continuation settle before
                // the second parse starts. Parsing itself remains on a worker thread.
                await Task.Delay(500);

                byte[] bytes = Convert.FromBase64String(base64);
                string workbookText = await Task.Run(() =>
                    DocumentReaders.ReadOfficeDocumentAsText(bytes, mimeType, name));

                List<UatTestCase> tests = ParseCurrentUatSheet(workbookText);
                if (tests == null || tests.Count == 0)
                {
                    // Ordinary XLSX: keep the normal attachment behavior. We only
                    // acknowledge that reading completed; the attachment remains
                    // available for the user to send normally.
                    await Dispatcher.InvokeAsync(() =>
                        PostUatMessage("✓ Το Excel διαβάστηκε. Δεν είναι UAT workbook — παραμένει διαθέσιμο για κανονική χρήση στο chat."));
                    return;
                }

                await Dispatcher.InvokeAsync(async () =>
                {
                    if (_uatRunning)
                        return;

                    _uatRunning = true;
                    try
                    {
                        // Only now that UAT recognition is confirmed do we consume
                        // the attachment. The sphere is already top-right because
                        // startConversation() ran in the first acknowledgement.
                        try
                        {
                            if (webView != null && webView.CoreWebView2 != null)
                                await webView.CoreWebView2.ExecuteScriptAsync("clearAttachment(); startConversation();");
                        }
                        catch (Exception clearEx)
                        {
                            DebugLog.Log("[uat] deferred clearAttachment failed: " + clearEx);
                        }

                        PostUatMessage(
                            "### UAT READY\n\n" +
                            "Αναγνώρισα το **" + EscapeMarkdown(name) + "** ως Jarvis UAT workbook με **" + tests.Count +
                            "** γραμμές. Το Excel φορτώθηκε και αφαιρέθηκε από το composer.\n\n" +
                            "▶ **Ξεκινώ τώρα τα tests.** Μείνε στο παράθυρο — θα εμφανίζω πρόοδο και στο τέλος θα δώσω συγκεντρωτικά αποτελέσματα. " +
                            "Όσα δεν μπορούν να εκτελεστούν με την ενεργή άδεια θα σημειωθούν MANUAL/BLOCKED.");

                        // Give the operator enough time to see the transition and
                        // understand that a potentially multi-minute UAT run starts.
                        await Task.Delay(1600);
                        await RunUatWorkbookAsync(name, tests);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log("[uat] deferred run failed: " + ex);
                        PostUatMessage("✖ UAT runner: " + ex.Message);
                    }
                    finally
                    {
                        _uatRunning = false;
                    }
                });
            }
            catch (Exception ex)
            {
                DebugLog.Log("[uat] deferred candidate failed: " + ex);
            }
        }
    }
}
