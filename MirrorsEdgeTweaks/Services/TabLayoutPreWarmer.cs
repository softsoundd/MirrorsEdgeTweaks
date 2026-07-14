using System.Windows;
using System.Windows.Media;

namespace MirrorsEdgeTweaks.Services
{
    // WPF TabControl only materialises the selected tab's content in the visual tree.
    internal static class TabLayoutPreWarmer
    {
        public static void PreWarm(System.Windows.Controls.TabControl tabControl, Window? hostWindow = null)
        {
            if (tabControl.Items.Count <= 1)
                return;

            hostWindow?.UpdateLayout();
            tabControl.UpdateLayout();
            PreWarmCore(tabControl);
        }

        public static void PreWarmBeforeShow(Window window, System.Windows.Controls.TabControl tabControl)
        {
            if (tabControl.Items.Count <= 1)
                return;

            window.ApplyTemplate();
            window.Measure(new System.Windows.Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            window.UpdateLayout();

            PreWarmCore(tabControl);
        }

        private static void PreWarmCore(System.Windows.Controls.TabControl tabControl)
        {
            int originalIndex = tabControl.SelectedIndex;

            try
            {
                for (int i = 0; i < tabControl.Items.Count; i++)
                {
                    tabControl.SelectedIndex = i;
                    tabControl.UpdateLayout();

                    if (tabControl.SelectedContent is DependencyObject selectedContent)
                        WarmVisualTree(selectedContent);
                }
            }
            finally
            {
                if (originalIndex >= 0 && originalIndex < tabControl.Items.Count)
                    tabControl.SelectedIndex = originalIndex;

                tabControl.UpdateLayout();
            }
        }

        private static void WarmVisualTree(DependencyObject root)
        {
            if (root is FrameworkElement element)
                element.UpdateLayout();

            for (int i = 0, count = VisualTreeHelper.GetChildrenCount(root); i < count; i++)
                WarmVisualTree(VisualTreeHelper.GetChild(root, i));
        }
    }
}
