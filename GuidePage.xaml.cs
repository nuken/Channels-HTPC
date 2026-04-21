using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Collections.ObjectModel;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Input;

namespace FeralCode
{
    public partial class GuidePage : Page
    {
        private readonly ChannelsApi _api = new ChannelsApi();
        public double UiScale => _settings.UiScale > 0 ? _settings.UiScale : 1.0;
        public bool IsSimplifiedGuide => _settings.SimplifiedGuide;
        private List<Channel> _masterChannelList = new List<Channel>();
        private List<Channel> _currentFilteredList = new List<Channel>();
        private List<ChannelCollection> _collections = new List<ChannelCollection>(); 
        private System.Windows.Threading.DispatcherTimer _refreshTimer;
        private DateTime _currentGridStart;
        private ObservableCollection<Channel> _displayedChannels = new ObservableCollection<Channel>();
        private Airing? _selectedAiring;
        private UserSettings _settings;
        private System.Windows.Controls.Button? _lastFocusedAiringButton;      
        private DateTime _lastTimeFocus = DateTime.MinValue;
        private DateTime _lastLeftKeyPressTime = DateTime.MinValue; 
        private System.Windows.Threading.DispatcherTimer _searchTimer = new System.Windows.Threading.DispatcherTimer();
        private string _activeTag = "All Channels";
		
        public GuidePage()
        {
            InitializeComponent();
			
            this.DataContext = this;
            ChannelItemsControl.ItemsSource = _displayedChannels;
            GuideItemsControl.ItemsSource = _displayedChannels;
            
            _settings = SettingsManager.Load();
            ApplyTheme(_settings.IsLightTheme);
            _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
            _searchTimer.Tick += (s, args) =>
            {
                _searchTimer.Stop();
                ApplyFilters();
            };
            
            _refreshTimer = new System.Windows.Threading.DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromMinutes(1);
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
            
            this.Loaded += Page_Loaded;
        }
		
		// Caches the scroll viewer so we don't have to search the visual tree on every tick
        private ScrollViewer? _guideScrollViewer;

        // Helper to dig into the ListBox and find its internal ScrollViewer
        private ScrollViewer? GetScrollViewer(System.Windows.DependencyObject depObj)
        {
            if (depObj is ScrollViewer sv) return sv;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private void PageUp_Click(object sender, RoutedEventArgs e)
        {
            _guideScrollViewer ??= GetScrollViewer(GuideItemsControl);
            if (_guideScrollViewer != null)
            {
                // A single click jumps 250 pixels. Holding the button repeats this every 50ms!
                double newOffset = _guideScrollViewer.VerticalOffset - 250;
                if (newOffset < 0) newOffset = 0;
                _guideScrollViewer.ScrollToVerticalOffset(newOffset);
            }
        }

        private void PageDown_Click(object sender, RoutedEventArgs e)
        {
            _guideScrollViewer ??= GetScrollViewer(GuideItemsControl);
            if (_guideScrollViewer != null)
            {
                double newOffset = _guideScrollViewer.VerticalOffset + 250;
                if (newOffset > _guideScrollViewer.ScrollableHeight) newOffset = _guideScrollViewer.ScrollableHeight;
                _guideScrollViewer.ScrollToVerticalOffset(newOffset);
            }
        }
        
        private void ChannelItemsControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 1. Tell WPF "I got this, don't scroll the left column."
            e.Handled = true;

            // 2. Package up the exact speed and direction of the scroll wheel movement
            var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };

            // 3. Fire it directly at the main Guide Scroller so they move together!
            TimelineScroller.RaiseEvent(eventArg);
        }

        public List<string> GetCollections()
        {
            var names = new List<string> { "All Channels" };
            if (_collections != null) names.AddRange(_collections.Select(c => c.name));
            return names;
        }

        public object GetMobileGuideData(string? collection, string? search)
        {
            IEnumerable<Channel> targetList = _masterChannelList;

            if (!string.IsNullOrWhiteSpace(collection) && collection != "All Channels")
            {
                var selected = _collections?.FirstOrDefault(c => c.name == collection);
                // --- FIX 1/3: Changed HasIdentifier to IsExactMatch for mobile API collections ---
                if (selected != null && selected.items != null) targetList = targetList.Where(ch => selected.items.Any(item => ch.IsExactMatch(item)));
            }

            // Keep HasIdentifier for general searching!
            if (!string.IsNullOrWhiteSpace(search)) targetList = targetList.Where(c => c.HasIdentifier(search));

            return targetList.Select(c => new
            {
                number = c.Number,
                name = c.Name,
                imageUrl = c.ImageUrl,
                currentShow = (c.CurrentAirings != null && c.CurrentAirings.Any()) ? c.CurrentAirings.First().DisplayTitle : "Unknown Programming"
            }).ToList();
        }
    
