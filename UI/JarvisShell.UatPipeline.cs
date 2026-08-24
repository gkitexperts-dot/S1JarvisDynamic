using System;
using System.Collections.Generic;
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

            // The original UAT bootstrap listened to WebMessageReceived in
            // parallel with the main Jarvis handler. That is timing-sensitive.
            // Disable that legacy hook and subscribe instead to the deterministic
            // Office-reader pipeline that is already used by normal XLSX upload.
            _uatWebMessageHookInstalled = true;

            if (_uatPipelineSubscribed)
                return;

            DocumentReaders.XlsxWorkbookRead += UatPipeline_XlsxWorkbookRead;
            _uatPipelineSubscribed = true;
            Unloaded += UatPipeline_Unloaded;
        }

        private void UatPipeline_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_uatPipelineSubscribed)
                return;

            DocumentReaders.XlsxWorkbookRead -= UatPipeline_XlsxWorkbookRead;
            _uatPipelineSubscribed = false;
        }

        private void UatPipeline_XlsxWorkbookRead(string fileName, string workbookText)
        {
            // DocumentReaders runs on Task.Run from the WebView message handler,
            // therefore this callback may arrive on a worker thread. Marshal the
            // actual UAT orchestration back to the Jarvis UI dispatcher.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_uatRunning)
                    return;

                List<UatTestCase> tests;
                try
                {
                    tests = ParseCurrentUatSheet(workbookText);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[uat] deterministic workbook detection failed: " + ex);
                    return;
                }

                // Ordinary XLSX: no UAT sheet/header => normal attachment flow.
                if (tests == null || tests.Count == 0)
                    return;

                _uatRunning = true;
                try
                {
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
        }
    }
}
