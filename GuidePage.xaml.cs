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
        
        public GuidePage()
        {
            InitializeComponent();
            
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

        // --- NEW: Helper to extract the internal ScrollViewer out of the virtualizing ListBoxes ---
        private ScrollViewer? GetScrollViewer(DependencyObject depObj)
        {
            if (depObj == null) return null;
            if (depObj is ScrollViewer scroll) return scroll;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                var result = GetScrollViewer(child);
                if (result != null) return result;
            }
            return null;
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

            if (discoveredServers.Any())
            {
                foreach (var server in discoveredServers)
                {
                    ServerComboBox.Items.Add(server);
                }
                ServerComboBox.SelectedIndex = 0; 
                
                ShowStatus($"Found {discoveredServers.Count} server(s).", "StatusSuccess");
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

            if (selectedName == "All Channels") _currentFilteredList = new List<Channel>(_masterChannelList);
            else
            {
                var selectedCollection = _collections.FirstOrDefault(c => c.name == selectedName);
                if (selectedCollection != null && selectedCollection.items != null)
                {
                    // --- FIX 2/3: Changed HasIdentifier to IsExactMatch when selecting a dropdown collection ---
                    _currentFilteredList = _masterChannelList.Where(channel => selectedCollection.items.Any(item => channel.IsExactMatch(item))).ToList();
                }
            }

            ApplyFilters(); 
        }

        private async void LoadData_Click(object sender, RoutedEventArgs e)
        {
            ShowStatus("Connecting...", "StatusConnecting");
            
            string baseUrl = "";
            string rawInput = ServerComboBox.Text.Trim();

            if (ServerComboBox.SelectedItem is DvrServer selectedServer) baseUrl = selectedServer.BaseUrl;
            else if (!string.IsNullOrWhiteSpace(rawInput))
            {
                if (!rawInput.Contains(":")) rawInput += ":8089";
                if (!rawInput.StartsWith("http")) rawInput = "http://" + rawInput;
                baseUrl = rawInput;
            }
            else
            {
                StatusText.Text = "Please select or enter a server address.";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            try
            {
                var rawChannels = await _api.GetChannelsAsync(baseUrl);
                var cleanChannels = rawChannels
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Number)
                             && (string.IsNullOrEmpty(c.Id) || !c.Id.StartsWith("virtual", StringComparison.OrdinalIgnoreCase))) 
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

            if (selectedCollectionName != "All Channels")
            {
                var selectedCollection = _collections.FirstOrDefault(c => c.name == selectedCollectionName);
                if (selectedCollection != null && selectedCollection.items != null)
                {
                    // --- FIX 3/3: Changed HasIdentifier to IsExactMatch when refreshing the grid filters ---
                    filtered = filtered.Where(c => selectedCollection.items.Any(item => c.IsExactMatch(item)));
                }
            }

            // Keep HasIdentifier here so typing "Fox" still finds "Fox Sports"
            if (!string.IsNullOrWhiteSpace(query)) filtered = filtered.Where(c => c.HasIdentifier(query));

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

            if (isArrowKey && !isFocusedOnAiring)
            {
                if (_displayedChannels.Count > 0 && _displayedChannels[0].CurrentAirings?.Count > 0)
                {
                    var targetBtn = FindButtonForAiring(GuideItemsControl, _displayedChannels[0].CurrentAirings![0]);
                    targetBtn?.Focus();
                    e.Handled = true; 
                    return;
                }
            }

            if (e.Key == System.Windows.Input.Key.Left || e.Key == System.Windows.Input.Key.Right)
            {
                if (e.Key == System.Windows.Input.Key.Left)
                {
                    if ((DateTime.Now - _lastLeftKeyPressTime).TotalMilliseconds < 150)
                    {
                        e.Handled = true;
                        
                        if (_displayedChannels.Count > 0)
                        {
                            var safeAirings = _displayedChannels[0].CurrentAirings ?? new List<Airing>();
                            var firstAiring = safeAirings.FirstOrDefault();
                            
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

                        return;
                    }
                    _lastLeftKeyPressTime = DateTime.Now;
                }

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
                    ModalImage.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(imgToUse));
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
                        mainWindow.Show(); 
                        Application.Current.Dispatcher.InvokeAsync(() => _lastFocusedAiringButton?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                    }; 
                    
                    mainWindow.Hide(); 
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