using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Globalization;
using System.Windows.Data;

namespace ChannelsNativeTest
{
    public partial class MoviesPage : Page
    {
        private readonly ChannelsApi _api = new ChannelsApi();
        private ObservableCollection<Movie> _movies = new ObservableCollection<Movie>();
        private UserSettings _settings;
        private Button? _lastFocusedMovieButton;
        private Movie? _selectedMovie;

        // NEW: The master list to filter against
        private List<Movie> _allMovies = new List<Movie>();

        public MoviesPage()
        {
            InitializeComponent();
            MoviesItemsControl.ItemsSource = _movies;
            _settings = SettingsManager.Load();
            
            this.Loaded += Page_Loaded;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            string baseUrl = _settings.LastServerAddress;

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                StatusText.Text = "Searching for DVR...";
                var servers = await _api.DiscoverDvrServersAsync();
                if (servers.Any())
                {
                    baseUrl = servers.First().BaseUrl;
                    _settings.LastServerAddress = baseUrl;
                    SettingsManager.Save(_settings);
                }
                else
                {
                    StatusText.Text = "Error: Could not find DVR Server.";
                    StatusText.Foreground = System.Windows.Media.Brushes.Red;
                    return;
                }
            }

            StatusText.Text = "Loading movies...";
            StatusText.Foreground = System.Windows.Media.Brushes.Orange;

            // Fetch to the MASTER list
            _allMovies = await _api.GetMoviesAsync(baseUrl);

            if (_allMovies.Any())
            {
                StatusText.Text = $"{_allMovies.Count} Movies Loaded.";
                StatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;

                // Dynamically build the Genre Dropdown
                var genres = _allMovies.Where(m => m.Genres != null)
                                       .SelectMany(m => m.Genres!)
                                       .Distinct()
                                       .OrderBy(g => g).ToList();
                
                GenreBox.Items.Clear();
                GenreBox.Items.Add(new ComboBoxItem { Content = "All Genres", IsSelected = true });
                foreach (var g in genres) GenreBox.Items.Add(new ComboBoxItem { Content = g });

                ApplyFilters();
            }
            else
            {
                StatusText.Text = "No movies found in the library.";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        // --- FILTERING LOGIC ---

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
            
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var request = new TraversalRequest(FocusNavigationDirection.First);
                MoviesItemsControl.MoveFocus(request);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_allMovies.Count > 0) ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (_allMovies == null || _allMovies.Count == 0) return;

            var filtered = _allMovies.AsEnumerable();

            // 1. Text Search
            string search = SearchBox.Text.ToLower().Trim();
            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(m => m.Title.ToLower().Contains(search));

            // 2. Genre
            if (GenreBox.SelectedIndex > 0 && GenreBox.SelectedItem is ComboBoxItem gi)
            {
                string genre = gi.Content.ToString() ?? "";
                filtered = filtered.Where(m => m.Genres != null && m.Genres.Contains(genre));
            }

            // 3. Watched Status
            if (WatchedBox.SelectedIndex == 1) filtered = filtered.Where(m => !m.Watched);
            if (WatchedBox.SelectedIndex == 2) filtered = filtered.Where(m => m.Watched);

            // 4. Sort
            if (SortBox.SelectedIndex == 0) filtered = filtered.OrderBy(m => m.Title);
            else if (SortBox.SelectedIndex == 1) filtered = filtered.OrderByDescending(m => m.CreatedAt);
            else if (SortBox.SelectedIndex == 2) filtered = filtered.OrderByDescending(m => m.ReleaseYear);

            // Update the Observable Collection! This safely updates the UI.
            _movies.Clear();
            foreach (var m in filtered)
            {
                _movies.Add(m);
            }

            // Update count text
            StatusText.Text = $"{_movies.Count} Movies Loaded.";

            // Auto-focus the very first movie so the D-Pad is ready to go!
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var request = new TraversalRequest(FocusNavigationDirection.First);
                MoviesItemsControl.MoveFocus(request);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }


        private void HomeButton_Click(object sender, RoutedEventArgs e) => NavigationService?.GoBack();

