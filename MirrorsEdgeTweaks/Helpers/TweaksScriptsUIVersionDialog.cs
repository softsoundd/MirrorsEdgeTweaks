namespace MirrorsEdgeTweaks.Helpers
{
    // Dialog asking the user which Tweaks Scripts UI variant to install
    // (Regular vs MEMM-Compatible). Returns true for MEMM, false for Regular, null on cancel.
    public class TweaksScriptsUIVersionDialog : System.Windows.Controls.UserControl
    {
        public TweaksScriptsUIVersionDialog()
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
                Text = "Select Version",
                FontSize = 18,
                FontWeight = System.Windows.FontWeights.Bold,
                Margin = new System.Windows.Thickness(0, 0, 0, 16)
            };

            var messageText = new System.Windows.Controls.TextBlock
            {
                Text = "Which version of Tweaks Scripts UI would you like to install?",
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 16),
                MaxWidth = 450
            };

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            var regularButton = new System.Windows.Controls.Button
            {
                Content = "Regular",
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("MaterialDesignRaisedButton")
            };
            regularButton.Click += (s, e) => MaterialDesignThemes.Wpf.DialogHost.CloseDialogCommand.Execute(false, regularButton);

            var memmButton = new System.Windows.Controls.Button
            {
                Content = "MEMM-Compatible",
                Margin = new System.Windows.Thickness(0, 0, 8, 0),
                Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("MaterialDesignRaisedButton")
            };
            memmButton.Click += (s, e) => MaterialDesignThemes.Wpf.DialogHost.CloseDialogCommand.Execute(true, memmButton);

            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("MaterialDesignOutlinedButton")
            };
            cancelButton.Click += (s, e) => MaterialDesignThemes.Wpf.DialogHost.CloseDialogCommand.Execute(null, cancelButton);

            buttonPanel.Children.Add(regularButton);
            buttonPanel.Children.Add(memmButton);
            buttonPanel.Children.Add(cancelButton);

            stackPanel.Children.Add(titleText);
            stackPanel.Children.Add(messageText);
            stackPanel.Children.Add(buttonPanel);

            border.Child = stackPanel;
            Content = border;
        }
    }
}
