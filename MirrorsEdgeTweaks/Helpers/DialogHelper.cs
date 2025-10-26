using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Text.RegularExpressions;
using MaterialDesignThemes.Wpf;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class DialogHelper
    {
        private static bool _isDialogOpen = false;
        private static readonly object _dialogLock = new object();

        public static async Task<bool> ShowConfirmationAsync(string title, string message)
        {
            lock (_dialogLock)
            {
                if (_isDialogOpen)
                {
                    return false;
                }
                _isDialogOpen = true;
            }

            try
            {
                var result = await DialogHost.Show(new ConfirmationDialog(title, message), "RootDialog");
                return result is bool boolResult && boolResult;
            }
            finally
            {
                lock (_dialogLock)
                {
                    _isDialogOpen = false;
                }
            }
        }

        public static async Task ShowMessageAsync(string title, string message, MessageType messageType = MessageType.Information)
        {
            lock (_dialogLock)
            {
                if (_isDialogOpen)
                {
                    return;
                }
                _isDialogOpen = true;
            }

            try
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    await DialogHost.Show(new MessageDialog(title, message, messageType), "RootDialog");
                });
            }
            finally
            {
                lock (_dialogLock)
                {
                    _isDialogOpen = false;
                }
            }
        }

        public static async void ShowMessage(string title, string message, MessageType messageType = MessageType.Information)
        {
            await ShowMessageAsync(title, message, messageType);
        }

        public enum MessageType
        {
            Information,
            Warning,
            Error,
            Success
        }
    }

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

    public class MessageDialog : System.Windows.Controls.UserControl
    {
        public MessageDialog(string title, string message, DialogHelper.MessageType messageType)
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
