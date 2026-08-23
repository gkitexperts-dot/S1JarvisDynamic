using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using S1Jarvis.Access;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private static readonly string[] VerilicActivationProducts =
        {
            JarvisProducts.Jarvis,
            JarvisProducts.JarvisCourier,
            JarvisProducts.JarvisDocReader
        };

        private void VerilicActivationControl_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshVerilicActivationControl();
        }

        private void RefreshVerilicActivationControl()
        {
            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();

                if (configuration.Mode != VerilicRuntimeMode.Verilic)
                {
                    verilicActivationBtn.Visibility = Visibility.Collapsed;
                    return;
                }

                var inspector = new VerilicReadinessInspector(configuration);
                bool hasPendingConfiguredActivation = false;

                foreach (string productCode in VerilicActivationProducts)
                {
                    VerilicProductReadiness readiness = inspector.Inspect(productCode);
                    if (readiness.ActivationReferencesConfigured &&
                        !readiness.RuntimeReady)
                    {
                        hasPendingConfiguredActivation = true;
                        break;
                    }
                }

                verilicActivationBtn.Visibility = hasPendingConfiguredActivation
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            catch
            {
                verilicActivationBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void VerilicActivationBtn_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirmation = MessageBox.Show(
                "This will create local ES256 installation identities and activate the configured Verilic licences for this Windows user. Continue?",
                "Activate Verilic licensing",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
                return;

            verilicActivationBtn.IsEnabled = false;

            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();
                if (configuration.Mode != VerilicRuntimeMode.Verilic)
                {
                    ShowVerilicActivationResult(
                        "Activation was not started because Verilic mode is not enabled.",
                        isError: true);
                    return;
                }

                var inspector = new VerilicReadinessInspector(configuration);
                var coordinator = new VerilicActivationCoordinator(configuration);
                var messages = new List<string>();

                foreach (string productCode in VerilicActivationProducts)
                {
                    VerilicProductReadiness before = inspector.Inspect(productCode);

                    if (before.RuntimeReady)
                    {
                        messages.Add(productCode + ": already activated locally");
                        continue;
                    }

                    if (!before.ActivationReferencesConfigured)
                    {
                        messages.Add(productCode + ": activation references not configured; skipped");
                        continue;
                    }

                    VerilicActivationResult result = coordinator.Activate(productCode);
                    if (!result.Success)
                    {
                        messages.Add(productCode + ": denied (" + result.ReasonCode + ")");
                        ShowVerilicActivationResult(
                            BuildActivationSummary(messages),
                            isError: true);
                        return;
                    }

                    VerilicProductReadiness after = inspector.Inspect(productCode);
                    if (!after.RuntimeReady)
                    {
                        messages.Add(productCode + ": activation returned success but local readiness is incomplete");
                        ShowVerilicActivationResult(
                            BuildActivationSummary(messages),
                            isError: true);
                        return;
                    }

                    messages.Add(productCode +
                        (result.WasAlreadyCompleted
                            ? ": activation already completed"
                            : ": activated"));
                }

                ShowVerilicActivationResult(
                    BuildActivationSummary(messages),
                    isError: false);
            }
            catch
            {
                ShowVerilicActivationResult(
                    "Verilic activation failed because the local configuration or activation state is invalid.",
                    isError: true);
            }
            finally
            {
                verilicActivationBtn.IsEnabled = true;
                RefreshVerilicActivationControl();
            }
        }

        private static string BuildActivationSummary(IEnumerable<string> messages)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Verilic activation result:");
            foreach (string message in messages)
                builder.AppendLine("- " + message);
            return builder.ToString().TrimEnd();
        }

        private static void ShowVerilicActivationResult(string message, bool isError)
        {
            MessageBox.Show(
                message,
                "Verilic licensing",
                MessageBoxButton.OK,
                isError ? MessageBoxImage.Error : MessageBoxImage.Information);
        }
    }
}
