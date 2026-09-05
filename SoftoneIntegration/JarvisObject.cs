using System;
using System.Windows;
using Softone;

namespace S1Jarvis.SoftoneIntegration
{
    [WorksOn("CCCJARVIS")]
    public class JarvisObject : TXCode
    {
        private static FrameworkElement _shell;

        public override void OnFormLoad(string formname)
        {
            base.OnFormLoad(formname);

            try
            {
                _shell = JarvisRuntimeLoader.CreateShell(XSupport);
                XModule.InsertWPFContent(_shell, "*PANEL(JarvisPanel)");
            }
            catch (Exception ex)
            {
                Exception root = ex;
                while (root.InnerException != null)
                    root = root.InnerException;

                MessageBox.Show(
                    "Αδυναμία φόρτωσης Jarvis: " + root.Message,
                    "S1Jarvis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