        private async void Page_Loaded(object sender, RoutedEventArgs e)
{
    ShowStatus("Scanning network for DVR servers...", "StatusConnecting");

    var discoveredServers = await _api.DiscoverDvrServersAsync();
    ServerComboBox.Items.Clear();

    // 1. Add dynamically discovered local servers
    foreach (var server in discoveredServers)
    {
        ServerComboBox.Items.Add(server);
    }
    
    // 2. Add saved manual servers that weren't broadcasted via Zeroconf
    foreach (var savedIp in _settings.SavedServers)
    {
        if (!discoveredServers.Any(s => s.BaseUrl.Equals(savedIp, StringComparison.OrdinalIgnoreCase)))
        {
            // Parse the saved URL string back into an IP and Port
            string parsedIp = savedIp;
            int parsedPort = 8089;
            
            try 
            {
                if (Uri.TryCreate(savedIp, UriKind.Absolute, out var uri))
                {
                    parsedIp = uri.Host;
                    parsedPort = uri.Port > 0 ? uri.Port : 8089;
                }
            }
            catch { }

            // Create a mock DvrServer object so the ComboBox displays it properly
            ServerComboBox.Items.Add(new DvrServer 
            { 
                Ip = parsedIp,
                Port = parsedPort,
                Name = "Saved Server" 
            });
        }
    }

    if (ServerComboBox.Items.Count > 0)
    {
        // Select the last used server, or default to the first in the list
        var match = ServerComboBox.Items.OfType<DvrServer>().FirstOrDefault(s => s.BaseUrl == _settings.LastServerAddress);
        if (match != null) ServerComboBox.SelectedItem = match;
        else ServerComboBox.SelectedIndex = 0; 
        
        ShowStatus("Ready.", "StatusSuccess");
        LoadData_Click(this, new RoutedEventArgs());
    }
    else
    {
        ShowStatus("No servers found. Please enter IP manually.", "StatusError");
    }
}
                     
        private void GenerateTimeHeaders(int durationHours)
        {
            var headers = new List<string>();
            DateTime now = DateTime.Now;
            DateTime start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute >= 30 ? 30 : 0, 0);

