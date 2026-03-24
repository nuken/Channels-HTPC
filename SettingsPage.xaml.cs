using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Reflection;

namespace FeralCode
{
    public partial class SettingsPage : Page
    {
        private UserSettings _settings;

        public SettingsPage()
        {
            InitializeComponent();
            _settings = SettingsManager.Load();
            
            // Populate the UI with current settings
            ManualServerIpBox.Text = _settings.LastServerAddress;
            AutoSkipCheckBox.IsChecked = _settings.AutoSkipCommercials;
            LightModeCheckBox.IsChecked = _settings.IsLightTheme;
            FullscreenCheckBox.IsChecked = _settings.StartPlayersFullscreen;
			ShowExtendedMetadata.IsChecked = _settings.ShowExtendedMetadata;
			ForceAacCheckBox.IsChecked = _settings.ForceAacAudio;            
            // NEW: Load the Logging Setting
            EnableLoggingCheckBox.IsChecked = _settings.EnableDebugLogging;

            // NEW: Load Guide Duration
            if (_settings.GuideDurationHours == 8) GuideDurationBox.SelectedIndex = 1;
            else if (_settings.GuideDurationHours == 12) GuideDurationBox.SelectedIndex = 2;
            else GuideDurationBox.SelectedIndex = 0; // Default to 4
			
			StickyHeadersCheckBox.IsChecked = _settings.StickyGuideHeaders;

            // --- NEW: Display the formatted Mobile Remote URL ---
            string localIp = GetLocalIPAddress();
            LocalRemoteUrlBox.Text = $"http://{localIp}:{_settings.WebServerPort}";
			string localVersion = "1.0.0-beta"; // Fallback
            try
            {
                var versionAttr = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (versionAttr != null) localVersion = versionAttr.InformationalVersion;
            }
            catch { }
            
            CurrentVersionText.Text = $"v{localVersion}";

            this.Loaded += Page_Loaded;
        }
		
		// --- NEW: VERSION CHECK LOGIC ---
        private async void CheckVersion_Click(object sender, RoutedEventArgs e)
        {
            CheckVersionBtn.IsEnabled = false;
            CheckVersionBtn.Content = "Checking...";

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    // WARNING: Replace this URL with the RAW link to your version.txt on GitHub or your server!
                    // The ?t= trick prevents aggressive caching so it always gets the latest file.
                    string repoUrl = $"https://raw.githubusercontent.com/nuken/Feral-HTPC/refs/heads/main/version.txt";
                    
                    string remoteVersion = await client.GetStringAsync(repoUrl);
                    remoteVersion = remoteVersion.Trim();

                    // Strip the "v" from our UI text so we are just comparing "1.0.0-beta" to "1.0.0-beta"
                    string localVersion = CurrentVersionText.Text.Replace("v", "").Trim();

                    if (string.Equals(remoteVersion, localVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show($"You are running the latest version!\n\nCurrent Version: {localVersion}", "Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show($"A new version is available!\n\nYour Version: {localVersion}\nLatest Version: {remoteVersion}\n\nPlease check the project repository to download the latest release.", "Update Available", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                    }
                }
            }
            catch
            {
                MessageBox.Show("Could not connect to the update server. Please check your internet connection and try again later.", "Update Check Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                // Reset the button UI
                CheckVersionBtn.IsEnabled = true;
                CheckVersionBtn.Content = "Check for Updates";
            }
        }

        // --- NEW: Helper to find the physical network IP ---
        private string GetLocalIPAddress()
        {
            try
            {
                // Create a dummy UDP socket. It doesn't actually connect over the network, 
                // but it forces Windows to reveal the primary local network adapter's IP.
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint? endPoint = socket.LocalEndPoint as IPEndPoint;
                    
                    if (endPoint != null)
                    {
                        return endPoint.Address.ToString();
                    }
                }
            }
            catch 
            {
                // Silently swallow errors if the PC is completely offline
            }
            
            // Fallback if the socket trick fails
            return "127.0.0.1"; 
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            ManualServerIpBox.Focus(); 
            RefreshStreamsList(); 

            // --- NEW: Auto-Discover Local Channels DVR Servers ---
            try
            {
                var api = new ChannelsApi();
                var discoveredServers = await api.DiscoverDvrServersAsync();
                
                DiscoveredServersPanel.Children.Clear(); // Remove "Searching..." text

                // --- NEW: Filter out duplicate IPs (IPv4/IPv6 overlaps) ---
                var uniqueServers = discoveredServers
                    .GroupBy(s => s.BaseUrl)
                    .Select(g => g.First())
                    .ToList();

                if (uniqueServers.Count == 0)
                {
                    var noServersText = new TextBlock { Text = "No local servers found.", FontStyle = FontStyles.Italic };
                    noServersText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                    DiscoveredServersPanel.Children.Add(noServersText);
                }
                else
                {
                    foreach (var server in uniqueServers) // Correctly loops through uniqueServers!
                    {
                        var rb = new RadioButton
                        {
                            Content = $"{server.Name} ({server.BaseUrl})",
                            Tag = server.BaseUrl,
                            FontSize = 16,
                            Margin = new Thickness(0, 5, 0, 5)
                        };
                        rb.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimary");
                        
                        rb.Checked += (s, ev) => 
                        { 
                            // When a user clicks a discovered server, clear the manual box
                            ManualServerIpBox.Text = ""; 
                        };

                        // If this discovered server matches the saved one, select it and clear the manual box
                        if (server.BaseUrl == _settings.LastServerAddress)
                        {
                            rb.IsChecked = true;
                            ManualServerIpBox.Text = "";
                        }

                        DiscoveredServersPanel.Children.Add(rb);
                    }
                }
            }
            catch 
            { 
                DiscoveredServersPanel.Children.Clear();
                var errorText = new TextBlock { Text = "Network discovery unavailable.", FontStyle = FontStyles.Italic };
                errorText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                DiscoveredServersPanel.Children.Add(errorText);
            }
        }

        // --- NEW: Sync the list and the textbox ---
        private void ManualServerIpBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // If the user starts typing manually, uncheck all discovered servers
            if (!string.IsNullOrWhiteSpace(ManualServerIpBox.Text) && DiscoveredServersPanel != null)
            {
                foreach (var child in DiscoveredServersPanel.Children)
                {
                    if (child is RadioButton rb) rb.IsChecked = false;
                }
            }
        }

