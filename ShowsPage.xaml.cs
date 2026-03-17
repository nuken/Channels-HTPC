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

namespace ChannelsNativeTest
{
    public partial class ShowsPage : Page
    {
        private List<Episode> _allEpisodes = new List<Episode>();
        private List<TvShow> _allShows = new List<TvShow>(); 
        private string _baseUrl = "";
		private UserSettings _settings;

        public ShowsPage()
        {
            InitializeComponent();
            _settings = SettingsManager.Load(); // <-- Moved this here so it's never null!
            this.Loaded += Page_Loaded;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsManager.Load();
            _baseUrl = _settings.LastServerAddress;
			
            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                LoadingText.Text = "⚠️ No DVR Server IP configured in Settings.";
                return;
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

            if (SortBox.SelectedIndex == 0) filtered = filtered.OrderBy(s => s.Name);
            else if (SortBox.SelectedIndex == 1) filtered = filtered.OrderByDescending(s => s.CreatedAt);
            else if (SortBox.SelectedIndex == 2) filtered = filtered.OrderByDescending(s => s.ReleaseYear);

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
                // Use the RAM-friendly loader to make the grid use 90% less memory
                try { if (!string.IsNullOrWhiteSpace(show.ImageUrl)) img.Source = LoadOptimizedImage(show.ImageUrl, 300); } catch { }

                // FIXED: Explicitly forcing a dark background and white/gray text so it doesn't break in Light Mode!
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

            if (ShowsWrapPanel.Children.Count > 0)
                ((UIElement)ShowsWrapPanel.Children[0]).Focus();
        }

