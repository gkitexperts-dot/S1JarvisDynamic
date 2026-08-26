using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using S1Jarvis.Core;

namespace S1Jarvis.UI
{
    public partial class JarvisShell
    {
        private async Task RunAiUsageAggregationOnBootAsync()
        {
            if (!JarvisAiUsageAggregator.ShouldRunToday(_xSupport))
                return;

            Grid overlay = null;
            ProgressBar bar = null;
            TextBlock status = null;

            try
            {
                overlay = BuildUsageAggregationOverlay(out bar, out status);
                rootGrid.Children.Add(overlay);
                Panel.SetZIndex(overlay, 10000);

                SetUsageAggregationProgress(bar, status, 15, "Έλεγχος ημερήσιων AI usage...");
                await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                SetUsageAggregationProgress(bar, status, 45, "Συγκέντρωση προηγούμενων ημερών...");
                await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);

                // XSupport is a Soft1 host object. Keep the DB call on the UI/host
                // thread rather than moving it to Task.Run and risking COM/thread
                // affinity problems.
                bool ok = JarvisAiUsageAggregator.TryAggregatePreviousDays(_xSupport);

                SetUsageAggregationProgress(
                    bar,
                    status,
                    ok ? 100 : 85,
                    ok ? "Η ενημέρωση AI usage ολοκληρώθηκε." : "Η ενημέρωση AI usage θα επαναληφθεί στην επόμενη είσοδο.");

                await Task.Delay(ok ? 300 : 650);
            }
            catch (Exception ex)
            {
                try { DebugLog.Log("[AI-USAGE-AGG-UI] failed; startup continues: " + ex.Message); }
                catch { }
            }
            finally
            {
                if (overlay != null)
                    rootGrid.Children.Remove(overlay);
            }
        }

        private static Grid BuildUsageAggregationOverlay(
            out ProgressBar progressBar,
            out TextBlock statusText)
        {
            var overlay = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(225, 30, 30, 46))
            };

            var panel = new StackPanel
            {
                Width = 330,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var title = new TextBlock
            {
                Text = "Jarvis",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14)
            };

            statusText = new TextBlock
            {
                Text = "Προετοιμασία ημερήσιων AI usage...",
                Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 205)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };

            progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 5,
                Height = 5,
                IsIndeterminate = false
            };

            panel.Children.Add(title);
            panel.Children.Add(statusText);
            panel.Children.Add(progressBar);
            overlay.Children.Add(panel);
            return overlay;
        }

        private static void SetUsageAggregationProgress(
            ProgressBar bar,
            TextBlock status,
            double value,
            string text)
        {
            if (bar != null) bar.Value = value;
            if (status != null) status.Text = text;
        }
    }
}