        private void MovieCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is Movie movie)
            {
                _lastFocusedMovieButton = btn;
                _selectedMovie = movie;

                ModalTitle.Text = movie.Title;
                ModalYear.Text = movie.ReleaseYear?.ToString() ?? "Unknown Year";
                ModalDuration.Text = movie.DisplayDuration;
                ModalSummary.Text = movie.Summary;

                ModalImage.Source = null; // Clear the previous movie poster so it doesn't ghost
                if (!string.IsNullOrWhiteSpace(movie.PosterUrl))
                {
                    LoadModalImageAsync(ModalImage, movie.PosterUrl, 400);
                }

                // --- NEW: Populate Extended Metadata if Setting is ON ---
                if (_settings.ShowExtendedMetadata)
                {
                    ModalExtendedData.Visibility = Visibility.Visible;
                    
                    ModalRating.Text = !string.IsNullOrWhiteSpace(movie.ContentRating) ? movie.ContentRating : "NR";
                    RatingBorder.Visibility = string.IsNullOrWhiteSpace(movie.ContentRating) ? Visibility.Collapsed : Visibility.Visible;

                    ModalTags.Text = movie.Tags != null ? string.Join(" • ", movie.Tags) : "";
                    ModalDirectors.Text = movie.Directors != null ? $"Directed by: {string.Join(", ", movie.Directors)}" : "";
                    
                    // Grab up to the first 6 cast members so it doesn't overflow the screen
                    ModalCast.Text = movie.Cast != null ? $"Starring: {string.Join(", ", movie.Cast.Take(6))}" : "";
                }
                else
                {
                    ModalExtendedData.Visibility = Visibility.Collapsed;
                }

                ModalOverlay.Visibility = Visibility.Visible;
                ToggleFiltersButton.Visibility = Visibility.Collapsed;
                PlayButton.Focus(); 
            }
        }

        private void CloseModal_Click(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            ToggleFiltersButton.Visibility = Visibility.Visible;
            _selectedMovie = null;
            _lastFocusedMovieButton?.Focus(); 
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedMovie != null)
            {
                string baseUrl = _settings.LastServerAddress;
                string streamUrl = $"{baseUrl.TrimEnd('/')}/dvr/files/{_selectedMovie.Id}/hls/master.m3u8?vcodec=copy&acodec=copy";
                
                var mainWindow = (MainWindow)Application.Current.MainWindow;
                if (mainWindow.ActivePlayerWindow != null) mainWindow.ActivePlayerWindow.Close();

                mainWindow.ActivePlayerWindow = new PlayerWindow(streamUrl, _selectedMovie.Title, _selectedMovie.PosterUrl, _selectedMovie.Commercials);
                mainWindow.ActivePlayerWindow.Closed += (s, args) => mainWindow.ActivePlayerWindow = null; 
                mainWindow.ActivePlayerWindow.Show();
                
                ModalOverlay.Visibility = Visibility.Collapsed;
                ToggleFiltersButton.Visibility = Visibility.Visible;
            }
        }

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ModalOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
                {
                    CloseModal_Click(null!, null!);
                    e.Handled = true;
                    return;
                }
            }
            
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                if (FilterBar.Visibility == Visibility.Visible)
                {
                    ApplyAndClose_Click(null!, null!);
                }
                else
                {
                    NavigationService?.GoBack();
                }
                e.Handled = true;
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

            if (e.Key == Key.PageUp || e.Key == Key.MediaPreviousTrack)
            {
                // Jump focus UP 3 rows instead of just visually scrolling!
                for (int i = 0; i < 3; i++)
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown || e.Key == Key.MediaNextTrack)
            {
                // Jump focus DOWN 3 rows!
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
	
	// --- NEW: XAML CONVERTER FOR RAM OPTIMIZATION ---
    public class ImageUrlToOptimizedBitmapConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(url);
                    bitmap.DecodePixelWidth = 200; 
                    bitmap.EndInit();
                    // Removed Freeze() so WPF can download it asynchronously!
                    return bitmap;
                }
                catch { return null; }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) 
            => throw new NotImplementedException();
    }	
}