using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FeralCode
{
    public partial class MultiviewSetupPage : Page
    {
        private List<Channel> _masterChannelList = new List<Channel>();
        private List<ChannelCollection> _collections = new List<ChannelCollection>();
        private List<Channel> _selectedChannels = new List<Channel>();
        private string _baseUrl = "";
        private UserSettings _settings;
        private Button? _lastFocusedChannel;
        private System.Windows.Threading.DispatcherTimer _searchTimer = new System.Windows.Threading.DispatcherTimer();

        public MultiviewSetupPage()
        {
            InitializeComponent();
            _settings = SettingsManager.Load();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
            _searchTimer.Tick += (s, args) =>
            {
                _searchTimer.Stop();
                ApplyFilters();
            };
            this.Loaded += Page_Loaded;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
{
    _settings = SettingsManager.Load();
    _baseUrl = _settings.LastServerAddress;

    var api = new ChannelsApi();

    // Try saved IP first. If blank, fallback to network discovery.
    if (string.IsNullOrWhiteSpace(_baseUrl))
    {
        var servers = await api.DiscoverDvrServersAsync();
        if (servers.Any())
        {
            _baseUrl = servers.First().BaseUrl;
            _settings.LastServerAddress = _baseUrl;
            SettingsManager.Save(_settings); // Auto-save the discovered IP
        }
        else
        {
            return; // Silently fail if no server is found, just like before
        }
    }

    var rawChannels = await api.GetChannelsAsync(_baseUrl);
    var cleanChannels = rawChannels.Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Number)).ToList();

    // --- Fetch 1 hour of guide data to get the currently airing shows ---
    try
            {
                // We pass '1' to only grab a tiny, fast slice of the timeline
                var guideBlocks = await api.GetGuideAsync(_baseUrl, 1);
                var validGuideChannels = guideBlocks.Where(g => !string.IsNullOrWhiteSpace(g.ChannelNumber) && g.Airings != null).ToList();
                var guideDict = validGuideChannels.GroupBy(g => g.ChannelNumber!).ToDictionary(g => g.Key, g => g.First());
                
                DateTime now = DateTime.Now;

                foreach (var channel in cleanChannels)
                {
                    if (channel.Number != null && guideDict.TryGetValue(channel.Number.Trim(), out var guideData))
                    {
                        // --- NEW: Inject the clean Favorite flag directly from the Guide payload! ---
                        channel.Favorite = guideData.IsFavorite;

                        // Filter the airings to strictly the show playing RIGHT NOW
                        channel.CurrentAirings = guideData.Airings?
                            .Where(a => a.StartTime <= now && a.StartTime.AddSeconds(a.Duration ?? 0) > now)
                            .ToList();
                    }
                }
            }
            catch 
            { 
                // Silently fail and fallback to "Unknown Programming" if the guide is offline 
            }

            _masterChannelList = cleanChannels;
            
            // Fetch Collections
            try
            {
                _collections = await api.GetChannelCollectionsAsync(_baseUrl);
                CollectionComboBox.Items.Clear();
                CollectionComboBox.Items.Add("All Channels");
                CollectionComboBox.Items.Add("Favorites"); // --- NEW: Add to Multiview Dropdown ---
                
                foreach (var collection in _collections)
                {
                    CollectionComboBox.Items.Add(collection.name);
                }
                
                if (!string.IsNullOrEmpty(_settings.LastCollection) && CollectionComboBox.Items.Contains(_settings.LastCollection))
                {
                    CollectionComboBox.SelectedItem = _settings.LastCollection;
                }
                else 
                {
                    CollectionComboBox.SelectedIndex = 0;
                }
            }
            catch
            {
                CollectionComboBox.Items.Add("All Channels");
                CollectionComboBox.Items.Add("Favorites"); // --- NEW: Add to fallback as well ---
                CollectionComboBox.SelectedIndex = 0;
            }

            ApplyFilters();
        }

        private void CollectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CollectionComboBox.SelectedItem != null)
            {
                _settings.LastCollection = CollectionComboBox.SelectedItem.ToString() ?? "All Channels";
                SettingsManager.Save(_settings);
            }
            ApplyFilters();
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_masterChannelList == null || _masterChannelList.Count == 0) return;

            // Reset the timer on every keystroke instead of filtering instantly
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void ApplyFilters()
        {
            if (_masterChannelList == null || !_masterChannelList.Any()) return;

            var query = SearchTextBox.Text?.Trim() ?? string.Empty;
            var selectedCollectionName = CollectionComboBox.SelectedItem?.ToString() ?? "All Channels";
            
            IEnumerable<Channel> filtered = _masterChannelList;

            if (selectedCollectionName == "Favorites")
            {
                // --- NEW: Filter by the native Channels DVR Favorite flag ---
                filtered = filtered.Where(c => c.Favorite);
            }
            else if (selectedCollectionName != "All Channels")
            {
                var selectedCollection = _collections.FirstOrDefault(c => c.name == selectedCollectionName);
                if (selectedCollection != null)
                {
                    filtered = filtered.Where(c => 
                    {
                        // 1. STAGE 1: Check Excluded Sources
                        if (selectedCollection.excluded_sources != null && selectedCollection.excluded_sources.Count > 0)
                        {
                            if (selectedCollection.excluded_sources.Any(es => c.Id != null && c.Id.IndexOf(es, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                return false; // Immediately drop if it comes from an excluded source
                            }
                        }

                        // 2. STAGE 2: Explicit Items Override
                        bool isExplicitItem = selectedCollection.items != null && selectedCollection.items.Any(item => c.IsExactMatch(item));
                        if (isExplicitItem) return true;

                        // 3. STAGE 3: Evaluate Smart Rules
                        bool hasRules = (selectedCollection.genres != null && selectedCollection.genres.Count > 0) ||
                                        (selectedCollection.categories != null && selectedCollection.categories.Count > 0) ||
                                        (selectedCollection.tags != null && selectedCollection.tags.Count > 0) ||
                                        (selectedCollection.keywords != null && selectedCollection.keywords.Count > 0);

                        if (!hasRules) return false;

                        // Grab what is currently airing on this channel to evaluate against
                        var currentAiring = c.CurrentAirings?.FirstOrDefault();
                        if (currentAiring == null) return false; 

                        bool passesRules = false; 

                        // --- Rule: GENRES ---
                        if (!passesRules && selectedCollection.genres != null && selectedCollection.genres.Count > 0)
                        {
                            if (currentAiring.Genres != null && currentAiring.Genres.Intersect(selectedCollection.genres, StringComparer.OrdinalIgnoreCase).Any()) passesRules = true;
                            if (currentAiring.Categories != null && currentAiring.Categories.Intersect(selectedCollection.genres, StringComparer.OrdinalIgnoreCase).Any()) passesRules = true;
                        }

                        // --- Rule: CATEGORIES ---
                        if (!passesRules && selectedCollection.categories != null && selectedCollection.categories.Count > 0)
                        {
                            if (currentAiring.Categories != null && currentAiring.Categories.Intersect(selectedCollection.categories, StringComparer.OrdinalIgnoreCase).Any()) passesRules = true;
                            if (currentAiring.Genres != null && currentAiring.Genres.Intersect(selectedCollection.categories, StringComparer.OrdinalIgnoreCase).Any()) passesRules = true;
                        }

                        // --- Rule: TAGS ---
                        if (!passesRules && selectedCollection.tags != null && selectedCollection.tags.Count > 0)
                        {
                            if (currentAiring.Tags != null && currentAiring.Tags.Intersect(selectedCollection.tags, StringComparer.OrdinalIgnoreCase).Any()) passesRules = true;
                            if (currentAiring.Categories != null && currentAiring.Categories.Intersect(selectedCollection.tags, StringComparer.OrdinalIgnoreCase).Any()) passesRules = true;
                        }

                        // --- Rule: KEYWORDS ---
                        if (!passesRules && selectedCollection.keywords != null && selectedCollection.keywords.Count > 0)
                        {
                            string searchBlock = $"{currentAiring.Title} {currentAiring.EpisodeTitle} {currentAiring.DisplaySummary}".ToLower();
                            if (selectedCollection.keywords.Any(kw => searchBlock.Contains(kw.ToLower()))) passesRules = true;
                        }

                        return passesRules;
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = filtered.Where(c => c.HasIdentifier(query));
            }

            ChannelsListControl.ItemsSource = filtered.ToList();

            // Auto-focus the first channel so the D-Pad is instantly ready!
            if (!SearchTextBox.IsKeyboardFocusWithin)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    var request = new TraversalRequest(FocusNavigationDirection.First);
                    ChannelsListControl.MoveFocus(request);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        private void Channel_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedChannels.Count >= 4) return;
            
            if (sender is Button btn && btn.Tag is Channel channel)
            {
                if (!_selectedChannels.Contains(channel))
                {
                    _selectedChannels.Add(channel);
                    UpdateSlots();
                }
            }
        }

        private void UpdateSlots()
        {
            Slot1Text.Text = _selectedChannels.Count > 0 ? $"Slot 1: {_selectedChannels[0].Name}" : "Slot 1: Empty";
            Slot2Text.Text = _selectedChannels.Count > 1 ? $"Slot 2: {_selectedChannels[1].Name}" : "Slot 2: Empty";
            Slot3Text.Text = _selectedChannels.Count > 2 ? $"Slot 3: {_selectedChannels[2].Name}" : "Slot 3: Empty";
            Slot4Text.Text = _selectedChannels.Count > 3 ? $"Slot 4: {_selectedChannels[3].Name}" : "Slot 4: Empty";

            // Enable launch if at least 2 channels are selected
            LaunchButton.IsEnabled = _selectedChannels.Count > 1;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _selectedChannels.Clear();
            UpdateSlots();
        }

        private void Launch_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = (MainWindow)Application.Current.MainWindow;
            var quadWindow = new QuadPlayerWindow(_baseUrl, _selectedChannels);
            
            // --- Hide & Seek ---
            quadWindow.Closed += (s, args) => 
            {
                if (_settings.MinimizeOnPlay) mainWindow.WindowState = WindowState.Normal;
                mainWindow.Show(); // 1. Bring the main basecamp back!
                Application.Current.Dispatcher.InvokeAsync(() => 
                {
                    LaunchButton.Focus(); 
                }, System.Windows.Threading.DispatcherPriority.Input);
            };

            if (_settings.MinimizeOnPlay) mainWindow.WindowState = WindowState.Minimized;
            else mainWindow.Hide(); // 2. Hide the basecamp
            
            quadWindow.Show(); // 3. Launch the quad player
        }

        // --- SAFE NAVIGATION ---
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

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Prevent Backspace from exiting the page if typing in a TextBox
            if (e.Key == Key.Back && e.OriginalSource is TextBox)
            {
                return; // Let the TextBox handle the backspace normally!
            }

            // Safely close the dropdown if it's open, instead of leaving the page!
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                if (CollectionComboBox.IsDropDownOpen)
                {
                    CollectionComboBox.IsDropDownOpen = false;
                    e.Handled = true;
                    return;
                }
                
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

            // --- THE DROPDOWN BYPASS ---
            // If the menu is actively dropping down, step back and let Windows handle the arrows natively!
            if (CollectionComboBox.IsDropDownOpen)
            {
                if (e.Key == Key.Left || e.Key == Key.Right) e.Handled = true; // Trap left/right so they don't break the menu
                return; 
            }

            var focusedElement = Keyboard.FocusedElement as FrameworkElement;

            // --- EXPLICIT D-PAD ROUTING ---
            if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Enter)
            {
                // ZONE 1: Collection Dropdown
                if (focusedElement == CollectionComboBox)
                {
                    if (e.Key == Key.Enter)
                    {
                        CollectionComboBox.IsDropDownOpen = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Down)
                    {
                        if (_lastFocusedChannel != null && _lastFocusedChannel.IsVisible) _lastFocusedChannel.Focus();
                        else ChannelsListControl.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Right)
                    {
                        SearchTextBox.Focus();
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Up || e.Key == Key.Left)
                    {
                        e.Handled = true; // Bounce off the walls
                        return;
                    }
                }

                // ZONE 2: Search Box
                if (focusedElement == SearchTextBox)
                {
                    if (e.Key == Key.Down)
                    {
                        if (_lastFocusedChannel != null && _lastFocusedChannel.IsVisible) _lastFocusedChannel.Focus();
                        else ChannelsListControl.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Left)
                    {
                        CollectionComboBox.Focus();
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Right)
                    {
                        if (LaunchButton.IsEnabled) LaunchButton.Focus();
                        else ClearButton.Focus();
                        e.Handled = true;
                        return;
                    }
                }

                // ZONE 3: Channel List
                if (focusedElement is Button btn && btn.Tag is Channel)
                {
                    _lastFocusedChannel = btn; // Save our exact spot in the list!

                    if (e.Key == Key.Right)
                    {
                        if (LaunchButton.IsEnabled) LaunchButton.Focus();
                        else ClearButton.Focus();
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Left)
                    {
                        e.Handled = true; 
                        return;
                    }
                    else if (e.Key == Key.Up || e.Key == Key.Down)
                    {
                        btn.MoveFocus(new TraversalRequest(e.Key == Key.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down));
                        e.Handled = true;
                        return;
                    }
                }
                
                // ZONE 4: Right Action Panel
                if (focusedElement == LaunchButton || focusedElement == ClearButton)
                {
                    if (e.Key == Key.Left)
                    {
                        if (_lastFocusedChannel != null && _lastFocusedChannel.IsVisible)
                            _lastFocusedChannel.Focus();
                        else
                            ChannelsListControl.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                            
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Right)
                    {
                        e.Handled = true; 
                        return;
                    }
                    else if (e.Key == Key.Up || e.Key == Key.Down)
                    {
                        focusedElement.MoveFocus(new TraversalRequest(e.Key == Key.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down));
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Quick Scroll Support
            if (e.Key == Key.PageUp || e.Key == Key.MediaPreviousTrack)
            {
                for (int i = 0; i < 4; i++)
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown || e.Key == Key.MediaNextTrack)
            {
                for (int i = 0; i < 4; i++)
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                e.Handled = true;
            }
        }
    }
}