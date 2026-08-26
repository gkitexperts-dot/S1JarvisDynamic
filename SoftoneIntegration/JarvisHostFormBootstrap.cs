using System;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using S1Jarvis.SoftoneIntegration;

namespace S1Jarvis.UI
{
    // Thin Soft1-visible Dll Form entrypoint. It deliberately has no compile-time
    // reference to JarvisShell or WebView2; the real WPF shell is created through
    // JarvisRuntimeLoader only after Soft1 has finished reflecting over S1Jarvis.dll.
    public class JarvisHostForm : Form
    {
        public JarvisHostForm()
        {
            Text = "Jarvis";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            Width = 10;
            Height = 10;

            try
            {
                if (JarvisCore.XSupport == null)
                    throw new InvalidOperationException("Jarvis XSupport is not initialized.");

                var shell = JarvisRuntimeLoader.CreateShell(JarvisCore.XSupport);
                var host = new ElementHost
                {
                    Dock = DockStyle.Fill,
                    Child = shell
                };

                Controls.Add(host);
            }
            catch (Exception ex)
            {
                Exception root = Unwrap(ex);
                MessageBox.Show(
                    BuildDiagnosticMessage(ex, root),
                    "S1Jarvis startup error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                // Do not rethrow here. Soft1 constructs Dll Forms through reflection;
                // rethrowing would hide the useful inner exception behind the generic
                // TargetInvocationException message "Exception has been thrown by the
                // target of an invocation".
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
            sb.AppendLine("Jarvis failed while opening.");
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
