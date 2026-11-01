using MaterialDesignThemes.Wpf;
using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MirrorsEdgeTweaks
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow(MainViewModel viewModel)
        {
            _viewModel = viewModel;

            InitializeComponent();

            RenderResolutionSlider.AddHandler(
                System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
                new System.Windows.Controls.Primitives.DragStartedEventHandler(RenderResolutionSlider_DragStarted));
            RenderResolutionSlider.AddHandler(
                System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
                new System.Windows.Controls.Primitives.DragCompletedEventHandler(RenderResolutionSlider_DragCompleted));

            DataContext = viewModel;

            EventManager.RegisterClassHandler(typeof(ComboBoxItem), System.Windows.Input.Mouse.PreviewMouseUpEvent, new System.Windows.Input.MouseButtonEventHandler(OnComboBoxItemMouseUp), true);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) => _ = RunStartupAsync();

        private async Task RunStartupAsync()
        {
            await Task.WhenAll(
                _viewModel.InitializeAsync(),
                PreWarmTabLayoutsAfterShowAsync());
        }

        private async Task PreWarmTabLayoutsAfterShowAsync()
        {
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);

            await Dispatcher.InvokeAsync(() =>
            {
                bool previousDisableTransitions = TransitionAssist.GetDisableTransitions(MainTabControl);
                bool previousDisableRipple = RippleAssist.GetIsDisabled(MainTabControl);
                double previousOpacity = MainTabControl.Opacity;

                TransitionAssist.SetDisableTransitions(MainTabControl, true);
                RippleAssist.SetIsDisabled(MainTabControl, true);
                MainTabControl.Opacity = 0;

                try
                {
                    TabLayoutPreWarmer.PreWarm(MainTabControl, this);
                }
                finally
                {
                    MainTabControl.UpdateLayout();
                    MainTabControl.Opacity = previousOpacity;

                    _ = Dispatcher.InvokeAsync(
                        () =>
                        {
                            TransitionAssist.SetDisableTransitions(MainTabControl, previousDisableTransitions);
                            RippleAssist.SetIsDisabled(MainTabControl, previousDisableRipple);
                        },
                        DispatcherPriority.Render);
                }
            }, DispatcherPriority.Background);
        }

        private void RenderResolutionSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
            => _viewModel.Graphics.BeginRenderResolutionDrag();

        private void RenderResolutionSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
            => _viewModel.Graphics.EndRenderResolutionDrag();

        // Clicking the selected combo box item again re-applies its setting.
        // The bound SelectedIndex and SelectedItem only notify the view model when the value changes,
        // so the selection is cleared and then restored.
        // Every combo box's apply path ignores the brief -1.
        private void OnComboBoxItemMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is ComboBoxItem item)
            {
                var comboBox = ItemsControl.ItemsControlFromItemContainer(item) as System.Windows.Controls.ComboBox;
                if (comboBox != null && comboBox.ItemContainerGenerator.ItemFromContainer(item) == comboBox.SelectedItem)
                {
                    int selectedIndex = comboBox.SelectedIndex;
                    if (selectedIndex >= 0)
                    {
                        comboBox.SelectedIndex = -1;
                        comboBox.SelectedIndex = selectedIndex;
                    }
                }
            }
        }
    }
}