        // --- EXTERNAL STREAM LOGIC ---

        private void RefreshStreamsList()
        {
            SavedStreamsList.Children.Clear();

            if (_settings.ExternalStreams.Count == 0)
            {
                var emptyText = new TextBlock { Text = "No external streams added yet.", FontStyle = FontStyles.Italic, Margin = new Thickness(5) };
                emptyText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                SavedStreamsList.Children.Add(emptyText);
                return;
            }

            foreach (var stream in _settings.ExternalStreams)
            {
                var border = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(15, 10, 15, 10), Margin = new Thickness(0, 0, 0, 8) };
                border.SetResourceReference(Border.BackgroundProperty, "CardBackground");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                
                var titleText = new TextBlock { Text = stream.Title, FontSize = 16, FontWeight = FontWeights.Bold };
                titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                
                var subText = new TextBlock { Text = $"{stream.Service}  •  {stream.StreamId}", FontSize = 12 };
                subText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                
                textStack.Children.Add(titleText);
                textStack.Children.Add(subText);

                var delBtn = new Button { Content = "❌", Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 16, Cursor = Cursors.Hand, Tag = stream.Id };
                delBtn.SetResourceReference(Button.ForegroundProperty, "StatusError");
                delBtn.Click += DeleteStream_Click;

                Grid.SetColumn(textStack, 0);
                Grid.SetColumn(delBtn, 1);
                grid.Children.Add(textStack);
                grid.Children.Add(delBtn);
                border.Child = grid;

                SavedStreamsList.Children.Add(border);
            }
        }

