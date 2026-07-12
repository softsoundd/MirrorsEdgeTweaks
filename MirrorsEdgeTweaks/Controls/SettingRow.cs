using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MirrorsEdgeTweaks.Controls
{
    // A single settings row: bold label in a fixed 190px column, arbitrary content, and an
    // optional trailing info button. Replaces the dozens of hand-written three-column grids in
    // MainWindow.xaml. Template lives in Themes/Generic.xaml.
    public class SettingRow : ContentControl
    {
        static SettingRow()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow),
                new FrameworkPropertyMetadata(typeof(SettingRow)));
        }

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty InfoCommandProperty = DependencyProperty.Register(
            nameof(InfoCommand), typeof(ICommand), typeof(SettingRow), new PropertyMetadata(null));

        public ICommand? InfoCommand
        {
            get => (ICommand?)GetValue(InfoCommandProperty);
            set => SetValue(InfoCommandProperty, value);
        }

        public static readonly DependencyProperty InfoParameterProperty = DependencyProperty.Register(
            nameof(InfoParameter), typeof(object), typeof(SettingRow), new PropertyMetadata(null));

        public object? InfoParameter
        {
            get => GetValue(InfoParameterProperty);
            set => SetValue(InfoParameterProperty, value);
        }
    }
}