            int totalBlocks = (durationHours * 2) + 4;
            for (int i = 0; i < totalBlocks; i++) 
            {
                headers.Add(start.AddMinutes(i * 30).ToString("h:mm tt"));
            }
            TimeHeadersControl.ItemsSource = headers;
        }
        
        private void CollectionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CollectionComboBox.SelectedIndex == -1 || _masterChannelList == null) return;

            string selectedName = CollectionComboBox.SelectedItem.ToString()!;
            _settings.LastCollection = selectedName;
            SettingsManager.Save(_settings);

            // ApplyFilters handles ALL the logic now, so we just call it!
            ApplyFilters(); 
        }

        private async void LoadData_Click(object sender, RoutedEventArgs e)
        {
            ShowStatus("Connecting...", "StatusConnecting");
            
            string baseUrl = "";
            string rawInput = ServerComboBox.Text.Trim();

            if (ServerComboBox.SelectedItem is DvrServer selectedServer) 
    {
        baseUrl = selectedServer.BaseUrl;
    }
    else if (!string.IsNullOrWhiteSpace(rawInput))
    {
        // 1. Ensure it has a protocol so the URL parser doesn't break
        if (!rawInput.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
            !rawInput.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            rawInput = "http://" + rawInput;
        }

        // 2. Check for a colon explicitly AFTER the "http://"
        // If there isn't one, append the 8089 default. If there is, respect the user's custom port!
        int colonIndex = rawInput.IndexOf(':', rawInput.IndexOf("://") + 3);
        if (colonIndex == -1)
        {
            rawInput += ":8089";
        }
        
        baseUrl = rawInput;
    }
    else
    {
        StatusText.Text = "Please select or enter a server address.";
        StatusText.Foreground = System.Windows.Media.Brushes.Red;
        return;
    }

    // NEW: Automatically save the successfully used IP so it persists across restarts
_settings.LastServerAddress = baseUrl;
if (!_settings.SavedServers.Contains(baseUrl, StringComparer.OrdinalIgnoreCase))
{
    _settings.SavedServers.Add(baseUrl);
}
SettingsManager.Save(_settings);

    try
    {
        var rawChannels = await _api.GetChannelsAsync(baseUrl);
var cleanChannels = rawChannels
    .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Number)) 
    // --- NEW: Filter out virtual channels if setting is disabled ---
    .Where(c => _settings.EnableVirtualChannels || !(c.Id != null && c.Id.StartsWith("virtual", StringComparison.OrdinalIgnoreCase)))
    .OrderBy(c => double.TryParse(c.Number, out double num) ? num : 99999)
    .ToList();

                var stations = await _api.GetStationsAsync(baseUrl);
                var stationLogoDict = stations
                    .Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Logo))
                    .GroupBy(s => s.Id!)
                    .ToDictionary(g => g.Key, g => g.First().Logo!);

                foreach (var channel in cleanChannels)
                {
                    string targetId = !string.IsNullOrWhiteSpace(channel.StationId) ? channel.StationId : channel.CallSign;

                    if (string.IsNullOrWhiteSpace(channel.ImageUrl) && !string.IsNullOrWhiteSpace(targetId))
                    {
                        if (stationLogoDict.TryGetValue(targetId, out string? mappedLogo)) channel.ImageUrl = mappedLogo;
                    }

                    if (!string.IsNullOrWhiteSpace(channel.ImageUrl))
                    {
                        if (channel.ImageUrl.StartsWith("/")) channel.ImageUrl = $"{baseUrl}{channel.ImageUrl}";
                        else if (channel.ImageUrl.StartsWith("tmsimg://", StringComparison.OrdinalIgnoreCase))
                            channel.ImageUrl = channel.ImageUrl.Replace("tmsimg://", $"{baseUrl}/tmsimg/", StringComparison.OrdinalIgnoreCase);
                    }
                }

                try 
                {
                    GenerateTimeHeaders(_settings.GuideDurationHours); 
                    
                    DateTime now = DateTime.Now;
                    _currentGridStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute >= 30 ? 30 : 0, 0);

                    var guideBlocks = await _api.GetGuideAsync(baseUrl, _settings.GuideDurationHours);
                    var validGuideChannels = guideBlocks
                        .Where(g => !string.IsNullOrWhiteSpace(g.ChannelNumber) && g.Airings != null)
                        .ToList();
                    
                    var guideDict = validGuideChannels.GroupBy(g => g.ChannelNumber!).ToDictionary(g => g.Key, g => g.First());
                    
                    int mappedShows = 0;
                    foreach (var channel in cleanChannels)
                    {
                        if (channel.Number != null && guideDict.TryGetValue(channel.Number.Trim(), out var guideData))
                        {
                            // --- NEW: Inject the clean Favorite flag directly from the Guide payload! ---
                            channel.Favorite = guideData.IsFavorite;

                            if (string.IsNullOrWhiteSpace(channel.ImageUrl) && !string.IsNullOrWhiteSpace(guideData.ChannelImageUrl))
                            {
                                channel.ImageUrl = guideData.ChannelImageUrl;
                                if (channel.ImageUrl.StartsWith("/")) channel.ImageUrl = $"{baseUrl}{channel.ImageUrl}";
                                else if (channel.ImageUrl.StartsWith("tmsimg://", StringComparison.OrdinalIgnoreCase))
                                    channel.ImageUrl = channel.ImageUrl.Replace("tmsimg://", $"{baseUrl}/tmsimg/", StringComparison.OrdinalIgnoreCase);
                            }

                            var airings = guideData.Airings;
                            if (airings != null) 
                            {
                                airings = airings.Where(a => a.StartTime != DateTime.MinValue && a.StartTime.AddSeconds(a.Duration ?? 0) > _currentGridStart).ToList();

                                for (int i = 0; i < airings.Count; i++) 
                                {
                                    var a = airings[i];
                                    a.ChannelNumber = channel.Number;
                                    a.ChannelLogoUrl = channel.ImageUrl; 
                                    
                                    if (i == 0)
                                    {
                                        double minutesDifference = (a.StartTime - _currentGridStart).TotalMinutes;
                                        a.LeftOffset = minutesDifference * 8.0; 
                                    }
                                    else a.LeftOffset = 0;
                                }
                            }
                            channel.CurrentAirings = airings;
                            mappedShows += airings?.Count ?? 0;
                        }
                    }

                    var channelsWithData = cleanChannels.Where(c => c.CurrentAirings != null && c.CurrentAirings.Any()).ToList();

                    RenderChannelsOnly(channelsWithData);
                    ShowStatus($"Loaded {channelsWithData.Count} channels from {baseUrl}", "StatusSuccess", true);

                    try 
                    {
                        _collections = await _api.GetChannelCollectionsAsync(baseUrl);
                        CollectionComboBox.Items.Clear();
                        CollectionComboBox.Items.Add("All Channels"); 
                        CollectionComboBox.Items.Add("Favorites"); // --- NEW: Manually inject Favorites into the dropdown! ---
                        foreach (var collection in _collections) CollectionComboBox.Items.Add(collection.name);

                        if (!string.IsNullOrEmpty(_settings.LastCollection) && CollectionComboBox.Items.Contains(_settings.LastCollection))
                        {
                            CollectionComboBox.SelectedItem = _settings.LastCollection;
                        }
                        else CollectionComboBox.SelectedIndex = 0;
                        
                        CollectionLabel.Visibility = Visibility.Visible;
                        CollectionComboBox.Visibility = Visibility.Visible;
                        SearchLabel.Visibility = Visibility.Visible;
                        SearchBorder.Visibility = Visibility.Visible;
                    }
                    catch (Exception colEx) { Console.WriteLine($"Collections failed to load: {colEx.Message}"); }
                }
                catch (Exception guideEx)
                {
                    StatusText.Text = $"Guide failed to load: {guideEx.Message}";
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    RenderChannelsOnly(cleanChannels);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Critical Error: {ex.Message}";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void RenderChannelsOnly(List<Channel> channels)
        {
            _masterChannelList = channels;
            _currentFilteredList = new List<Channel>(_masterChannelList); 
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
            if (_masterChannelList == null) return;

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
                        // Channels DVR puts the source in the channel ID (e.g., "M3U-testadb-1234")
                        if (selectedCollection.excluded_sources != null && selectedCollection.excluded_sources.Count > 0)
                        {
                            if (selectedCollection.excluded_sources.Any(es => c.Id != null && c.Id.IndexOf(es, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                return false; // Immediately drop if it comes from an excluded source
                            }
                        }

                        // 2. STAGE 2: Explicit Items Override
                        // If the user manually added this specific channel to the list, include it
                        bool isExplicitItem = selectedCollection.items != null && selectedCollection.items.Any(item => c.IsExactMatch(item));
                        if (isExplicitItem) return true;

                        // 3. STAGE 3: Evaluate Smart Rules
                        bool hasRules = (selectedCollection.genres != null && selectedCollection.genres.Count > 0) ||
                                        (selectedCollection.categories != null && selectedCollection.categories.Count > 0) ||
                                        (selectedCollection.tags != null && selectedCollection.tags.Count > 0) ||
                                        (selectedCollection.keywords != null && selectedCollection.keywords.Count > 0);

                        // If it's not explicitly included and there are no smart rules, it fails
                        if (!hasRules) return false;

                        // Grab what is currently airing on this channel to evaluate against
                        var currentAiring = c.CurrentAirings?.FirstOrDefault(a => a.IsAiringNow);
                        if (currentAiring == null) return false; 

                        bool passesRules = true; // Assume true, and use AND logic to knock it out

                        // --- Rule: GENRES ---
                        if (passesRules && selectedCollection.genres != null && selectedCollection.genres.Count > 0)
                        {
                            bool genreMatch = (currentAiring.Genres != null && currentAiring.Genres.Intersect(selectedCollection.genres, StringComparer.OrdinalIgnoreCase).Any()) ||
                                              (currentAiring.Categories != null && currentAiring.Categories.Intersect(selectedCollection.genres, StringComparer.OrdinalIgnoreCase).Any());
                            passesRules = genreMatch;
                        }

                        // --- Rule: CATEGORIES ---
                        if (passesRules && selectedCollection.categories != null && selectedCollection.categories.Count > 0)
                        {
                            bool catMatch = (currentAiring.Categories != null && currentAiring.Categories.Intersect(selectedCollection.categories, StringComparer.OrdinalIgnoreCase).Any()) ||
                                            (currentAiring.Genres != null && currentAiring.Genres.Intersect(selectedCollection.categories, StringComparer.OrdinalIgnoreCase).Any());
                            passesRules = catMatch;
                        }

                        // --- Rule: TAGS ---
                        if (passesRules && selectedCollection.tags != null && selectedCollection.tags.Count > 0)
                        {
                            bool tagMatch = (currentAiring.Tags != null && currentAiring.Tags.Intersect(selectedCollection.tags, StringComparer.OrdinalIgnoreCase).Any()) ||
                                            (currentAiring.Categories != null && currentAiring.Categories.Intersect(selectedCollection.tags, StringComparer.OrdinalIgnoreCase).Any());
                            passesRules = tagMatch;
                        }

                        // --- Rule: KEYWORDS ---
                        if (passesRules && selectedCollection.keywords != null && selectedCollection.keywords.Count > 0)
                        {
                            // Smash all the text together and do a fast substring check
                            string searchBlock = $"{currentAiring.Title} {currentAiring.EpisodeTitle} {currentAiring.DisplaySummary}".ToLower();
                            bool keywordMatch = selectedCollection.keywords.Any(kw => searchBlock.Contains(kw.ToLower()));
                            passesRules = keywordMatch;
                        }

                        return passesRules;
                    });
                }
            }