        private void Show_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is TvShow show)
            {
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
            SelectedShowImage.Source = null; // Clear the previous show poster so it doesn't ghost
            if (!string.IsNullOrWhiteSpace(show.ImageUrl))
            {
                LoadModalImageAsync(SelectedShowImage, show.ImageUrl, 400); 
            }

            SeasonsStackPanel.Children.Clear();
            EpisodesStackPanel.Children.Clear();
            _selectedSeasonButton = null;

            // Get all episodes for this specific show
            var showEpisodes = _allEpisodes.Where(ep => ep.ShowId == show.Id).ToList();

            // --- FIXED: Populate Extended Metadata safely ---
            if (_settings.ShowExtendedMetadata && showEpisodes.Any())
            {
                var firstEp = showEpisodes.First();
                ModalExtendedData.Visibility = Visibility.Visible;
                
                ModalRating.Text = !string.IsNullOrWhiteSpace(firstEp.ContentRating) ? firstEp.ContentRating : "NR";
                RatingBorder.Visibility = string.IsNullOrWhiteSpace(firstEp.ContentRating) ? Visibility.Collapsed : Visibility.Visible;

                // Safely check if lists exist before attempting to join them
                ModalTags.Text = (firstEp.Tags != null && firstEp.Tags.Any()) ? string.Join(" • ", firstEp.Tags) : "";
                ModalCast.Text = (firstEp.Cast != null && firstEp.Cast.Any()) ? $"Starring: {string.Join(", ", firstEp.Cast.Take(6))}" : "";
            }
            else
            {
                ModalExtendedData.Visibility = Visibility.Collapsed;
            }
            
            // Group the episodes by Season Number, sorting the Seasons descending (newest season at the top)
            var seasons = showEpisodes.GroupBy(ep => ep.SeasonNumber).OrderByDescending(g => g.Key).ToList();

            if (!seasons.Any()) return;

            // Dynamically create the Season buttons
            foreach (var seasonGroup in seasons)
            {
                int seasonNum = seasonGroup.Key;
                // If the DVR labels it Season 0, it's usually "Specials" or "Extras"
                string seasonText = seasonNum == 0 ? "Specials" : $"Season {seasonNum}";

                var btn = new Button
                {
                    Content = new TextBlock { Text = seasonText, FontSize = 16, FontWeight = FontWeights.Bold },
                    Style = (Style)FindResource("SeasonButton"),
                    Tag = seasonGroup.ToList() // Store the episodes for this season inside the button's memory
                };

                btn.Click += SeasonButton_Click;
                SeasonsStackPanel.Children.Add(btn);
            }

            // Auto-select the latest season by default when the page opens
            if (SeasonsStackPanel.Children.Count > 0)
            {
                var latestSeasonBtn = (Button)SeasonsStackPanel.Children[0];
                SelectSeason(latestSeasonBtn);
                
                // Drop focus immediately into the Episodes list so the user can just hit "Play"
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
            // 1. Reset the styling of the previously selected button
            if (_selectedSeasonButton != null)
            {
                _selectedSeasonButton.Background = Brushes.Transparent;
                _selectedSeasonButton.SetResourceReference(Button.ForegroundProperty, "TextSecondary");
            }

            // 2. Highlight the newly clicked button
            _selectedSeasonButton = btn;
            _selectedSeasonButton.SetResourceReference(Button.BackgroundProperty, "CardHoverBackground");
            _selectedSeasonButton.SetResourceReference(Button.ForegroundProperty, "TextPrimary");

            // 3. Render the episodes for this specific season
            if (btn.Tag is List<Episode> seasonEpisodes)
            {
                RenderEpisodesList(seasonEpisodes);
            }
        }

        private void RenderEpisodesList(List<Episode> episodes)
        {
            EpisodesStackPanel.Children.Clear();

            // Order episodes within the season (Newest episode at the top)
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
                    mainWin.ActivePlayerWindow.Closed += (s, args) => mainWin.ActivePlayerWindow = null;
                    mainWin.ActivePlayerWindow.Show();
                }
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (EpisodesView.Visibility == Visibility.Visible)
            {
                EpisodesView.Visibility = Visibility.Collapsed;
				FilterBar.Visibility = Visibility.Collapsed;
                ShowsScrollViewer.Visibility = Visibility.Visible;
                PageTitle.Text = "🍿 TV SHOWS";
                BackButton.Content = "🏠 Home";
                ToggleFiltersButton.Visibility = Visibility.Visible;
				
                if (ShowsWrapPanel.Children.Count > 0)
                    ((UIElement)ShowsWrapPanel.Children[0]).Focus();
            }
            else
            {
                NavigationService?.GoBack();
            }
        }

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                BackButton_Click(null!, null!);
                e.Handled = true;
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
            // NEW: USB Remote Page Up / Page Down support!
            else if (e.Key == Key.PageUp || e.Key == Key.MediaPreviousTrack)
            {
                // Rapidly jump focus UP 3 rows!
                for (int i = 0; i < 3; i++)
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown || e.Key == Key.MediaNextTrack)
            {
                // Rapidly jump focus DOWN 3 rows!
                for (int i = 0; i < 3; i++)
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                e.Handled = true;
            }
        }
		
		// --- NEW: RAM SAVER FOR IMAGES ---
        private System.Windows.Media.Imaging.BitmapImage LoadOptimizedImage(string imageUrl, int width = 300)
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imageUrl);
            bitmap.DecodePixelWidth = width; 
            bitmap.EndInit();
            // Removed Freeze() here as well!
            return bitmap;
        }
		
		// --- NEW: Bulletproof Async Image Loader for Modals ---
        private async void LoadModalImageAsync(System.Windows.Controls.Image imgControl, string url, int width)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return;
                
                // 1. Download the raw image bytes in the background (Bypasses WPF's HTTP bug)
                using var client = new System.Net.Http.HttpClient();
                var bytes = await client.GetByteArrayAsync(url);
                using var stream = new System.IO.MemoryStream(bytes);
                
                // 2. Decode the memory stream safely (Bypasses the "Gold Rush" JPEG crash)
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = width;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze(); // 100% safe to freeze now that the download is complete!
                
                // 3. Assign the completely processed image to the UI
                imgControl.Source = bitmap;
            }
            catch { }
        }
    }
}