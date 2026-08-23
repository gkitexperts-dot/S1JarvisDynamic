using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using S1Jarvis.Access;
using S1Jarvis.Access.Verilic;

namespace S1Jarvis.UI
{
    public partial class VerilicMaintenanceShell : UserControl
    {
        private VerilicRuntimeConfiguration _configuration;
        private VerilicReadinessInspector _inspector;
        private VerilicActivationCoordinator _coordinator;

        public VerilicMaintenanceShell()
        {
            InitializeComponent();
            Loaded += VerilicMaintenanceShell_Loaded;
        }

        private void VerilicMaintenanceShell_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshState();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshState();
        }

        private void RefreshState()
        {
            try
            {
                _configuration = VerilicRuntimeConfiguration.Load();

                if (_configuration.Mode != VerilicRuntimeMode.Verilic)
                {
                    _inspector = null;
                    _coordinator = null;
                    modeStatusText.Text = "Verilic mode is not enabled for this Windows user.";
                    SetUnavailable();
                    return;
                }

                _inspector = new VerilicReadinessInspector(_configuration);
                _coordinator = new VerilicActivationCoordinator(_configuration);
                modeStatusText.Text = "Verilic mode is enabled. This maintenance screen never activates automatically and never displays keys, proofs or tokens.";

                ApplyReadiness(
                    _inspector.Inspect(JarvisProducts.Jarvis),
                    jarvisConfiguredText,
                    jarvisRefsText,
                    jarvisStateText,
                    jarvisReadyText,
                    activateJarvisButton);

                ApplyReadiness(
                    _inspector.Inspect(JarvisProducts.JarvisCourier),
                    courierConfiguredText,
                    courierRefsText,
                    courierStateText,
                    courierReadyText,
                    activateCourierButton);

                ApplyReadiness(
                    _inspector.Inspect(JarvisProducts.JarvisDocReader),
                    docReaderConfiguredText,
                    docReaderRefsText,
                    docReaderStateText,
                    docReaderReadyText,
                    activateDocReaderButton);

                activateAllButton.IsEnabled =
                    activateJarvisButton.IsEnabled ||
                    activateCourierButton.IsEnabled ||
                    activateDocReaderButton.IsEnabled;
            }
            catch
            {
                _configuration = null;
                _inspector = null;
                _coordinator = null;
                modeStatusText.Text = "Verilic maintenance configuration is invalid or unavailable.";
                resultText.Text = "No activation was attempted.";
                SetUnavailable();
            }
        }

        private static void ApplyReadiness(
            VerilicProductReadiness readiness,
            TextBlock configured,
            TextBlock activationRefs,
            TextBlock localState,
            TextBlock runtimeReady,
            Button activateButton)
        {
            if (readiness == null)
            {
                configured.Text = "Unavailable";
                activationRefs.Text = "Unavailable";
                localState.Text = "Unavailable";
                runtimeReady.Text = "No";
                activateButton.IsEnabled = false;
                return;
            }

            configured.Text = YesNo(readiness.ProductConfigured);
            activationRefs.Text = YesNo(readiness.ActivationReferencesConfigured);
            localState.Text = readiness.StatePresent
                ? readiness.ActivationCompleted ? "Activated" : "Pending"
                : "Not created";
            runtimeReady.Text = YesNo(readiness.RuntimeReady);
            activateButton.IsEnabled =
                readiness.ProductConfigured &&
                readiness.ActivationReferencesConfigured &&
                !readiness.RuntimeReady;
        }

        private void SetUnavailable()
        {
            SetUnavailableRow(jarvisConfiguredText, jarvisRefsText, jarvisStateText, jarvisReadyText, activateJarvisButton);
            SetUnavailableRow(courierConfiguredText, courierRefsText, courierStateText, courierReadyText, activateCourierButton);
            SetUnavailableRow(docReaderConfiguredText, docReaderRefsText, docReaderStateText, docReaderReadyText, activateDocReaderButton);
            activateAllButton.IsEnabled = false;
        }

        private static void SetUnavailableRow(
            TextBlock configured,
            TextBlock activationRefs,
            TextBlock localState,
            TextBlock runtimeReady,
            Button activateButton)
        {
            configured.Text = "Unavailable";
            activationRefs.Text = "Unavailable";
            localState.Text = "Unavailable";
            runtimeReady.Text = "No";
            activateButton.IsEnabled = false;
        }

