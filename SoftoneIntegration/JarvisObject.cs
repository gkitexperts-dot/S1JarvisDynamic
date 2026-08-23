using System;
using System.Collections.Generic;
using System.Windows;
using Softone;
using S1Jarvis.Access;
using S1Jarvis.Access.Verilic;
using S1Jarvis.Core;

namespace S1Jarvis.SoftoneIntegration
{
    // Hook πάνω στο custom Object "CCCJARVIS" - φτιάχνεται στον SoftOne
    // Designer, ΟΧΙ εδώ (βλ. README.md, βήματα 1-3). Ίδιο μοτίβο ακριβώς με
    // το [WorksOn("SALDOC")] του S1Courier, απλά στοχεύει στο δικό μας νέο
    // Object αντί να "γαντζώνεται" πάνω σε υπάρχουσα φόρμα παραστατικού.
    [WorksOn("CCCJARVIS")]
    public class JarvisObject : TXCode
    {
        private static readonly string[] VerilicActivationProducts =
        {
            JarvisProducts.Jarvis,
            JarvisProducts.JarvisCourier,
            JarvisProducts.JarvisDocReader
        };

        // Static, ίδια σύμβαση με S1Courier's _courierPage/_manifestPage -
        // κρατάει reference στο ζωντανό control όσο είναι ανοιχτό το object.
        private static FrameworkElement _shell;

