using MaterialDesignThemes.Wpf;
using MirrorsEdgeTweaks.Services;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace MirrorsEdgeTweaks.Helpers
{
    public class MessageDialog : System.Windows.Controls.UserControl
    {
        public MessageDialog(string title, string message, DialogMessageType messageType)
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
                TextWrapping = TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 16),
                MaxWidth = 450
            };

            ParseAndAddInlines(messageText, message);

            var okButton = new System.Windows.Controls.Button
            {
                Content = "OK",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)System.Windows.Application.Current.FindResource("MaterialDesignRaisedButton")
            };
            okButton.Click += (s, e) => DialogHost.CloseDialogCommand.Execute(null, okButton);

            stackPanel.Children.Add(titleText);
            stackPanel.Children.Add(messageText);
            stackPanel.Children.Add(okButton);

            border.Child = stackPanel;
            Content = border;
        }

        private static void ParseAndAddInlines(System.Windows.Controls.TextBlock textBlock, string message)
        {
            string urlPattern = @"(https?://[^\s]+)";
            var matches = Regex.Matches(message, urlPattern);

            if (matches.Count == 0)
            {
                textBlock.Text = message;
                return;
            }

            int lastIndex = 0;
            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                {
                    string textBefore = message.Substring(lastIndex, match.Index - lastIndex);
                    textBlock.Inlines.Add(new Run(textBefore));
                }

                string url = match.Value;
                var hyperlink = new Hyperlink(new Run(url))
                {
                    NavigateUri = new Uri(url)
                };
                hyperlink.RequestNavigate += (s, e) =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = e.Uri.AbsoluteUri,
                        UseShellExecute = true
                    });
                    e.Handled = true;
                };
                textBlock.Inlines.Add(hyperlink);

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < message.Length)
            {
                string textAfter = message.Substring(lastIndex);
                textBlock.Inlines.Add(new Run(textAfter));
            }
        }
    }
}
