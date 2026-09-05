using System;
using System.Windows;
using Softone;
using S1Jarvis.Core;

namespace S1Jarvis.Runtime
{
    public static class JarvisRuntimeBridge
    {
        public static FrameworkElement CreateShell(XSupport xSupport)
        {
            if (xSupport == null)
                throw new ArgumentNullException("xSupport");

            JarvisParameterAudit.Run(xSupport);

            S1Jarvis.UI.JarvisShell shell;
            try
            {
                shell = new S1Jarvis.UI.JarvisShell(xSupport);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[BOOT] JarvisShell construction failed: " + ex);
                return BuildSafeStartupError(
                    "Ο Jarvis δεν μπόρεσε να αρχικοποιηθεί. Το Soft1 παραμένει ενεργό.",
                    ex);
            }

            SafeEnable("NativeS1 AI provisioning", shell.EnableProviderHealthCheck);
            SafeEnable("AI usage UI", shell.EnableAiUsageUi);
            SafeEnable("AI usage aggregation", shell.EnableAiUsageAggregation);
            return shell;
        }

        private static void SafeEnable(string featureName, Action action)
        {
            try
            {
                if (action != null)
                    action();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[BOOT] optional feature disabled: " + featureName + " - " + ex);
            }
        }

        private static FrameworkElement BuildSafeStartupError(string message, Exception ex)
        {
            var panel = new System.Windows.Controls.Border
            {
                Padding = new Thickness(20),
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = message + Environment.NewLine + Environment.NewLine +
                           "Λεπτομέρειες: " + (ex == null ? "unknown" : ex.Message),
                    TextWrapping = TextWrapping.Wrap
                }
            };
            return panel;
        }
    }
}
