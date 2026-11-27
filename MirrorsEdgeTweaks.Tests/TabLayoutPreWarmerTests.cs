using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Windows;
using System.Windows.Controls;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("Wpf")]
    public class TabLayoutPreWarmerTests
    {
        [Fact]
        public void PreWarm_restores_original_tab_selection()
        {
            StaWpfTestRunner.Run(() =>
            {
                var tabControl = CreateThreeTabControl();
                tabControl.SelectedIndex = 1;

                var window = CreateHostWindow(tabControl);

                TabLayoutPreWarmer.PreWarm(tabControl, window);

                Assert.Equal(1, tabControl.SelectedIndex);
            });
        }

        [Fact]
        public void PreWarm_does_not_throw_for_single_tab()
        {
            StaWpfTestRunner.Run(() =>
            {
                var tabControl = new TabControl();
                tabControl.Items.Add(new TabItem { Header = "Only", Content = new TextBlock { Text = "Content" } });

                var window = CreateHostWindow(tabControl);

                TabLayoutPreWarmer.PreWarm(tabControl, window);
            });
        }

        private static TabControl CreateThreeTabControl()
        {
            var tabControl = new TabControl();
            for (int i = 0; i < 3; i++)
            {
                tabControl.Items.Add(new TabItem
                {
                    Header = $"Tab {i}",
                    Content = new StackPanel
                    {
                        Children = { new TextBlock { Text = $"Content {i}" } }
                    }
                });
            }

            return tabControl;
        }

        private static Window CreateHostWindow(TabControl tabControl) =>
            new()
            {
                Width = 650,
                Height = 400,
                Content = tabControl
            };
    }
}
