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
                // Give the normal read_office_document callback time to complete
                // and the JS attachment continuation time to settle before doing
                // any UAT work. Parsing itself remains on a worker thread.
                await Task.Delay(700);

                byte[] bytes = Convert.FromBase64String(base64);
                string workbookText = await Task.Run(() =>
                    DocumentReaders.ReadOfficeDocumentAsText(bytes, mimeType, name));

                List<UatTestCase> tests = ParseCurrentUatSheet(workbookText);
                if (tests == null || tests.Count == 0)
                    return; // ordinary XLSX: normal attachment behavior only.

                await Dispatcher.InvokeAsync(async () =>
                {
                    if (_uatRunning)
                        return;

                    _uatRunning = true;
                    try
                    {
                        // Explicit notice BEFORE test execution. Also clear the
                        // normal attachment chip so the workbook is not accidentally
                        // sent later as ordinary chat text.
                        try
                        {
                            if (webView != null && webView.CoreWebView2 != null)
                                await webView.CoreWebView2.ExecuteScriptAsync("clearAttachment();");
                        }
                        catch (Exception clearEx)
                        {
                            DebugLog.Log("[uat] deferred clearAttachment failed: " + clearEx);
                        }

                        PostUatMessage(
                            "UAT READY\n\n" +
                            "Αναγνωρίστηκε το **" + EscapeMarkdown(name) + "** με **" + tests.Count +
                            "** γραμμές. Ξεκινώ τώρα τα αυτόματα tests που επιτρέπονται από την ενεργή άδεια. " +
                            "Τα υπόλοιπα θα σημειωθούν MANUAL/BLOCKED χωρίς να εκτελεστούν.");

                        // Small visual pause so the operator can actually read the
                        // notice before the first provider/tool call begins.
                        await Task.Delay(900);
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
