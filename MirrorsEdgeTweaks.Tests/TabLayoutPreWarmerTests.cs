using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Windows;
using System.Windows.Controls;

namespace MirrorsEdgeTweaks.Tests
{
    [Collection("Wpf")]
    public class TabLayoutPreWarmerTests
    {
        [Fact]
        public void PreWarmBeforeShow_restores_original_tab_selection()
        {
            StaWpfTestRunner.Run(() =>
            {
                var tabControl = CreateThreeTabControl();
                tabControl.SelectedIndex = 0;

                var window = CreateHostWindow(tabControl);

                TabLayoutPreWarmer.PreWarmBeforeShow(window, tabControl);

                Assert.Equal(0, tabControl.SelectedIndex);
            });
        }

        [Fact]
        public void PreWarmBeforeShow_does_not_throw_for_single_tab()
        {
            StaWpfTestRunner.Run(() =>
            {
                var tabControl = new TabControl();
                tabControl.Items.Add(new TabItem { Header = "Only", Content = new TextBlock { Text = "Content" } });

                var window = CreateHostWindow(tabControl);

                TabLayoutPreWarmer.PreWarmBeforeShow(window, tabControl);
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
