using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    /// <summary>
    /// Soft1 Dll Form host for operator-only Verilic maintenance.
    /// Uses the same proven reparenting pattern as JarvisHostForm, but hosts
    /// native WPF maintenance controls instead of the Jarvis WebView shell.
    /// </summary>
    public class VerilicMaintenanceHostForm : Form
    {
        public VerilicMaintenanceHostForm()
        {
            DebugLog.Log("VerilicMaintenanceHostForm ctor: creating VerilicMaintenanceShell");

            Text = "Verilic Licensing";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            Width = 10;
            Height = 10;

            var host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = new VerilicMaintenanceShell()
            };

            Controls.Add(host);
        }
    }
}
