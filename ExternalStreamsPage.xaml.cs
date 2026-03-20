using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks; // <-- Added for Task.Delay
using System.Runtime.InteropServices; // <-- Added for keybd_event

namespace FeralCode
{
    public partial class ExternalStreamsPage : Page
    {
        // --- NEW: Import Windows API for Key Injection ---
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        
        private const byte VK_F11 = 0x7A;
        private const uint KEYEVENTF_KEYUP = 0x0002;

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

                btn.Background = GetBrandColor(stream.Service);

                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                
                var serviceText = new TextBlock { Text = stream.Service.ToUpper(), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,5) };
                var titleText = new TextBlock { Text = stream.Title, FontSize = 22, FontWeight = FontWeights.Black, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brushes.White };

                stack.Children.Add(serviceText);
                stack.Children.Add(titleText);

                btn.Content = stack;
                
                // IMPORTANT: Changed to async so we can use Task.Delay
                btn.Click += Stream_Click;

                StreamsWrapPanel.Children.Add(btn);
            }

            if (StreamsWrapPanel.Children.Count > 0)
                ((UIElement)StreamsWrapPanel.Children[0]).Focus();
        }

        private SolidColorBrush GetBrandColor(string service)
        {
            return service.ToLower() switch
            {
                "netflix" => new SolidColorBrush(Color.FromRgb(229, 9, 20)),      
                "disney+" => new SolidColorBrush(Color.FromRgb(17, 60, 207)),     
                "hulu" => new SolidColorBrush(Color.FromRgb(28, 231, 131)),       
                "prime video" => new SolidColorBrush(Color.FromRgb(0, 168, 225)), 
                _ => new SolidColorBrush(Color.FromRgb(50, 50, 50))               
            };
        }

        // --- NEW: Added 'async' keyword to this method ---
        private async void Stream_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ExternalStream stream)
            {
                try
                {
                    var settings = SettingsManager.Load();
                    
                    // We also tell Windows to maximize the window right out of the gate, just to be safe
                    var windowStyle = settings.StartPlayersFullscreen ? ProcessWindowStyle.Maximized : ProcessWindowStyle.Normal;

                    if (stream.Service.ToLower() == "netflix")
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "msedge", 
                            Arguments = $"--app=https://www.netflix.com/watch/{stream.StreamId.Trim()}",
                            UseShellExecute = true,
                            WindowStyle = windowStyle
                        });
                    }
                    else if (stream.Service.ToLower() == "disney+")
                    {
                        string input = stream.StreamId.Trim();
                        string finalUrl = input.StartsWith("http") ? input : $"https://www.disneyplus.com/play/{input}";
                        
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "msedge", 
                            Arguments = $"--app={finalUrl}",
                            UseShellExecute = true,
                            WindowStyle = windowStyle
                        });
                    }
                    else
                    {
                        string uri = BuildUri(stream);
                        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true, WindowStyle = windowStyle });
                    }

                    // --- NEW: The HTPC F11 Injection Bypass ---
                    if (settings.StartPlayersFullscreen)
                    {
                        // Wait 1.5 seconds for the PWA window to fully render and steal the keyboard focus
                        await Task.Delay(1500); 
                        
                        // Programmatically tap the 'F11' key on behalf of the user to force true full screen!
                        keybd_event(VK_F11, 0, 0, 0);             // Key Down
                        keybd_event(VK_F11, 0, KEYEVENTF_KEYUP, 0); // Key Up
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
                _ => id 
            };
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService != null && NavigationService.CanGoBack)
            {
                NavigationService.GoBack();
            }
            else
            {
                NavigationService?.Navigate(new StartPage());
            }
        }

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                e.Handled = true; 
                
                if (NavigationService != null && NavigationService.CanGoBack)
                {
                    NavigationService.GoBack();
                }
                else
                {
                    NavigationService?.Navigate(new StartPage());
                }
            }
        }
    }
}
