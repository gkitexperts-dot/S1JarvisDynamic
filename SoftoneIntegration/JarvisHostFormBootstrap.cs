using System;
using System.Drawing;
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
    }
}
