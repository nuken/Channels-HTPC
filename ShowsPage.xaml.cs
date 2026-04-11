using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace FeralCode
{
    public partial class ShowsPage : Page
    {
        private List<Episode> _allEpisodes = new List<Episode>();
        private List<TvShow> _allShows = new List<TvShow>(); 
        private string _baseUrl = "";
        private UserSettings _settings;
        private Button? _lastFocusedShowButton; 

        public ShowsPage()
        {
            InitializeComponent();
            _settings = SettingsManager.Load(); 
            this.Loaded += Page_Loaded;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
{
    _settings = SettingsManager.Load();
    _baseUrl = _settings.LastServerAddress;

    // Try saved IP first. If blank, fallback to network discovery.
    if (string.IsNullOrWhiteSpace(_baseUrl))
    {
        LoadingText.Text = "Searching for DVR Server...";
        var api = new ChannelsApi();
        var servers = await api.DiscoverDvrServersAsync();
        
        if (servers.Any())
        {
            _baseUrl = servers.First().BaseUrl;
            _settings.LastServerAddress = _baseUrl;
            SettingsManager.Save(_settings); // Auto-save the discovered IP
        }
        else
        {
            LoadingText.Text = "⚠️ No DVR Server found on network. Please enter IP in Settings.";
            return;
        }
    }

    await LoadShowsAsync();
}

        private async Task LoadShowsAsync()
        {
            var api = new ChannelsApi();
            _allShows = await api.GetShowsAsync(_baseUrl);
            _allEpisodes = await api.GetEpisodesAsync(_baseUrl);

            if (_allShows.Count == 0)
            {
                LoadingText.Text = "No TV Shows found on the DVR.";
                return;
            }

            var genres = _allShows.Where(s => s.Genres != null)
                                  .SelectMany(s => s.Genres!)
                                  .Distinct()
                                  .OrderBy(g => g).ToList();
            
            GenreBox.Items.Clear();
            GenreBox.Items.Add(new ComboBoxItem { Content = "All Genres", IsSelected = true });
            foreach (var g in genres) GenreBox.Items.Add(new ComboBoxItem { Content = g });

            ApplyFilters(); 
            LoadingText.Visibility = Visibility.Collapsed;
        }
        
        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_allShows.Count > 0) ApplyFilters();
        }
        
        private void ToggleFilters_Click(object sender, RoutedEventArgs e)
        {
            if (FilterBar.Visibility == Visibility.Visible)
            {
                FilterBar.Visibility = Visibility.Collapsed;
            }
            else
            {
                FilterBar.Visibility = Visibility.Visible;
                SearchBox.Focus(); 
            }
        }

        private void ApplyAndClose_Click(object sender, RoutedEventArgs e)
        {
            FilterBar.Visibility = Visibility.Collapsed;
            
            if (ShowsWrapPanel.Children.Count > 0)
                ((UIElement)ShowsWrapPanel.Children[0]).Focus();
        }

        private void ApplyFilters()
        {
            if (_allShows == null || _allShows.Count == 0) return;

            var filtered = _allShows.AsEnumerable();

            string search = SearchBox.Text.ToLower().Trim();
            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(s => s.Name.ToLower().Contains(search));

            if (GenreBox.SelectedIndex > 0 && GenreBox.SelectedItem is ComboBoxItem gi)
            {
                string genre = gi.Content.ToString() ?? "";
                filtered = filtered.Where(s => s.Genres != null && s.Genres.Contains(genre));
            }

            if (WatchedBox.SelectedIndex == 1) filtered = filtered.Where(s => !s.IsWatched); 
            if (WatchedBox.SelectedIndex == 2) filtered = filtered.Where(s => s.IsWatched);  

            if (SortBox.SelectedIndex == 0) 
                filtered = filtered.OrderBy(s => s.Name);
            else if (SortBox.SelectedIndex == 1) 
                filtered = filtered.OrderByDescending(s => s.CreatedAt);
            else if (SortBox.SelectedIndex == 2) 
                filtered = filtered.OrderByDescending(s => s.ReleaseYear);
            else if (SortBox.SelectedIndex == 3) 
            {
                // --- NEW: Sort shows by looking at their episodes and finding the highest timestamp! ---
                filtered = filtered.OrderByDescending(s => 
                    _allEpisodes.Where(e => e.ShowId == s.Id)
                                .Max(e => (long?)e.CreatedAt) ?? 0);
            }

            RenderShowsGrid(filtered.ToList());
        }
        
        private void RenderShowsGrid(List<TvShow> showsToRender)
        {
            ShowsWrapPanel.Children.Clear();

            foreach (var show in showsToRender)
            {
                var btn = new Button { Style = (Style)FindResource("PosterButton"), Tag = show };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(260) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                var img = new Image { Stretch = Stretch.UniformToFill };
                try { if (!string.IsNullOrWhiteSpace(show.ImageUrl)) img.Source = LoadOptimizedImage(show.ImageUrl, 300); } catch { }

                var textBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Padding = new Thickness(10) };
                var textPanel = new StackPanel();
                
                textPanel.Children.Add(new TextBlock { Text = show.Name, FontWeight = FontWeights.Bold, FontSize = 16, TextWrapping = TextWrapping.Wrap, MaxHeight = 45, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White });
                textPanel.Children.Add(new TextBlock { Text = $"{show.EpisodeCount} Episodes", Foreground = Brushes.LightGray, FontSize = 12, Margin = new Thickness(0, 5, 0, 0) });
                
                textBorder.Child = textPanel;
                Grid.SetRow(img, 0);
                Grid.SetRow(textBorder, 1);
                grid.Children.Add(img);
                grid.Children.Add(textBorder);

                btn.Content = grid;
                btn.Click += Show_Click;
                ShowsWrapPanel.Children.Add(btn);
            }

            if (ShowsWrapPanel.Children.Count > 0 && !SearchBox.IsKeyboardFocusWithin)
            {
                ((UIElement)ShowsWrapPanel.Children[0]).Focus();
            }
        }

        private void Show_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TvShow show)
            {
                _lastFocusedShowButton = btn;
                OpenEpisodesView(show);
            }
        }

        private System.Windows.Controls.Button? _selectedSeasonButton;

        private void OpenEpisodesView(TvShow show)
        {
            ShowsScrollViewer.Visibility = Visibility.Collapsed;
            EpisodesView.Visibility = Visibility.Visible;
            ToggleFiltersButton.Visibility = Visibility.Collapsed;
            FilterBar.Visibility = Visibility.Collapsed;
            
            PageTitle.Text = $"🍿 {show.Name.ToUpper()}";
            BackButton.Content = "🔙 Back to Shows";

            SelectedShowTitle.Text = show.Name;
            SelectedShowCount.Text = $"{show.EpisodeCount} Recorded Episodes";
            SelectedShowImage.Source = null; 
            if (!string.IsNullOrWhiteSpace(show.ImageUrl))
            {
                LoadModalImageAsync(SelectedShowImage, show.ImageUrl, 400); 
            }

            SeasonsStackPanel.Children.Clear();
            EpisodesStackPanel.Children.Clear();
            _selectedSeasonButton = null;

            var showEpisodes = _allEpisodes.Where(ep => ep.ShowId == show.Id).ToList();

            if (_settings.ShowExtendedMetadata && showEpisodes.Any())
            {
                var firstEp = showEpisodes.First();
                ModalExtendedData.Visibility = Visibility.Visible;
                
                ModalRating.Text = !string.IsNullOrWhiteSpace(firstEp.ContentRating) ? firstEp.ContentRating : "NR";
                RatingBorder.Visibility = string.IsNullOrWhiteSpace(firstEp.ContentRating) ? Visibility.Collapsed : Visibility.Visible;

                ModalTags.Text = (firstEp.Tags != null && firstEp.Tags.Any()) ? string.Join(" • ", firstEp.Tags) : "";
                ModalCast.Text = (firstEp.Cast != null && firstEp.Cast.Any()) ? $"Starring: {string.Join(", ", firstEp.Cast.Take(6))}" : "";
            }
            else
            {
                ModalExtendedData.Visibility = Visibility.Collapsed;
            }
            
            var seasons = showEpisodes.GroupBy(ep => ep.SeasonNumber).OrderByDescending(g => g.Key).ToList();

            if (!seasons.Any()) return;

            foreach (var seasonGroup in seasons)
            {
                int seasonNum = seasonGroup.Key;
                string seasonText = seasonNum == 0 ? "Specials" : $"Season {seasonNum}";

                var btn = new Button
                {
                    Content = new TextBlock { Text = seasonText, FontSize = 16, FontWeight = FontWeights.Bold },
                    Style = (Style)FindResource("SeasonButton"),
                    Tag = seasonGroup.ToList() 
                };

                btn.Click += SeasonButton_Click;
                SeasonsStackPanel.Children.Add(btn);
            }

            if (SeasonsStackPanel.Children.Count > 0)
            {
                var latestSeasonBtn = (Button)SeasonsStackPanel.Children[0];
                SelectSeason(latestSeasonBtn);
                
                if (EpisodesStackPanel.Children.Count > 0)
                    ((UIElement)EpisodesStackPanel.Children[0]).Focus();
            }
        }

        private void SeasonButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                SelectSeason(btn);
            }
        }

        private void SelectSeason(Button btn)
        {
            if (_selectedSeasonButton != null)
            {
                _selectedSeasonButton.Background = Brushes.Transparent;
                _selectedSeasonButton.SetResourceReference(Button.ForegroundProperty, "TextSecondary");
            }

            _selectedSeasonButton = btn;
            _selectedSeasonButton.SetResourceReference(Button.BackgroundProperty, "CardHoverBackground");
            _selectedSeasonButton.SetResourceReference(Button.ForegroundProperty, "TextPrimary");

            if (btn.Tag is List<Episode> seasonEpisodes)
            {
                RenderEpisodesList(seasonEpisodes);
            }
        }

        private void RenderEpisodesList(List<Episode> episodes)
        {
            EpisodesStackPanel.Children.Clear();

            var sortedEpisodes = episodes.OrderByDescending(ep => ep.EpisodeNumber).ToList();

            foreach (var ep in sortedEpisodes)
            {
                var btn = new Button
                {
                    Style = (Style)FindResource("EpisodeButton"),
                    Tag = ep
                };

                var sp = new StackPanel();
                
                var seasonText = new TextBlock { Text = $"Season {ep.SeasonNumber} • Episode {ep.EpisodeNumber}", FontWeight = FontWeights.Bold, FontSize = 14 };
                seasonText.SetResourceReference(TextBlock.ForegroundProperty, "LiveBorder");
                
                var titleText = new TextBlock { Text = ep.EpisodeTitle, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
                titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

                sp.Children.Add(seasonText);
                sp.Children.Add(titleText);

                btn.Content = sp;
                btn.Click += Episode_Click;
                EpisodesStackPanel.Children.Add(btn);
            }
        }
        
        private void Episode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Episode ep)
            {
                string streamUrl = $"{_baseUrl.TrimEnd('/')}/dvr/files/{ep.Id}/hls/master.m3u8?vcodec=copy&acodec=copy";
                string displayTitle = $"{ep.Title} - S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2} - {ep.EpisodeTitle}";

                if (Application.Current.MainWindow is MainWindow mainWin)
                {
                    if (mainWin.ActivePlayerWindow != null) mainWin.ActivePlayerWindow.Close();

                    mainWin.ActivePlayerWindow = new PlayerWindow(streamUrl, displayTitle, ep.ImageUrl, ep.Commercials);
                    
                    mainWin.ActivePlayerWindow.Closed += (s, args) => 
                    {
                        mainWin.ActivePlayerWindow = null;
                        if (_settings.MinimizeOnPlay) mainWin.WindowState = WindowState.Normal;
                        mainWin.Show(); 
                        Application.Current.Dispatcher.InvokeAsync(() => btn.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                    };
                    
                    if (_settings.MinimizeOnPlay) mainWin.WindowState = WindowState.Minimized;
                    else mainWin.Hide(); 
                    
                    mainWin.ActivePlayerWindow.Show();
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilterBar.Visibility == Visibility.Visible)
            {
                ApplyAndClose_Click(null!, null!);
                return;
            }

            if (EpisodesView.Visibility == Visibility.Visible)
            {
                EpisodesView.Visibility = Visibility.Collapsed;
                FilterBar.Visibility = Visibility.Collapsed;
                ShowsScrollViewer.Visibility = Visibility.Visible;
                PageTitle.Text = "🍿 TV SHOWS";
                BackButton.Content = "🏠 Home";
                ToggleFiltersButton.Visibility = Visibility.Visible;
                
                if (_lastFocusedShowButton != null && _lastFocusedShowButton.IsVisible)
                {
                    _lastFocusedShowButton.Focus();
                }
                else if (ShowsWrapPanel.Children.Count > 0)
                {
                    ((UIElement)ShowsWrapPanel.Children[0]).Focus();
                }
            }
            else
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

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Back && e.OriginalSource is TextBox)
            {
                return; 
            }

            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                e.Handled = true; 
                BackButton_Click(null!, null!);
                return;
            }
            if (e.Key == Key.BrowserHome)
            {
                NavigationService?.Navigate(new StartPage());
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Apps || e.Key == Key.System)
            {
                ToggleFilters_Click(null!, null!);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right)
            {
                if (Keyboard.FocusedElement is Button btn && (btn.Tag is TvShow || btn.Tag is Episode || btn.Tag is List<Episode>))
                {
                    FocusNavigationDirection dir = FocusNavigationDirection.Next;
                    if (e.Key == Key.Up) dir = FocusNavigationDirection.Up;
                    else if (e.Key == Key.Down) dir = FocusNavigationDirection.Down;
                    else if (e.Key == Key.Left) dir = FocusNavigationDirection.Left;
                    else if (e.Key == Key.Right) dir = FocusNavigationDirection.Right;

                    btn.MoveFocus(new TraversalRequest(dir));
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.PageUp || e.Key == Key.MediaPreviousTrack)
            {
                for (int i = 0; i < 3; i++)
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown || e.Key == Key.MediaNextTrack)
            {
                for (int i = 0; i < 3; i++)
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                e.Handled = true;
            }
        }
        
        private System.Windows.Media.Imaging.BitmapImage LoadOptimizedImage(string imageUrl, int width = 300)
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imageUrl);
            bitmap.DecodePixelWidth = width; 
            bitmap.EndInit();
            return bitmap;
        }
        
        private async void LoadModalImageAsync(System.Windows.Controls.Image imgControl, string url, int width)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                
                using var client = new System.Net.Http.HttpClient();
                var bytes = await client.GetByteArrayAsync(url);
                using var stream = new System.IO.MemoryStream(bytes);
                
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = width;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze(); 
                
                imgControl.Source = bitmap;
            }
            catch { }
        }
    }
}