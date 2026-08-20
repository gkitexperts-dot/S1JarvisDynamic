using System;
using System.Windows;
using Softone;
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
                // ΔΙΑΓΝΩΣΤΙΚΟ 20/08 - έρευνα black-box artifact. Αφαιρέθηκε
                // όταν επιβεβαιωθεί η αιτία (βλ. ίδιο log και στο
                // JarvisHostForm.cs - στόχος είναι να δούμε αν ΠΟΤΕ
                // πυροδοτείται και το ένα ΚΑΙ το άλλο μαζί).
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
    }
}