        private void ActivateJarvis_Click(object sender, RoutedEventArgs e)
        {
            ActivateSingle(JarvisProducts.Jarvis, "S1 Jarvis");
        }

        private void ActivateCourier_Click(object sender, RoutedEventArgs e)
        {
            ActivateSingle(JarvisProducts.JarvisCourier, "Jarvis Courier");
        }

        private void ActivateDocReader_Click(object sender, RoutedEventArgs e)
        {
            ActivateSingle(JarvisProducts.JarvisDocReader, "Jarvis DocReader");
        }

        private void ActivateSingle(string productCode, string displayName)
        {
            if (_coordinator == null || _inspector == null)
            {
                resultText.Text = "Activation was not started because Verilic maintenance is not ready.";
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                "Activate " + displayName + " on this Windows user? This creates a local ES256 installation identity protected with DPAPI.",
                "Verilic licensing",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
                return;

            RunActivation(productCode, displayName);
        }

        private void ActivateAll_Click(object sender, RoutedEventArgs e)
        {
            if (_coordinator == null || _inspector == null)
            {
                resultText.Text = "Activation was not started because Verilic maintenance is not ready.";
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                "Activate all configured S1 Jarvis products on this Windows user? Products are processed in parent-first order and the operation stops on the first deny.",
                "Verilic licensing",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
                return;

            var products = new[]
            {
                new ProductActivation(JarvisProducts.Jarvis, "S1 Jarvis"),
                new ProductActivation(JarvisProducts.JarvisCourier, "Jarvis Courier"),
                new ProductActivation(JarvisProducts.JarvisDocReader, "Jarvis DocReader")
            };

            var results = new List<string>();

            foreach (ProductActivation product in products)
            {
                VerilicProductReadiness readiness = _inspector.Inspect(product.ProductCode);
                if (readiness.RuntimeReady)
                {
                    results.Add(product.DisplayName + ": already ready");
                    continue;
                }

                if (!readiness.ProductConfigured || !readiness.ActivationReferencesConfigured)
                {
                    results.Add(product.DisplayName + ": configuration missing; skipped");
                    continue;
                }

                VerilicActivationResult activation = _coordinator.Activate(product.ProductCode);
                if (activation == null || !activation.Success)
                {
                    results.Add(product.DisplayName + ": denied (" + SafeReason(activation == null ? null : activation.ReasonCode) + ")");
                    resultText.Text = string.Join(" | ", results);
                    RefreshState();
                    return;
                }

                VerilicProductReadiness after = _inspector.Inspect(product.ProductCode);
                if (!after.RuntimeReady)
                {
                    results.Add(product.DisplayName + ": activation incomplete locally");
                    resultText.Text = string.Join(" | ", results);
                    RefreshState();
                    return;
                }

                results.Add(product.DisplayName + ": ready");
            }

            resultText.Text = string.Join(" | ", results);
            RefreshState();
        }

        private void RunActivation(string productCode, string displayName)
        {
            try
            {
                VerilicActivationResult activation = _coordinator.Activate(productCode);
                if (activation == null || !activation.Success)
                {
                    resultText.Text = displayName + ": denied (" + SafeReason(activation == null ? null : activation.ReasonCode) + ")";
                    RefreshState();
                    return;
                }

                VerilicProductReadiness after = _inspector.Inspect(productCode);
                resultText.Text = after.RuntimeReady
                    ? displayName + ": activation completed and local runtime state is ready."
                    : displayName + ": activation returned success but local runtime state is incomplete.";
            }
            catch
            {
                resultText.Text = displayName + ": activation failed.";
            }
            finally
            {
                RefreshState();
            }
        }

        private static string YesNo(bool value)
        {
            return value ? "Yes" : "No";
        }

        private static string SafeReason(string reasonCode)
        {
            if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 100)
                return "activation_failed";

            string value = reasonCode.Trim();
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool allowed =
                    (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' ||
                    character == '-';
                if (!allowed)
                    return "activation_failed";
            }

            return value;
        }

        private sealed class ProductActivation
        {
            public ProductActivation(string productCode, string displayName)
            {
                ProductCode = productCode;
                DisplayName = displayName;
            }

            public string ProductCode { get; private set; }
            public string DisplayName { get; private set; }
        }
    }
}
