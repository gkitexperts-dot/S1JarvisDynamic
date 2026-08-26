using System;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using S1Jarvis.SoftoneIntegration;

namespace S1Jarvis.UI
{
    // Thin Soft1-visible Dll Form entrypoint for licensing.
    // Keep the constructor side-effect free so Soft1 can discover/instantiate the
    // form during startup without forcing the embedded runtime to load. The actual
    // WPF licensing shell is created only when the operator opens the form.
    public class VerilicMaintenanceHostForm : Form
    {
        private bool _initialized;

        public VerilicMaintenanceHostForm()
        {
            Text = "Verilic Licensing";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            Width = 10;
            Height = 10;
            Shown += VerilicMaintenanceHostForm_Shown;
        }

        private void VerilicMaintenanceHostForm_Shown(object sender, EventArgs e)
        {
            if (_initialized)
                return;

            _initialized = true;

            try
            {
                var shell = JarvisRuntimeLoader.CreateVerilicMaintenanceShell();
                var host = new ElementHost
                {
                    Dock = DockStyle.Fill,
                    Child = shell
                };

                Controls.Add(host);
                host.BringToFront();
            }
            catch (Exception ex)
            {
                Exception root = Unwrap(ex);
                MessageBox.Show(
                    BuildDiagnosticMessage(ex, root),
                    "S1Jarvis licensing error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static Exception Unwrap(Exception ex)
        {
            Exception current = ex;
            while (current != null && current.InnerException != null &&
                   (current is TargetInvocationException || current is TypeInitializationException))
            {
                current = current.InnerException;
            }
            return current ?? ex;
        }

        private static string BuildDiagnosticMessage(Exception original, Exception root)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Verilic licensing failed while opening.");
            sb.AppendLine();
            sb.AppendLine("Root exception:");
            sb.AppendLine(root.GetType().FullName);
            sb.AppendLine(root.Message);

            if (!ReferenceEquals(original, root))
            {
                sb.AppendLine();
                sb.AppendLine("Wrapper exception:");
                sb.AppendLine(original.GetType().FullName);
                sb.AppendLine(original.Message);
            }

            if (!string.IsNullOrWhiteSpace(root.StackTrace))
            {
                sb.AppendLine();
                sb.AppendLine("Stack trace:");
                sb.AppendLine(root.StackTrace);
            }

            return sb.ToString();
        }
    }
}
