using MirrorsEdgeTweaks.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace MirrorsEdgeTweaks
{
    // Thin View shell: resolves the MainViewModel DataContext via DI and hosts the few interactions
    // that genuinely belong to the View - the render-resolution slider Thumb drag events and a class
    // handler that re-applies a setting when its already-selected combo item is re-clicked. All
    // orchestration lives in the view model layer.
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

        private void Window_Loaded(object sender, RoutedEventArgs e) => _ = _viewModel.InitializeAsync();

        // Forward the render-resolution slider's Thumb drag events to the GraphicsTweaksViewModel,
        // which owns the drag-deferred apply logic.
        private void RenderResolutionSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
            => _viewModel.Graphics.BeginRenderResolutionDrag();

        private void RenderResolutionSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
            => _viewModel.Graphics.EndRenderResolutionDrag();

        // Re-clicking the already-selected combo item should re-apply its setting. The combos are
        // bound via SelectedIndex/SelectedItem, which only raise their VM OnChanged handler on an
        // actual value change, so we briefly clear the selection and restore it: the transient -1 is
        // a guarded no-op in every combo's apply path, and restoring the original index makes the
        // bound property genuinely change and re-apply.
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
