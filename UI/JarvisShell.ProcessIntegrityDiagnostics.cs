using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly bool ProcessIntegrityDiagnosticsRegistered = RegisterProcessIntegrityDiagnostics();

        private static bool RegisterProcessIntegrityDiagnostics()
        {
            EventManager.RegisterClassHandler(
                typeof(JarvisShell),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(JarvisShell_ProcessIntegrityLoaded));
            return true;
        }

        private static void JarvisShell_ProcessIntegrityLoaded(object sender, RoutedEventArgs e)
        {
            var shell = sender as JarvisShell;
            if (shell != null)
                shell.LogProcessIntegrityForDragDrop();
        }

        private void LogProcessIntegrityForDragDrop()
        {
            try
            {
                bool elevated = false;
                string identityName = string.Empty;

                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity != null)
                    {
                        identityName = identity.Name ?? string.Empty;
                        var principal = new WindowsPrincipal(identity);
                        elevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }

                string processName = string.Empty;
                try { processName = Process.GetCurrentProcess().ProcessName; }
                catch { }

                DebugLog.Log("[drag-drop] host process=" + processName +
                    " elevated=" + elevated +
                    " identity=" + identityName +
                    ". External Explorer drag/drop can be blocked by Windows when source and target integrity levels differ.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[drag-drop] process-integrity diagnostic EXCEPTION: " + ex);
            }
        }
    }
}
