using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks; 
using System.Runtime.InteropServices; 

namespace FeralCode
{
    public partial class ExternalStreamsPage : Page
    {
        // --- Import Windows API for Key and Mouse Injection ---
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        private const byte VK_F11 = 0x7A;
        private const byte VK_F = 0x46;
        private const byte VK_K = 0x4B;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

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

    // A clean, uniform dark blue for all buttons (you can tweak these RGB values to your liking!)
    var uniformBlue = new SolidColorBrush(Color.FromRgb(30, 60, 110));

    foreach (var stream in settings.ExternalStreams)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("AppButton"),
            Tag = stream,
            Background = uniformBlue
        };

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        // 1. Load and Add the Service Icon
        var icon = new Image
        {
            Source = GetServiceIcon(stream.Service),
            Width = 60,
            Height = 60,
            Margin = new Thickness(0, 0, 0, 12),
            Stretch = Stretch.Uniform
        };

        // 2. Add the User's Custom Title
        var titleText = new TextBlock 
        { 
            Text = stream.Title, 
            FontSize = 18, 
            FontWeight = FontWeights.Bold, 
            TextWrapping = TextWrapping.Wrap, 
            TextAlignment = TextAlignment.Center, 
            Foreground = Brushes.White 
        };

        // Safety check: If the icon loads, show it. If you forgot to add the image file, it falls back to text.
        if (icon.Source != null)
        {
            stack.Children.Add(icon);
        }
        else
        {
            var fallbackText = new TextBlock { Text = stream.Service.ToUpper(), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,0,0,5) };
            stack.Children.Add(fallbackText);
        }

        stack.Children.Add(titleText);
        btn.Content = stack;
        
        btn.Click += Stream_Click;

        StreamsWrapPanel.Children.Add(btn);
    }

    if (StreamsWrapPanel.Children.Count > 0)
        ((UIElement)StreamsWrapPanel.Children[0]).Focus();
}

// --- NEW: Helper method to load images from the Assets folder ---
private System.Windows.Media.ImageSource? GetServiceIcon(string service)
{
    // Maps the service dropdown name to your specific image files
    string fileName = service.ToLower() switch
    {
        "netflix" => "netflix.png",
        "disney+" => "disneyplus.png",
        "youtube" => "youtube.png",
        _ => "custom.png" // The fallback icon for Custom URIs
    };

    try
    {
        // Loads the image directly from the compiled app resources
        return new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/{fileName}"));
    }
    catch
    {
        return null; 
    }
}
        private async void Stream_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is ExternalStream stream)
            {
                try
                {
                    var settings = SettingsManager.Load();
                    var windowStyle = settings.StartPlayersFullscreen ? ProcessWindowStyle.Maximized : ProcessWindowStyle.Normal;

                    if (stream.Service.ToLower() == "netflix")
                    {
                        string input = stream.StreamId.Trim();
                        string finalUrl = input.StartsWith("http") ? input : $"https://www.netflix.com/watch/{input}";
                        
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "msedge", 
                            Arguments = $"--app=\"{finalUrl}\"",
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
                            Arguments = $"--app=\"{finalUrl}\"",
                            UseShellExecute = true,
                            WindowStyle = windowStyle
                        });
                    }
                    else if (stream.Service.ToLower() == "youtube")
                    {
                        string input = stream.StreamId.Trim();
                        string finalUrl = (string.IsNullOrWhiteSpace(input) || input.Equals("app", StringComparison.OrdinalIgnoreCase)) 
                            ? "https://www.youtube.com/tv" 
                            : (input.StartsWith("http") ? input : $"https://www.youtube.com/watch?v={input}");

                        Process.Start(new ProcessStartInfo { 
                            FileName = "msedge", 
                            Arguments = $"--app=\"{finalUrl}\" --start-fullscreen", 
                            UseShellExecute = true, 
                            WindowStyle = windowStyle 
                        });
                    }
                    else
                    {
                        // Custom URI Fallback
                        string uri = BuildUri(stream);
                        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true, WindowStyle = windowStyle });
                    }

                    // The HTPC Fullscreen & YouTube Injection Bypass
                    if (settings.StartPlayersFullscreen)
                    {
                        // Wait for the Edge PWA window to initially render
                        await Task.Delay(1500); 
                        
                        keybd_event(VK_F11, 0, 0, 0);      // F11 down
                        await Task.Delay(50);
                        keybd_event(VK_F11, 0, KEYEVENTF_KEYUP, 0); // F11 up

                        if (stream.Service.ToLower() == "youtube")
                        {
                            // Give YouTube's standard web player time to load the video
                            await Task.Delay(2500); 

                            // Calculate center of screen
                            int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
                            int screenHeight = (int)SystemParameters.PrimaryScreenHeight;

                            // 1. Move mouse to the upper-center of the screen
                            SetCursorPos(screenWidth / 2, screenHeight / 3);

                            // 2. Send a quick Left Mouse Click to register interaction with the document
                            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

                            await Task.Delay(100); 

                            // 3. Send the 'f' key
                            keybd_event(VK_F, 0, 0, 0);      
                            await Task.Delay(50);
                            keybd_event(VK_F, 0, KEYEVENTF_KEYUP, 0); 
                            
                            await Task.Delay(100);

                            // 4. Send the 'k' key to unpause
                            keybd_event(VK_K, 0, 0, 0);      
                            await Task.Delay(50);
                            keybd_event(VK_K, 0, KEYEVENTF_KEYUP, 0); 

                            // 5. Hide the mouse by throwing it into the extreme bottom-right corner 
                            SetCursorPos(9999, 9999); 
                        }
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
                "netflix" => id.StartsWith("http") ? id : $"https://www.netflix.com/watch/{id}",
                "disney+" => id.StartsWith("http") ? id : $"https://www.disneyplus.com/play/{id}",
                "youtube" => id.StartsWith("http") ? id : $"https://www.youtube.com/watch?v={id}",
                _ => id // Returns the exact custom URI input
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