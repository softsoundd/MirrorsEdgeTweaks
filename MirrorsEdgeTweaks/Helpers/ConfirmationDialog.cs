using MaterialDesignThemes.Wpf;
using System.Windows;

namespace MirrorsEdgeTweaks.Helpers
{
    public class ConfirmationDialog : System.Windows.Controls.UserControl
    {
        public ConfirmationDialog(string title, string message)
        {
            var border = new System.Windows.Controls.Border
            {
                BorderBrush = System.Windows.Media.Brushes.LightGray,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8),
                Background = System.Windows.Media.Brushes.White,
                Padding = new System.Windows.Thickness(20),
                MaxWidth = 500,
                MinWidth = 300
            };

            var stackPanel = new System.Windows.Controls.StackPanel();

            var titleText = new System.Windows.Controls.TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new System.Windows.Thickness(0, 0, 0, 16)
            };

            var messageText = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 16),
                MaxWidth = 450
            };

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var yesButton = new System.Windows.Controls.Button
            {
                Content = "Yes",
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Style = (Style)System.Windows.Application.Current.FindResource("MaterialDesignRaisedButton")
            };
            yesButton.Click += (s, e) => DialogHost.CloseDialogCommand.Execute(true, yesButton);

            var noButton = new System.Windows.Controls.Button
            {
                Content = "No",
                Style = (Style)System.Windows.Application.Current.FindResource("MaterialDesignRaisedButton")
            };
            noButton.Click += (s, e) => DialogHost.CloseDialogCommand.Execute(false, noButton);

            buttonPanel.Children.Add(yesButton);
            buttonPanel.Children.Add(noButton);

            stackPanel.Children.Add(titleText);
            stackPanel.Children.Add(messageText);
            stackPanel.Children.Add(buttonPanel);

            border.Child = stackPanel;
            Content = border;
        }
    }
}
