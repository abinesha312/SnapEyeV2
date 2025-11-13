using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SnapEye.SolutionRegion
{
    public partial class SolutionRegionAlpha : UserControl
    {
        public SolutionRegionAlpha()
        {
            InitializeComponent();
            
            // Set default markdown content
            SetMarkdownContent("**Welcome to SnapEye AI Assistant!**\n\nAwaiting your request...");
        }

        #region Tab Switching
        
        private void ChatTab_Click(object sender, RoutedEventArgs e)
        {
            // Activate Chat tab
            ChatTab.Tag = "Active";
            TranscriptionTab.Tag = null;
            
            // Show Chat view, hide Transcription view
            ChatView.Visibility = Visibility.Visible;
            TranscriptionView.Visibility = Visibility.Collapsed;
        }

        private void TranscriptionTab_Click(object sender, RoutedEventArgs e)
        {
            // Activate Transcription tab
            ChatTab.Tag = null;
            TranscriptionTab.Tag = "Active";
            
            // Show Transcription view, hide Chat view
            ChatView.Visibility = Visibility.Collapsed;
            TranscriptionView.Visibility = Visibility.Visible;
        }

        #endregion

        #region Opacity Control

        // Method to update the region's opacity
        public void UpdateOpacity(double opacityValue)
        {
            // Apply opacity to the border background
            if (AlphaRegionBorder != null)
            {
                AlphaRegionBorder.Opacity = opacityValue;
            }
        }

        #endregion

        #region Chat Methods

        // Method to set markdown content
        public void SetMarkdownContent(string markdownText)
        {
            if (MarkdownViewer != null)
            {
                MarkdownViewer.Markdown = markdownText;
            }
        }

        // Method to append markdown content
        public void AppendMarkdownContent(string markdownText)
        {
            if (MarkdownViewer != null)
            {
                MarkdownViewer.Markdown += "\n\n" + markdownText;
            }
        }

        // Method to clear content
        public void ClearContent()
        {
            if (MarkdownViewer != null)
            {
                MarkdownViewer.Markdown = string.Empty;
            }
        }

        // Method to show AI response with formatted markdown
        public void ShowAIResponse(string response)
        {
            if (MarkdownViewer != null)
            {
                // Format the response with better styling
                string formattedResponse = $"## AI Response\n\n{response}";
                MarkdownViewer.Markdown = formattedResponse;
            }
        }

        // Method to show loading state
        public void ShowLoading()
        {
            if (MarkdownViewer != null)
            {
                MarkdownViewer.Markdown = "⏳ **Processing your request...**\n\nPlease wait while the AI generates a response.";
            }
        }

        // Method to show error
        public void ShowError(string errorMessage)
        {
            if (MarkdownViewer != null)
            {
                MarkdownViewer.Markdown = $"❌ **Error**\n\n{errorMessage}";
            }
        }

        // Example method to show sample AI response
        public void ShowSampleResponse()
        {
            string sampleMarkdown = @"# Hello from SnapEye AI! 👁️
                                      ## Quick Analysis

                                  Here's what I found:

### Key Points
- **Point 1**: This is an important observation
- **Point 2**: Another significant detail
- **Point 3**: Final key insight

### Code Example
```csharp
public void Example()
{
    Console.WriteLine(""Hello, SnapEye!"");
}
```

### Next Steps
1. Review the findings
2. Take appropriate action
3. Monitor results

> **Tip**: You can adjust the opacity using the slider above!

---
*Powered by AI Vision Technology*";

            SetMarkdownContent(sampleMarkdown);
        }

        #endregion

        #region Transcription Methods

        // Method to add a user message (right side)
        public void AddUserTranscription(string text)
        {
            if (TranscriptionPanel != null)
            {
                var messageContainer = new Grid
                {
                    Margin = new Thickness(0, 4, 0, 4)
                };

                messageContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                messageContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

                var messageBorder = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2A2A")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B794F7")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 6, 8, 6)
                };

                var textBlock = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };

                messageBorder.Child = textBlock;
                Grid.SetColumn(messageBorder, 1);
                messageContainer.Children.Add(messageBorder);

                TranscriptionPanel.Children.Add(messageContainer);
                
                // Auto-scroll to bottom
                TranscriptionView.ScrollToEnd();
            }
        }

        // Method to add a system message (left side)
        public void AddSystemTranscription(string text)
        {
            if (TranscriptionPanel != null)
            {
                var messageContainer = new Grid
                {
                    Margin = new Thickness(0, 4, 0, 4)
                };

                messageContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                messageContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var messageBorder = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1A1A")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8, 6, 8, 6)
                };

                var textBlock = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFFFF")),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };

                messageBorder.Child = textBlock;
                Grid.SetColumn(messageBorder, 0);
                messageContainer.Children.Add(messageBorder);

                TranscriptionPanel.Children.Add(messageContainer);
                
                // Auto-scroll to bottom
                TranscriptionView.ScrollToEnd();
            }
        }

        // Method to clear transcription
        public void ClearTranscription()
        {
            if (TranscriptionPanel != null)
            {
                TranscriptionPanel.Children.Clear();
                
                // Add default message
                var defaultText = new TextBlock
                {
                    Text = "🎤 Transcription session ready. Start speaking...",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888")),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                
                TranscriptionPanel.Children.Add(defaultText);
            }
        }

        #endregion
    }
}
