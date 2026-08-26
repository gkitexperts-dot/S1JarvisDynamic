using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace S1Jarvis.UI
{
    // Thin Soft1-visible Dll Form entrypoint for the licensing screen.
    // The real WPF maintenance shell lives in the embedded runtime assembly.
    public class VerilicMaintenanceHostForm : Form
    {
        public VerilicMaintenanceHostForm()
        {
            Text = "Verilic Licensing";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            Width = 10;
            Height = 10;

            try
            {
                var shell = S1Jarvis.SoftoneIntegration.JarvisRuntimeLoader.CreateVerilicMaintenanceShell();
                var host = new ElementHost
                {
                    Dock = DockStyle.Fill,
                    Child = shell
                };
                Controls.Add(host);
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root is TargetInvocationException && root.InnerException != null)
                    root = root.InnerException;
                while (root.InnerException != null)
                    root = root.InnerException;

                MessageBox.Show(
                    "Verilic maintenance startup failed.\r\n\r\n" +
                    root.GetType().FullName + "\r\n" + root.Message +
                    (string.IsNullOrWhiteSpace(root.StackTrace) ? string.Empty : "\r\n\r\n" + root.StackTrace),
                    "S1Jarvis licensing error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
