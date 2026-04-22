using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace MirrorsEdgeTweaks.Helpers
{
    public class KeybindCaptureDialog : System.Windows.Controls.UserControl
    {
        private readonly Dictionary<string, string> _ue3KeyMap;

        public KeybindCaptureDialog(Dictionary<string, string> ue3KeyMap)
        {
            _ue3KeyMap = ue3KeyMap;
            Focusable = true;

            var rootBorder = new System.Windows.Controls.Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8),
                Background = System.Windows.Media.Brushes.White,
                Padding = new System.Windows.Thickness(20),
                MaxWidth = 500,
                MinWidth = 300
            };

            var stack = new System.Windows.Controls.StackPanel();

            var title = new System.Windows.Controls.TextBlock
            {
                Text = "Set Keybind",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new System.Windows.Thickness(0, 0, 0, 16)
            };

            var instruction = new System.Windows.Controls.TextBlock
            {
                Text = "Press any key, mouse button, or scrollwheel.\n\nPress Escape to cancel.\n\nPress Backspace or Delete to clear.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 16),
                MaxWidth = 450
            };

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var clearButton = new System.Windows.Controls.Button
            {
                Content = "Clear",
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Style = (Style)System.Windows.Application.Current.FindResource("MaterialDesignRaisedButton")
            };
            clearButton.Click += (s, e) => DialogHost.CloseDialogCommand.Execute("", clearButton);

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Style = (Style)System.Windows.Application.Current.FindResource("MaterialDesignOutlinedButton")
            };
            cancelButton.Click += (s, e) => DialogHost.CloseDialogCommand.Execute(null, cancelButton);

            buttonPanel.Children.Add(clearButton);
            buttonPanel.Children.Add(cancelButton);

            stack.Children.Add(title);
            stack.Children.Add(instruction);
            stack.Children.Add(buttonPanel);

            rootBorder.Child = stack;
            Content = rootBorder;

            Loaded += (s, e) =>
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new System.Action(() =>
                {
                    Focus();
                    System.Windows.Input.Keyboard.Focus(this);
                    System.Windows.Input.Mouse.Capture(this, System.Windows.Input.CaptureMode.SubTree);
                }));
            };

            PreviewKeyDown += OnPreviewKeyDown;
            PreviewMouseDown += OnPreviewMouseDown;
            PreviewMouseWheel += OnPreviewMouseWheel;
        }

        private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            e.Handled = true;

            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

            if (key == System.Windows.Input.Key.Escape)
            {
                DialogHost.CloseDialogCommand.Execute(null, this);
                return;
            }

            if (key == System.Windows.Input.Key.Back || key == System.Windows.Input.Key.Delete)
            {
                DialogHost.CloseDialogCommand.Execute("", this);
                return;
            }

            if (key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl ||
                key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt ||
                key == System.Windows.Input.Key.LeftShift || key == System.Windows.Input.Key.RightShift ||
                key == System.Windows.Input.Key.LWin || key == System.Windows.Input.Key.RWin)
            {
                return;
            }

            string keyString = key.ToString();
            string ue3Key;

            if (_ue3KeyMap.TryGetValue(keyString, out ue3Key!))
            {
            }
            else if (keyString.Length == 1 && char.IsLetter(keyString[0]))
            {
                ue3Key = keyString.ToUpper();
            }
            else
            {
                return;
            }

            DialogHost.CloseDialogCommand.Execute(ue3Key, this);
        }

        private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (IsWithinButton(e.OriginalSource as DependencyObject))
                return;

            e.Handled = true;

            string ue3Key;
            switch (e.ChangedButton)
            {
                case System.Windows.Input.MouseButton.Left:
                    ue3Key = "LeftMouseButton";
                    break;
                case System.Windows.Input.MouseButton.Right:
                    ue3Key = "RightMouseButton";
                    break;
                case System.Windows.Input.MouseButton.Middle:
                    ue3Key = "MiddleMouseButton";
                    break;
                case System.Windows.Input.MouseButton.XButton1:
                    ue3Key = "ThumbMouseButton";
                    break;
                case System.Windows.Input.MouseButton.XButton2:
                    ue3Key = "ThumbMouseButton2";
                    break;
                default:
                    return;
            }

            DialogHost.CloseDialogCommand.Execute(ue3Key, this);
        }

        private void OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            e.Handled = true;
            string ue3Key = e.Delta > 0 ? "MouseScrollUp" : "MouseScrollDown";
            DialogHost.CloseDialogCommand.Execute(ue3Key, this);
        }

        private static bool IsWithinButton(DependencyObject? source)
        {
            var current = source;
            while (current != null)
            {
                if (current is System.Windows.Controls.Button)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }
    }
}