// Keep HasIdentifier here so typing "Fox" still finds "Fox Sports"
            if (!string.IsNullOrWhiteSpace(query)) filtered = filtered.Where(c => c.HasIdentifier(query));

            // --- NEW: Sort the filtered list to bring the active tag to the top! ---
            if (_activeTag != "All Channels") 
            {
                filtered = filtered.OrderByDescending(c => DoesChannelMatchTag(c, _activeTag)).ThenBy(c => c.Number);
            }

            _currentFilteredList = filtered.ToList();
            
            _displayedChannels.Clear();
            foreach (var channel in _currentFilteredList) _displayedChannels.Add(channel);

            var guideScroll = GetScrollViewer(GuideItemsControl);
            if (guideScroll != null) guideScroll.ScrollToVerticalOffset(0);

            if (TimelineScroller != null) TimelineScroller.ScrollToHorizontalOffset(0);

            if (!SearchTextBox.IsKeyboardFocusWithin)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var request = new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First);
                    GuideItemsControl.MoveFocus(request);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        private void GuideItemsControl_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0) return;

            var channelScroll = GetScrollViewer(ChannelItemsControl);
            if (channelScroll != null)
            {
                channelScroll.ScrollToVerticalOffset(e.VerticalOffset);
            }

            if (!_settings.StickyGuideHeaders)
            {
                TimeHeadersControl.RenderTransform = new System.Windows.Media.TranslateTransform(0, -e.VerticalOffset);
                ChannelHeaderSpace.RenderTransform = new System.Windows.Media.TranslateTransform(0, -e.VerticalOffset);
            }
            else
            {
                TimeHeadersControl.RenderTransform = null;
                ChannelHeaderSpace.RenderTransform = null;
            }
        }

        private void ScrollLeft_Click(object sender, RoutedEventArgs e)
        {
            if (TimelineScroller != null) TimelineScroller.ScrollToHorizontalOffset(TimelineScroller.HorizontalOffset - 240);
        }
        
        private void TimelineScroller_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Shift)
            {
                TimelineScroller.ScrollToHorizontalOffset(TimelineScroller.HorizontalOffset - e.Delta);
            }
            else 
            {
                var guideScroll = GetScrollViewer(GuideItemsControl);
                if (guideScroll != null)
                {
                    guideScroll.ScrollToVerticalOffset(guideScroll.VerticalOffset - e.Delta);
                }
            }
            e.Handled = true;
        }

        private void ScrollRight_Click(object sender, RoutedEventArgs e)
        {
            if (TimelineScroller != null) TimelineScroller.ScrollToHorizontalOffset(TimelineScroller.HorizontalOffset + 240);
        }
        
        private void Page_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Back && e.OriginalSource is TextBox)
            {
                return; 
            }
            if (ModalOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == System.Windows.Input.Key.Escape || e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.BrowserBack)
                {
                    CloseModal_Click(null!, null!);
                    e.Handled = true;
                    return;
                }
            }
            
            if (e.Key == System.Windows.Input.Key.Escape || e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.BrowserBack)
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
                return;
            }
            
            if (e.Key == Key.BrowserHome)
            {
                NavigationService?.Navigate(new StartPage());
                e.Handled = true;
                return;
            }

            bool isArrowKey = e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right;
            bool isFocusedOnAiring = Keyboard.FocusedElement is Button fBtn && fBtn.Tag is Airing;
            bool isFocusedOnTag = Keyboard.FocusedElement is Button tBtn && TagPanel.Children.Contains(tBtn);
            bool isFocusedOnTopMenu = Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is TextBox || 
                                      (Keyboard.FocusedElement is Button topBtn && (topBtn.Content?.ToString()?.Contains("Home") == true || topBtn.Content?.ToString()?.Contains("Connect") == true || topBtn.Content?.ToString()?.Contains("🌙") == true));

            // Only trap arrows if focus is completely lost in the void
            if (isArrowKey && !isFocusedOnAiring && !isFocusedOnTag && !isFocusedOnTopMenu)
            {
                if (_displayedChannels.Count > 0 && _displayedChannels[0].CurrentAirings?.Count > 0)
                {
                    var targetBtn = FindButtonForAiring(GuideItemsControl, _displayedChannels[0].CurrentAirings![0]);
                    targetBtn?.Focus();
                    e.Handled = true; 
                    return;
                }
            }
            
            // --- NEW: Routing from Top Menu DOWN to Tags ---
            if (isFocusedOnTopMenu && e.Key == System.Windows.Input.Key.Down)
            {
                e.Handled = true;
                var activeTagBtn = TagPanel.Children.OfType<Button>().FirstOrDefault(b => b.Content.ToString() == _activeTag);
                if (activeTagBtn != null) activeTagBtn.Focus();
                else TagPanel.Children.OfType<Button>().FirstOrDefault()?.Focus();
                return;
            }

            // --- NEW: Navigating while focused on a Tag Pill ---
            if (isFocusedOnTag)
            {
                if (e.Key == System.Windows.Input.Key.Down)
                {
                    e.Handled = true;
                    // Jump down into the Guide (to the currently airing show)
                    if (_displayedChannels.Count > 0 && _displayedChannels[0].CurrentAirings?.Count > 0)
                    {
                        var targetAiring = _displayedChannels[0].CurrentAirings!.FirstOrDefault(a => 
                            a.StartTime <= _lastTimeFocus && 
                            a.StartTime.AddSeconds(a.Duration ?? 0) > _lastTimeFocus) ?? _displayedChannels[0].CurrentAirings![0];

                        var targetBtn = FindButtonForAiring(GuideItemsControl, targetAiring);
                        targetBtn?.Focus();
                    }
                    return;
                }
                else if (e.Key == System.Windows.Input.Key.Up)
                {
                    e.Handled = true;
                    // Jump up to the Dropdowns
                    if (CollectionComboBox.Visibility == Visibility.Visible) CollectionComboBox.Focus();
                    else ServerComboBox.Focus();
                    return;
                }
                else if (e.Key == System.Windows.Input.Key.Left || e.Key == System.Windows.Input.Key.Right)
                {
                    e.Handled = true;
                    var tagBtn = (Button)Keyboard.FocusedElement;
                    tagBtn.MoveFocus(new System.Windows.Input.TraversalRequest(e.Key == System.Windows.Input.Key.Left ? System.Windows.Input.FocusNavigationDirection.Left : System.Windows.Input.FocusNavigationDirection.Right));
                    
                    // Auto-scroll the horizontal Tag bar so the focused pill is always visible!
                    if (Keyboard.FocusedElement is Button newBtn) newBtn.BringIntoView();
                    return;
                }
            }

            // === THE SMART LEFT/RIGHT WARP LOGIC ===
            if (e.Key == System.Windows.Input.Key.Left || e.Key == System.Windows.Input.Key.Right)
            {
                if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Button btn && btn.Tag is Airing currentAiring)
                {
                    e.Handled = true; 

                    var channel = _displayedChannels.FirstOrDefault(c => c.Number == currentAiring.ChannelNumber);
                    if (channel != null)
                    {
                        var safeAirings = channel.CurrentAirings ?? new List<Airing>();
                        int currentIndex = safeAirings.IndexOf(currentAiring);
                        
                        int nextIndex = (e.Key == System.Windows.Input.Key.Right) ? currentIndex + 1 : currentIndex - 1;

                        if (nextIndex >= 0 && nextIndex < safeAirings.Count)
                        {
                            var targetAiring = safeAirings[nextIndex];
                            var targetBtn = FindButtonForAiring(GuideItemsControl, targetAiring);
                            targetBtn?.Focus();
                        }
                        // --- NEW: If they hit Left while already on the leftmost show, bounce to top! ---
                        else if (nextIndex < 0 && e.Key == System.Windows.Input.Key.Left)
                        {
                            if (_displayedChannels.Count > 0)
                            {
                                var firstAiring = _displayedChannels[0].CurrentAirings?.FirstOrDefault();
                                if (firstAiring != null)
                                {
                                    var targetBtn = FindButtonForAiring(GuideItemsControl, firstAiring);
                                    targetBtn?.Focus();
                                }
                            }
                            Dispatcher.BeginInvoke(new Action(() => 
                            {
                                var guideScroll = GetScrollViewer(GuideItemsControl);
                                if (guideScroll != null) guideScroll.ScrollToVerticalOffset(0);
                                if (TimelineScroller != null) TimelineScroller.ScrollToHorizontalOffset(0);
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }

                    // --- Double-Tap / Hold Warp Logic ---
                    if (e.Key == System.Windows.Input.Key.Left)
                    {
                        // Increase to 400ms for mobile network latency, and explicitly check for physical remote holds (IsRepeat).
                        // ONLY warp if they are in the "Live" column, protecting normal fast-scrolling in the timeline!
                        if (e.IsRepeat || (currentAiring.IsAiringNow && (DateTime.Now - _lastLeftKeyPressTime).TotalMilliseconds < 400))
                        {
                            if (_displayedChannels.Count > 0)
                            {
                                var firstAiring = _displayedChannels[0].CurrentAirings?.FirstOrDefault();
                                if (firstAiring != null)
                                {
                                    var targetBtn = FindButtonForAiring(GuideItemsControl, firstAiring);
                                    targetBtn?.Focus();
                                }
                            }
                            Dispatcher.BeginInvoke(new Action(() => 
                            {
                                var guideScroll = GetScrollViewer(GuideItemsControl);
                                if (guideScroll != null) guideScroll.ScrollToVerticalOffset(0);
                                if (TimelineScroller != null) TimelineScroller.ScrollToHorizontalOffset(0);
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                        _lastLeftKeyPressTime = DateTime.Now;
                    }
                    return;
                }
            }

            if (e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down)
            {
                if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Button btn && btn.Tag is Airing currentAiring)
                {
                    e.Handled = true; 
                    
                    int currentChannelIndex = _displayedChannels.IndexOf(_displayedChannels.First(c => c.Number == currentAiring.ChannelNumber));
                    int nextIndex = e.Key == System.Windows.Input.Key.Down ? currentChannelIndex + 1 : currentChannelIndex - 1;
                    
                    if (nextIndex >= 0 && nextIndex < _displayedChannels.Count)
                    {
                        var nextChannel = _displayedChannels[nextIndex];
                        var safeAirings = nextChannel.CurrentAirings ?? new List<Airing>();
                        
                        var targetAiring = safeAirings.FirstOrDefault(a => 
                            a.StartTime <= _lastTimeFocus && 
                            a.StartTime.AddSeconds(a.Duration ?? 0) > _lastTimeFocus);
                            
                        if (targetAiring == null) targetAiring = safeAirings.FirstOrDefault();
                            
                        if (targetAiring != null)
                        {
                            var targetBtn = FindButtonForAiring(GuideItemsControl, targetAiring);
                            targetBtn?.Focus();
                        }
                    }
                    else if (nextIndex < 0)
                    {
                        Dispatcher.BeginInvoke(new Action(() => 
                        {
                            var guideScroll = GetScrollViewer(GuideItemsControl);
                            if (guideScroll != null) guideScroll.ScrollToVerticalOffset(0);
                            
                            // --- NEW: Jump UP to the active Tag Pill! ---
                            var activeTagBtn = TagPanel.Children.OfType<Button>().FirstOrDefault(b => b.Content.ToString() == _activeTag);
                            if (activeTagBtn != null) activeTagBtn.Focus();
                            else TagPanel.Children.OfType<Button>().FirstOrDefault()?.Focus();
                            
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                    return; 
                }
            }

            if (e.Key == System.Windows.Input.Key.PageUp || e.Key == System.Windows.Input.Key.MediaPreviousTrack)
            {
                for (int i = 0; i < 4; i++)
                    (System.Windows.Input.Keyboard.FocusedElement as UIElement)?.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Up));
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.PageDown || e.Key == System.Windows.Input.Key.MediaNextTrack)
            {
                for (int i = 0; i < 4; i++)
                    (System.Windows.Input.Keyboard.FocusedElement as UIElement)?.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Down));
                e.Handled = true;
            }
        }
		
		// --- NEW: TAG CHIP LOGIC ---
        private void TagChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Content is string clickedTag)
            {
                _activeTag = clickedTag;
                
                // Show a quick status message so the user knows it worked
                if (_activeTag == "All Channels") ShowStatus("Restored default channel sorting.", "StatusSuccess", true);
                else ShowStatus($"Moved '{_activeTag}' programming to the top.", "StatusSuccess", true);

                ApplyFilters(); // Re-run the master filter!
            }
        }

        private bool DoesChannelMatchTag(Channel channel, string tag)
        {
            var currentAiring = channel.CurrentAirings?.FirstOrDefault(a => a.IsAiringNow);
            if (currentAiring == null) return false;

            if (currentAiring.Categories != null && currentAiring.Categories.Contains(tag)) return true;
            if (currentAiring.Genres != null && currentAiring.Genres.Contains(tag)) return true;

            return false;
        }

        private void AiringBlock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is Airing airing)
            {
                _lastFocusedAiringButton = btn;
                _selectedAiring = airing;
                
                ModalTitle.Text = airing.DisplayTitle;
                ModalSummary.Text = airing.DisplaySummary;
                ModalMetaData.Text = airing.DisplayMetaData;
                ModalCategoryBar.Background = new System.Windows.Media.BrushConverter().ConvertFromString(airing.CategoryColor) as System.Windows.Media.Brush;
                
                if (airing.StartTime != DateTime.MinValue && airing.Duration.HasValue)
                {
                    DateTime endTime = airing.StartTime.AddSeconds(airing.Duration.Value);
                    ModalTime.Text = $"{airing.StartTime:h:mm tt} - {endTime:h:mm tt}";
                }
                else ModalTime.Text = "Time unknown";

                string? imgToUse = !string.IsNullOrWhiteSpace(airing.ImageUrl) ? airing.ImageUrl : airing.ChannelLogoUrl;
                
                if (!string.IsNullOrWhiteSpace(imgToUse))
                {
                    // FIX: Added UriKind.RelativeOrAbsolute to prevent crashes on malformed image links
                    ModalImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(imgToUse, UriKind.RelativeOrAbsolute));
                    ModalImage.Visibility = Visibility.Visible;
                }
                else ModalImage.Visibility = Visibility.Collapsed;

                WatchButton.Visibility = airing.IsAiringNow ? Visibility.Visible : Visibility.Collapsed;
                ModalOverlay.Visibility = Visibility.Visible;
                
                if (airing.IsAiringNow) WatchButton.Focus();
                else CloseModalButton.Focus();
            }
        }
        
        private void AiringBlock_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true; 
        }

        private System.Windows.Controls.Button? FindButtonForAiring(DependencyObject parent, Airing targetAiring)
        {
            Queue<DependencyObject> queue = new Queue<DependencyObject>();
            queue.Enqueue(parent);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is System.Windows.Controls.Button b && b.Tag == targetAiring) return b;

                int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < childCount; i++) queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(current, i));
            }
            return null;
        }

        private void AiringBlock_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is Airing airing)
            {
                bool isHorizontalMove = _lastFocusedAiringButton == null || 
                    ((Airing)_lastFocusedAiringButton.Tag).ChannelNumber == airing.ChannelNumber;

                if (isHorizontalMove)
                {
                    if (airing.IsAiringNow) 
                    {
                        _lastTimeFocus = DateTime.Now;
                    }
                    else 
                    {
                        _lastTimeFocus = airing.StartTime.AddSeconds(1);
                    }
                }
                try
                {
                    if (TimelineScroller != null)
                    {
                        var hTransform = btn.TransformToAncestor(TimelineScroller);
                        var hBounds = hTransform.TransformBounds(new Rect(0, 0, btn.ActualWidth, btn.ActualHeight));

                        if (hBounds.Left < 0)
                            TimelineScroller.ScrollToHorizontalOffset(TimelineScroller.HorizontalOffset + hBounds.Left - 20);
                        else if (hBounds.Right > TimelineScroller.ViewportWidth)
                        {
                            if (btn.ActualWidth > TimelineScroller.ViewportWidth)
                                TimelineScroller.ScrollToHorizontalOffset(TimelineScroller.HorizontalOffset + hBounds.Left - 20);
                            else
                                TimelineScroller.ScrollToHorizontalOffset(TimelineScroller.HorizontalOffset + (hBounds.Right - TimelineScroller.ViewportWidth) + 20);
                        }
                    }

                    var guideScroll = GetScrollViewer(GuideItemsControl);
                    if (guideScroll != null)
                    {
                        var vTransform = btn.TransformToAncestor(guideScroll);
                        var vBounds = vTransform.TransformBounds(new Rect(0, 0, btn.ActualWidth, btn.ActualHeight));

                        var firstChannel = _displayedChannels.FirstOrDefault();
                        bool isTopRow = (firstChannel != null && airing.ChannelNumber == firstChannel.Number);

                        if (isTopRow)
                        {
                            Dispatcher.BeginInvoke(new Action(() => 
                            {
                                guideScroll.ScrollToVerticalOffset(0);
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        }
                        else
                        {
                            if (vBounds.Top < 0)
                                guideScroll.ScrollToVerticalOffset(guideScroll.VerticalOffset + vBounds.Top - 20);
                            else if (vBounds.Bottom > guideScroll.ViewportHeight)
                                guideScroll.ScrollToVerticalOffset(guideScroll.VerticalOffset + (vBounds.Bottom - guideScroll.ViewportHeight) + 20);
                        }
                    }
                }
                catch { } 
                
                _lastFocusedAiringButton = btn;
            }
        }

        private void CloseModal_Click(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            _selectedAiring = null;
            _lastFocusedAiringButton?.Focus();
        }

       private void WatchButton_Click(object sender, RoutedEventArgs e)
        {
            string baseUrl = "";
            if (ServerComboBox.SelectedItem is DvrServer selectedServer) baseUrl = selectedServer.BaseUrl;
            else
            {
                string rawInput = ServerComboBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(rawInput))
                {
                    if (!rawInput.Contains(":")) rawInput += ":8089";
                    if (!rawInput.StartsWith("http")) rawInput = "http://" + rawInput;
                    baseUrl = rawInput;
                }
            }

            if (_selectedAiring != null && !string.IsNullOrWhiteSpace(baseUrl))
            {
                try
                {
                    ModalOverlay.Visibility = Visibility.Collapsed;

                    int channelIndex = _masterChannelList.FindIndex(c => c.Number == _selectedAiring.ChannelNumber);
                    if (channelIndex == -1) channelIndex = 0;

                    var mainWindow = (MainWindow)Application.Current.MainWindow;

                    if (mainWindow.ActivePlayerWindow != null) mainWindow.ActivePlayerWindow.Close();

                    mainWindow.ActivePlayerWindow = new PlayerWindow(baseUrl, _masterChannelList, channelIndex);
                    
                    mainWindow.ActivePlayerWindow.Closed += (s, args) => 
                    {
                        mainWindow.ActivePlayerWindow = null; 
                        if (_settings.MinimizeOnPlay) mainWindow.WindowState = WindowState.Normal;
                        mainWindow.Show(); 
                        Application.Current.Dispatcher.InvokeAsync(() => _lastFocusedAiringButton?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                    }; 
                    
                    if (_settings.MinimizeOnPlay) mainWindow.WindowState = WindowState.Minimized;
                    else mainWindow.Hide(); 
                    
                    mainWindow.ActivePlayerWindow.Show();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Player failed to launch:\n{ex.Message}", "VLC Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        
        public void RemotePlayChannel(string channelNumber)
        {
            string baseUrl = "";
            if (ServerComboBox.SelectedItem is DvrServer selectedServer) baseUrl = selectedServer.BaseUrl;
            else
            {
                string rawInput = ServerComboBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(rawInput))
                {
                    if (!rawInput.Contains(":")) rawInput += ":8089";
                    if (!rawInput.StartsWith("http")) rawInput = "http://" + rawInput;
                    baseUrl = rawInput;
                }
            }

            if (string.IsNullOrWhiteSpace(baseUrl)) return;

            int channelIndex = _masterChannelList.FindIndex(c => c.Number == channelNumber);
            if (channelIndex == -1) return; 

            var mainWindow = (MainWindow)Application.Current.MainWindow;

            if (mainWindow.ActivePlayerWindow != null) mainWindow.ActivePlayerWindow.Close();

            mainWindow.ActivePlayerWindow = new PlayerWindow(baseUrl, _masterChannelList, channelIndex);
            mainWindow.ActivePlayerWindow.Closed += (s, args) => mainWindow.ActivePlayerWindow = null; 
            mainWindow.ActivePlayerWindow.Show();
        }
        
        private void ShowStatus(string message, string colorKey, bool autoHide = false)
        {
            StatusText.Text = message;
            StatusText.Foreground = FindResource(colorKey) as System.Windows.Media.Brush;
            
            StatusText.BeginAnimation(UIElement.OpacityProperty, null);
            StatusText.Opacity = 1.0;

            if (autoHide)
            {
                var fadeOut = new DoubleAnimation
                {
                    From = 1.0, To = 0.0,
                    BeginTime = TimeSpan.FromSeconds(4), 
                    Duration = new Duration(TimeSpan.FromSeconds(1.5)) 
                };
                StatusText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }
        
        private void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            DateTime newGridStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute >= 30 ? 30 : 0, 0);

            if (newGridStart > _currentGridStart && _masterChannelList.Any())
            {
                LoadData_Click(this, new RoutedEventArgs());
            }
        }

        private void ApplyTheme(bool isLight)
        {
            string themeName = isLight ? "LightTheme.xaml" : "DarkTheme.xaml";
            var newDictionary = new ResourceDictionary { Source = new Uri($"Themes/{themeName}", UriKind.Relative) };
            Application.Current.Resources.MergedDictionaries.Clear();
            Application.Current.Resources.MergedDictionaries.Add(newDictionary);
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            _settings.IsLightTheme = !_settings.IsLightTheme;
            ApplyTheme(_settings.IsLightTheme);
            SettingsManager.Save(_settings);
        }
		
		private void ChannelContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            // WPF Trick: Get the exact Channel Border that the user right-clicked
            if (sender is ContextMenu menu && menu.PlacementTarget is FrameworkElement border && border.DataContext is Channel channel)
            {
                var transcodeItem = menu.Items[0] as MenuItem;
                var remuxItem = menu.Items[1] as MenuItem; // Assumes your Remux option is the second item in the XAML
                
                if (_settings.ForcedFfmpegChannels == null) _settings.ForcedFfmpegChannels = new List<string>();
                if (_settings.ForcedFfmpegRemuxChannels == null) _settings.ForcedFfmpegRemuxChannels = new List<string>();

                transcodeItem!.IsChecked = _settings.ForcedFfmpegChannels.Contains(channel.Number!);
                remuxItem!.IsChecked = _settings.ForcedFfmpegRemuxChannels.Contains(channel.Number!);
            }
        }

        private void ForceFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu menu && menu.PlacementTarget is FrameworkElement border && border.DataContext is Channel channel)
            {
                if (_settings.ForcedFfmpegChannels == null) 
                    _settings.ForcedFfmpegChannels = new List<string>();

                // Toggle the setting
                if (_settings.ForcedFfmpegChannels.Contains(channel.Number!))
                {
                    _settings.ForcedFfmpegChannels.Remove(channel.Number!);
                    StatusText.Text = $"Removed FFmpeg Transcode for CH {channel.Number}";
                }
                else
                {
                    _settings.ForcedFfmpegChannels.Add(channel.Number!);
                    StatusText.Text = $"Forced FFmpeg Transcode for CH {channel.Number}";
                }

                // Save to disk immediately
                SettingsManager.Save(_settings);
                
                // Optional: Fade out the status text after a few seconds
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation { From = 1.0, To = 0.0, Duration = new Duration(TimeSpan.FromSeconds(1)), BeginTime = TimeSpan.FromSeconds(3) };
                StatusText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }
		
		private void ForceFfmpegRemux_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu menu && menu.PlacementTarget is FrameworkElement border && border.DataContext is Channel channel)
            {
                if (_settings.ForcedFfmpegRemuxChannels == null) 
                    _settings.ForcedFfmpegRemuxChannels = new List<string>();

                // Toggle the setting
                if (_settings.ForcedFfmpegRemuxChannels.Contains(channel.Number!))
                {
                    _settings.ForcedFfmpegRemuxChannels.Remove(channel.Number!);
                    StatusText.Text = $"Removed FFmpeg Remux for CH {channel.Number}";
                }
                else
                {
                    _settings.ForcedFfmpegRemuxChannels.Add(channel.Number!);
                    StatusText.Text = $"Forced FFmpeg Remux for CH {channel.Number}";
                }

                SettingsManager.Save(_settings);
                
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation { From = 1.0, To = 0.0, Duration = new Duration(TimeSpan.FromSeconds(1)), BeginTime = TimeSpan.FromSeconds(3) };
                StatusText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }
        
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
    }
}