        public override void OnFormLoad(string formname)
        {
            base.OnFormLoad(formname);

            try
            {
                DebugLog.Log("JarvisObject.OnFormLoad: δημιουργία JarvisShell (WorksOn CCCJARVIS, InsertWPFContent)");

                _shell = new S1Jarvis.UI.JarvisShell(XSupport);

                // "JarvisPanel": το όνομα του Panel που πρέπει να υπάρχει μέσα
                // στο Form του Object CCCJARVIS (Designer, βήμα 2 στο README).
                XModule.InsertWPFContent(_shell, "*PANEL(JarvisPanel)");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Αδυναμία φόρτωσης Jarvis: " + ex.Message,
                    "S1Jarvis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // Native Soft1-side operator command. Intended to be bound to a normal
        // Soft1 form/admin action, outside the Jarvis WPF/WebView UI. It is
        // deliberately never called from OnFormLoad/startup.
        public string ActivateVerilicAll()
        {
            MessageBoxResult confirmation = MessageBox.Show(
                "Θα δημιουργηθούν τα τοπικά Verilic installation identities και θα ενεργοποιηθούν οι ρυθμισμένες test/demo άδειες για αυτόν τον Windows user. Συνέχεια;",
                "Verilic licensing activation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
                return "Verilic activation cancelled.";

            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();

                if (configuration.Mode != VerilicRuntimeMode.Verilic)
                    return "Verilic activation: runtime_mode_legacy";

                var inspector = new VerilicReadinessInspector(configuration);
                var coordinator = new VerilicActivationCoordinator(configuration);
                var messages = new List<string>();

                foreach (string productCode in VerilicActivationProducts)
                {
                    VerilicProductReadiness before = inspector.Inspect(productCode);

                    if (before.RuntimeReady)
                    {
                        messages.Add(productCode + ": already_activated");
                        continue;
                    }

                    if (!before.ActivationReferencesConfigured)
                    {
                        messages.Add(productCode + ": activation_references_missing");
                        return ShowActivationSummary(messages, true);
                    }

                    VerilicActivationResult result = coordinator.Activate(productCode);
                    if (result == null || !result.Success)
                    {
                        messages.Add(productCode + ": denied (" +
                            SafeReason(result == null ? null : result.ReasonCode) + ")");
                        return ShowActivationSummary(messages, true);
                    }

                    VerilicProductReadiness after = inspector.Inspect(productCode);
                    if (!after.RuntimeReady)
                    {
                        messages.Add(productCode + ": local_readiness_incomplete");
                        return ShowActivationSummary(messages, true);
                    }

                    messages.Add(productCode +
                        (result.WasAlreadyCompleted
                            ? ": already_completed"
                            : ": activated"));
                }

                return ShowActivationSummary(messages, false);
            }
            catch
            {
                string message =
                    "Verilic activation failed: activation_configuration_invalid";
                MessageBox.Show(
                    message,
                    "Verilic licensing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return message;
            }
        }

        // Individual explicit entrypoints remain available for maintenance and
        // controlled troubleshooting. They are not invoked automatically.
        public string ActivateVerilicJarvis()
        {
            return ActivateVerilicProduct(JarvisProducts.Jarvis);
        }

        public string ActivateVerilicCourier()
        {
            return ActivateVerilicProduct(JarvisProducts.JarvisCourier);
        }

        public string ActivateVerilicDocReader()
        {
            return ActivateVerilicProduct(JarvisProducts.JarvisDocReader);
        }

        // Read-only local readiness summary. It never activates, verifies over
        // the network or returns configuration identifiers/private material.
        public string GetVerilicReadiness()
        {
            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();

                if (configuration.Mode != VerilicRuntimeMode.Verilic)
                    return "Verilic readiness: runtime_mode_legacy";

                var inspector = new VerilicReadinessInspector(configuration);
                return string.Join(
                    Environment.NewLine,
                    FormatReadiness("Jarvis", inspector.Inspect(JarvisProducts.Jarvis)),
                    FormatReadiness("Courier", inspector.Inspect(JarvisProducts.JarvisCourier)),
                    FormatReadiness("DocReader", inspector.Inspect(JarvisProducts.JarvisDocReader)));
            }
            catch
            {
                return "Verilic readiness: configuration_invalid";
            }
        }

        private static string ActivateVerilicProduct(string productCode)
        {
            try
            {
                VerilicRuntimeConfiguration configuration =
                    VerilicRuntimeConfiguration.Load();
                var coordinator = new VerilicActivationCoordinator(configuration);
                VerilicActivationResult result = coordinator.Activate(productCode);

                if (result != null && result.Success)
                    return result.WasAlreadyCompleted
                        ? "Η ενεργοποίηση Verilic είναι ήδη ολοκληρωμένη."
                        : "Η ενεργοποίηση Verilic ολοκληρώθηκε επιτυχώς.";

                return "Η ενεργοποίηση Verilic δεν ολοκληρώθηκε. Κωδικός: " +
                       SafeReason(result == null ? null : result.ReasonCode);
            }
            catch
            {
                return "Η ενεργοποίηση Verilic δεν ολοκληρώθηκε. Κωδικός: activation_failed";
            }
        }

        private static string ShowActivationSummary(
            IEnumerable<string> messages,
            bool isError)
        {
            string text = "Verilic activation result:" +
                          Environment.NewLine +
                          "- " +
                          string.Join(Environment.NewLine + "- ", messages);

            MessageBox.Show(
                text,
                "Verilic licensing",
                MessageBoxButton.OK,
                isError ? MessageBoxImage.Error : MessageBoxImage.Information);

            return text;
        }

        private static string FormatReadiness(
            string label,
            VerilicProductReadiness readiness)
        {
            if (readiness == null)
                return label + ": readiness_failed";

            return label + ": " + SafeReason(readiness.ReasonCode) +
                   " | product=" + Flag(readiness.ProductConfigured) +
                   " | activationRefs=" + Flag(readiness.ActivationReferencesConfigured) +
                   " | state=" + Flag(readiness.StatePresent) +
                   " | activated=" + Flag(readiness.ActivationCompleted) +
                   " | binding=" + Flag(readiness.ProductBindingMatches) +
                   " | runtimeReady=" + Flag(readiness.RuntimeReady);
        }

        private static string Flag(bool value)
        {
            return value ? "yes" : "no";
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
    }
}
