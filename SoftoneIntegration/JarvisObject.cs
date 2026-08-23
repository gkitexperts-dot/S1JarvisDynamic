using System;
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

        // Explicit operator entrypoints for Verilic activation. These methods
        // are deliberately never called from OnFormLoad/startup. Activation
        // establishes installation identity only; normal runtime verification
        // remains authoritative for Allowed/Denied.
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
                // Never expose configuration, transport or cryptographic
                // exception details through the Soft1 operator surface.
                return "Η ενεργοποίηση Verilic δεν ολοκληρώθηκε. Κωδικός: activation_failed";
            }
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
