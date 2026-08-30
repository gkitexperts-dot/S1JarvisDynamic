using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private bool _uatPipelineSubscribed;

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            RegisterDeterministicUatPipeline();
        }

        private void RegisterDeterministicUatPipeline()
        {
            // Disable the older parallel WebMessageReceived hook. UAT now plugs
            // into the exact synchronous XLSX reader path that normal uploads use.
            _uatWebMessageHookInstalled = true;

            if (_uatPipelineSubscribed)
                return;

            DocumentReaders.UatWorkbookInterceptor = TryInterceptUatWorkbook;
            _uatPipelineSubscribed = true;
            Loaded += UatPipeline_Loaded;
            Unloaded += UatPipeline_Unloaded;
        }

        private void UatPipeline_Loaded(object sender, RoutedEventArgs e)
        {
            // Re-register after any WPF unload/reload/reparent cycle. There is
            // normally one Jarvis shell, but this makes the interception robust.
            DocumentReaders.UatWorkbookInterceptor = TryInterceptUatWorkbook;
            _uatPipelineSubscribed = true;
        }

        private void UatPipeline_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(DocumentReaders.UatWorkbookInterceptor,
                (Func<string, string, bool>)TryInterceptUatWorkbook))
            {
                DocumentReaders.UatWorkbookInterceptor = null;
            }
            _uatPipelineSubscribed = false;
        }

        private bool TryInterceptUatWorkbook(string fileName, string workbookText)
        {
            if (_uatRunning)
                return false;

            List<UatTestCase> tests;
            try
            {
                tests = ParseCurrentUatSheet(workbookText);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[uat] deterministic workbook detection failed: " + ex);
                return false;
            }

            if (tests == null || tests.Count == 0)
                return false;

            // This callback runs on DocumentReaders' Task.Run worker. Schedule
            // all UI/AI orchestration back to the Jarvis dispatcher, but return
            // true immediately so DocumentReaders can suppress the full workbook
            // text from the normal LLM attachment path.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_uatRunning)
                    return;

                _uatRunning = true;
                try
                {
                    // The normal office_document_text_result continuation is
                    // still allowed to finish so its JS async state is released.
                    // It receives only a sentinel. Clear the attachment chip just
                    // after that continuation has had a chance to run, ensuring
                    // the user never has to press Send for a UAT workbook.
                    await Task.Delay(180);
                    try
                    {
                        if (webView != null && webView.CoreWebView2 != null)
                            await webView.CoreWebView2.ExecuteScriptAsync("clearAttachment();");
                    }
                    catch (Exception clearEx)
                    {
                        DebugLog.Log("[uat] clearAttachment failed: " + clearEx);
                    }

                    await RunUatWorkbookAsync(fileName, tests);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[uat] deterministic pipeline run failed: " + ex);
                    PostUatMessage("✖ UAT runner: " + ex.Message);
                }
                finally
                {
                    _uatRunning = false;
                }
            }));

            return true;
        }
    }
}
