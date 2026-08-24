using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using S1Jarvis.Core;
using S1Jarvis.SoftoneIntegration;

namespace S1Jarvis.UI
{
    // "Dll Form" job type του Soft1 (Menu Job, Action/File =
    // ".S1Jarvis.dll;JarvisHostForm") - το Soft1 φτιάχνει αυτό το Form με
    // reflection (public, χωρίς παραμέτρους constructor) και ΤΟ ΙΔΙΟ κάνει
    // raw reparent μέσα στο δικό του tab panel, ώστε να δείχνει σαν κανονική
    // καρτέλα "Jarvis". ΕΠΙΒΕΒΑΙΩΜΕΝΟ 20/08 (Win32 window enumeration,
    // ζωντανά πάνω στο τρέχον Soft1 process): ΕΝΑ και μοναδικό, καθαρό chain
    // χωρίς διπλό/orphan αντίγραφο - THostPanel (Soft1) -> JarvisHostForm ->
    // ElementHost -> JarvisShell (WPF, το ΠΡΑΓΜΑΤΙΚΟ Jarvis) ->
    // Chrome_WidgetWin_0 (WebView2). Βλ. README "Ιστορικό / γιατί υπάρχουν
    // αχρησιμοποίητα αρχεία" - αυτό είναι το ΜΟΝΟ μονοπάτι από τα 3 που
    // δοκιμάστηκαν που δουλεύει (Plan A crashed τον Designer, Plan C.1
    // άνοιγε κενό).
    //
    // ΡΗΤΟ ΑΙΤΗΜΑ ΧΡΗΣΤΗ 20/08: μικρό αρχικό Size + πάνω-αριστερά Location
    // ΑΝΤΙ για 700x500/CenterScreen - το Soft1 ούτως ή άλλως το
    // reparent-άρει/κάνει dock στο δικό του tab panel μόλις ενεργοποιηθεί,
    // οπότε το αρχικό Size/Location εδώ είναι μόνο η ΠΡΟΣΩΡΙΝΗ κατάσταση πριν
    // προλάβει αυτό να συμβεί. ΔΕΝ αλλάζουμε Background/χρώμα. ΔΕΝ κάνουμε
    // Hide()/Minimized - αυτό έσπασε εντελώς τον Jarvis σε προηγούμενη
    // δοκιμή (βλ. commit bc5e3e3, revert) - παραμένει Normal/Visible, μόνο
    // μικρότερο και σε άλλη αρχική θέση.
    public class JarvisHostForm : Form
    {
        public JarvisHostForm()
        {
            DebugLog.Log("JarvisHostForm ctor: δημιουργία JarvisShell (ElementHost, top-level Form -> reparented από Soft1)");

            Text = "Jarvis";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            Width = 10;
            Height = 10;

            var shell = new JarvisShell(JarvisCore.XSupport);
            shell.EnableProviderHealthCheck();

            var host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = shell
            };

            Controls.Add(host);
        }
    }
}
