using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FeralCode
{
    public partial class ExternalStreamsPage : Page
    {
        public ExternalStreamsPage()
        {
            InitializeComponent();
            this.Loaded += Page_Loaded;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            LoadStreams();
        }

        private void LoadStreams()
        {
            StreamsWrapPanel.Children.Clear();
            var settings = SettingsManager.Load();

            if (settings.ExternalStreams == null || settings.ExternalStreams.Count == 0)
            {
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            EmptyText.Visibility = Visibility.Collapsed;

            foreach (var stream in settings.ExternalStreams)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("AppButton"),
                    Tag = stream
                };

                // Automatically set the background color based on the streaming service!
                btn.Background = GetBrandColor(stream.Service);

                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                
                // Keep the subtext semi-transparent white
                var serviceText = new TextBlock { Text = stream.Service.ToUpper(), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,5) };
                
                // FIXED: Explicitly force the title to be White so Light Mode doesn't make it dark gray over the brand colors!
                var titleText = new TextBlock { Text = stream.Title, FontSize = 22, FontWeight = FontWeights.Black, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brushes.White };

                stack.Children.Add(serviceText);
                stack.Children.Add(titleText);

                btn.Content = stack;
                btn.Click += Stream_Click;

                StreamsWrapPanel.Children.Add(btn);
            }

            // Focus the first button automatically so the D-pad works instantly
            if (StreamsWrapPanel.Children.Count > 0)
                ((UIElement)StreamsWrapPanel.Children[0]).Focus();
        }

        private SolidColorBrush GetBrandColor(string service)
        {
            return service.ToLower() switch
            {
                "netflix" => new SolidColorBrush(Color.FromRgb(229, 9, 20)),      // Netflix Red
                "disney+" => new SolidColorBrush(Color.FromRgb(17, 60, 207)),     // Disney Blue
                "hulu" => new SolidColorBrush(Color.FromRgb(28, 231, 131)),       // Hulu Green
                "prime video" => new SolidColorBrush(Color.FromRgb(0, 168, 225)), // Prime Blue
                _ => new SolidColorBrush(Color.FromRgb(50, 50, 50))               // Custom Dark Grey
            };
        }

        private void Stream_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ExternalStream stream)
            {
                try
                {
                    // Intercept Netflix AND Disney+ to force the clean, chromeless PWA window
                    if (stream.Service.ToLower() == "netflix")
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "msedge", 
                            Arguments = $"--app=https://www.netflix.com/watch/{stream.StreamId.Trim()}",
                            UseShellExecute = true
                        });
                    }
                    else if (stream.Service.ToLower() == "disney+")
                    {
                        string input = stream.StreamId.Trim();
                        // If you pasted the full link, use it. Otherwise, build the /play/ URL!
                        string finalUrl = input.StartsWith("http") ? input : $"https://www.disneyplus.com/play/{input}";
                        
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "msedge", 
                            Arguments = $"--app={finalUrl}",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        // Hulu, Prime, and Custom continue using their native deep links
                        string uri = BuildUri(stream);
                        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not launch {stream.Service}.\n\nError: {ex.Message}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string BuildUri(ExternalStream stream)
        {
            string id = stream.StreamId.Trim();
            return stream.Service.ToLower() switch
            {
                "netflix" => $"https://www.netflix.com/watch/{id}",
                "disney+" => $"disneyplus://video/{id}",
                "hulu" => $"hulu://w/{id}",
                "prime video" => $"https://www.primevideo.com/watch/{id}",
                _ => id // If it's a Custom URI, we just pass the raw link they typed in!
            };
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => NavigationService?.GoBack();

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                NavigationService?.GoBack();
                e.Handled = true;
            }
        }
    }
}