        private void AddStream_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(StreamTitleBox.Text) || string.IsNullOrWhiteSpace(StreamIdBox.Text))
            {
                MessageBox.Show("Please enter both a Title and a Deep Link ID.", "Missing Info", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var newStream = new ExternalStream
            {
                Title = StreamTitleBox.Text.Trim(),
                Service = ((ComboBoxItem)StreamServiceBox.SelectedItem).Content.ToString() ?? "Custom URI",
                StreamId = StreamIdBox.Text.Trim()
            };

            _settings.ExternalStreams.Add(newStream);
            SettingsManager.Save(_settings); 
            
            StreamTitleBox.Text = "";
            StreamIdBox.Text = "";
            
            RefreshStreamsList();
        }

        private void DeleteStream_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var streamToRemove = _settings.ExternalStreams.FirstOrDefault(s => s.Id == id);
                if (streamToRemove != null)
                {
                    _settings.ExternalStreams.Remove(streamToRemove);
                    SettingsManager.Save(_settings);
                    RefreshStreamsList();
                }
            }
        }

        // ----------------------------------

        // --- NEW: SAFE NAVIGATION ---
        private void HomeButton_Click(object sender, RoutedEventArgs e)
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

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // --- NEW: Format the Server Address for remote/manual connections ---
            // --- NEW: Get Server Address from either the Manual Box or the Discovered List ---
            string serverInput = ManualServerIpBox.Text.Trim();

            // If the manual box is empty, check if a discovered server is selected
            if (string.IsNullOrWhiteSpace(serverInput))
            {
                var selectedRadio = DiscoveredServersPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
                if (selectedRadio != null)
                {
                    serverInput = selectedRadio.Tag?.ToString() ?? "";
                }
            }

            // Validation check to prevent saving a completely blank server URL
            if (string.IsNullOrWhiteSpace(serverInput))
            {
                MessageBox.Show("Please select a discovered server or enter one manually.", "Missing Server Address", MessageBoxButton.OK, MessageBoxImage.Warning);
                return; // Stop the save process
            }

            if (!string.IsNullOrWhiteSpace(serverInput))
            {
                // 1. If the user forgot http:// or https://, add it automatically
                if (!serverInput.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    serverInput = "http://" + serverInput;
                }

                // 2. If the user didn't specify a port (e.g., the only colon is in "http://"), add the default :8089
                int colonIndex = serverInput.IndexOf(':', serverInput.IndexOf("://") + 3);
                if (colonIndex == -1)
                {
                    serverInput += ":8089";
                }

                // 3. Update the text box so the user sees the corrected URL
                ManualServerIpBox.Text = serverInput;
            }
            
            _settings.LastServerAddress = serverInput;
            // -------------------------------------------------------------------

            _settings.AutoSkipCommercials = AutoSkipCheckBox.IsChecked ?? true;
            _settings.IsLightTheme = LightModeCheckBox.IsChecked ?? false;
            _settings.StartPlayersFullscreen = FullscreenCheckBox.IsChecked ?? false;
            _settings.StickyGuideHeaders = StickyHeadersCheckBox.IsChecked ?? true;
			_settings.ShowExtendedMetadata = ShowExtendedMetadata.IsChecked ?? false;
			_settings.ForceAacAudio = ForceAacCheckBox.IsChecked ?? true;
            
            if (GuideDurationBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int parsedHours))
            {
                _settings.GuideDurationHours = parsedHours;
            }
            _settings.EnableDebugLogging = EnableLoggingCheckBox.IsChecked ?? false;
			AppLogger.IsEnabled = _settings.EnableDebugLogging;
            SettingsManager.Save(_settings);
            ApplyTheme(_settings.IsLightTheme);

            SaveStatusText.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = false;
            
            Task.Delay(1500).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() => 
                {
                    SaveStatusText.Visibility = Visibility.Collapsed;
                    SaveButton.IsEnabled = true;
                });
            });
        }

        private void ApplyTheme(bool isLight)
        {
            string themeName = isLight ? "LightTheme.xaml" : "DarkTheme.xaml";
            try 
            {
                var newDictionary = new ResourceDictionary { Source = new Uri($"Themes/{themeName}", UriKind.Relative) };
                Application.Current.Resources.MergedDictionaries.Clear();
                Application.Current.Resources.MergedDictionaries.Add(newDictionary);
            }
            catch { }
        }
		
		private void ForceAacCheckBox_Click(object sender, RoutedEventArgs e)
        {
            var settings = SettingsManager.Load();
            settings.ForceAacAudio = ForceAacCheckBox.IsChecked ?? true;
            SettingsManager.Save(settings);
        }

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Prevent Backspace from exiting the page if typing in a TextBox
            if (e.Key == Key.Back && e.OriginalSource is TextBox)
            {
                return; // Let the TextBox handle the backspace normally!
            }

            // --- NEW: SAFE NAVIGATION ---
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                e.Handled = true; // Stop WPF native navigation
                
                if (NavigationService != null && NavigationService.CanGoBack)
                {
                    NavigationService.GoBack();
                }
                else
                {
                    NavigationService?.Navigate(new StartPage());
                }
                return;
            }

            if (e.Key == Key.BrowserHome)
            {
                NavigationService?.Navigate(new StartPage());
                e.Handled = true;
                return;
            }
        }
    }
}