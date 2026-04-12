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
using System.Windows.Media;

namespace FeralCode
{
    public partial class MoviesPage : Page
    {
        private readonly ChannelsApi _api = new ChannelsApi();
        private ObservableCollection<Movie> _movies = new ObservableCollection<Movie>();
        private UserSettings _settings;
        private Button? _lastFocusedMovieButton;
        private Movie? _selectedMovie;
        private List<Movie> _allMovies = new List<Movie>();
        // --- Lazy Loading Trackers ---
        private List<Movie> _currentFilteredMovies = new List<Movie>();
        private int _currentMovieIndex = 0;
        private bool _isLoadingMovies = false;
        private bool _isInitializing = false;

        public MoviesPage()
        {
            InitializeComponent();
            MoviesItemsControl.ItemsSource = _movies;
            _settings = SettingsManager.Load();
            
            this.Loaded += Page_Loaded;
            
            // --- NEW: Global Scroll Listener ---
            this.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(MoviesScrollViewer_ScrollChanged), true);
        }

        // --- NEW: A helper to dynamically find the ScrollViewer inside your XAML ---
        private ScrollViewer? FindScrollViewer(DependencyObject depObj)
        {
            if (depObj is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        private void MoviesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Capture the specific ScrollViewer that fired the event
            if (e.OriginalSource is ScrollViewer sv)
            {
                // Only trigger if the user scrolled vertically OR if the layout just expanded
                if (Math.Abs(e.VerticalChange) > 0 || e.ExtentHeightChange > 0)
                {
                    double threshold = e.ExtentHeight > 200 ? 800 : 15;
                    if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - threshold)
                    {
                        LoadNextMovieBatch(sv); // Pass the scrollviewer down!
                    }
                }
            }
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            _settings = SettingsManager.Load();
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
                // Dynamically build the Genre Dropdown
                var genres = _allMovies.Where(m => m.Genres != null)
                                       .SelectMany(m => m.Genres!)
                                       .Distinct()
                                       .OrderBy(g => g).ToList();
                
                _isInitializing = true; // Lock the event
                GenreBox.Items.Clear();
                GenreBox.Items.Add(new ComboBoxItem { Content = "All Genres", IsSelected = true });
                foreach (var g in genres) GenreBox.Items.Add(new ComboBoxItem { Content = g });
                _isInitializing = false; // Unlock

                ApplyFilters();
            }
            else
            {
                StatusText.Text = "No movies found in the library.";
                StatusText.Foreground = System.Windows.Media.Brushes.Red;
            }
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
            
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var request = new TraversalRequest(FocusNavigationDirection.First);
                MoviesItemsControl.MoveFocus(request);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return; // Ignore event if we are building the UI
            if (_allMovies.Count > 0) ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (_allMovies == null || _allMovies.Count == 0) return;

            var filtered = _allMovies.AsEnumerable();

            string search = SearchBox.Text.ToLower().Trim();
            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(m => m.Title.ToLower().Contains(search));

            if (GenreBox.SelectedIndex > 0 && GenreBox.SelectedItem is ComboBoxItem gi)
            {
                string genre = gi.Content.ToString() ?? "";
                filtered = filtered.Where(m => m.Genres != null && m.Genres.Contains(genre));
            }

            if (WatchedBox.SelectedIndex == 1) filtered = filtered.Where(m => !m.Watched);
            if (WatchedBox.SelectedIndex == 2) filtered = filtered.Where(m => m.Watched);

            if (SortBox.SelectedIndex == 0) filtered = filtered.OrderBy(m => m.Title);
            else if (SortBox.SelectedIndex == 1) filtered = filtered.OrderByDescending(m => m.CreatedAt);
            else if (SortBox.SelectedIndex == 2) filtered = filtered.OrderByDescending(m => m.ReleaseYear);

            // --- NEW: Reset the lazy loader and start drawing ---
            _currentFilteredMovies = filtered.ToList();
            _movies.Clear();
            _currentMovieIndex = 0;
            _isLoadingMovies = false; 

            LoadNextMovieBatch(null);
        } // End of ApplyFilters

        // Note: We added the (ScrollViewer? sv) parameter!
        private async void LoadNextMovieBatch(ScrollViewer? sv)
        {
            if (_isLoadingMovies || _currentMovieIndex >= _currentFilteredMovies.Count) return;
            _isLoadingMovies = true;

            var batch = _currentFilteredMovies.Skip(_currentMovieIndex).Take(50).ToList();
            foreach (var m in batch)
            {
                _movies.Add(m);
            }

            bool wasFirstBatch = (_currentMovieIndex == 0);
            _currentMovieIndex += batch.Count;
            
            StatusText.Text = $"{_movies.Count} of {_currentFilteredMovies.Count} Movies Loaded.";
            StatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;

            if (wasFirstBatch && !SearchBox.IsKeyboardFocusWithin)
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    var request = new TraversalRequest(FocusNavigationDirection.First);
                    MoviesItemsControl.MoveFocus(request);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }

            await Task.Delay(50); // Give the UI time to physically draw the items
            _isLoadingMovies = false;

            // --- FIX: If the screen is so big that 50 items didn't create a scrollbar, load more! ---
            if (sv != null && sv.ViewportHeight >= sv.ExtentHeight - 100)
            {
                LoadNextMovieBatch(sv);
            }
        }

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

                ModalImage.Source = null; 
                if (!string.IsNullOrWhiteSpace(movie.PosterUrl))
                {
                    LoadModalImageAsync(ModalImage, movie.PosterUrl, 400);
                }

                if (_settings.ShowExtendedMetadata)
                {
                    ModalExtendedData.Visibility = Visibility.Visible;
                    
                    ModalRating.Text = !string.IsNullOrWhiteSpace(movie.ContentRating) ? movie.ContentRating : "NR";
                    RatingBorder.Visibility = string.IsNullOrWhiteSpace(movie.ContentRating) ? Visibility.Collapsed : Visibility.Visible;

                    ModalTags.Text = movie.Tags != null ? string.Join(" • ", movie.Tags) : "";
                    ModalDirectors.Text = movie.Directors != null ? $"Directed by: {string.Join(", ", movie.Directors)}" : "";
                    
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

                string streamUrl = $"{baseUrl.TrimEnd('/')}/dvr/files/{_selectedMovie.Id}/stream.mpg?format=ts&vcodec=copy&acodec=copy";
                
                var mainWindow = (MainWindow)Application.Current.MainWindow;
                if (mainWindow.ActivePlayerWindow != null) mainWindow.ActivePlayerWindow.Close();

                mainWindow.ActivePlayerWindow = new PlayerWindow(streamUrl, _selectedMovie.Title, _selectedMovie.PosterUrl, _selectedMovie.Commercials);
                
                mainWindow.ActivePlayerWindow.Closed += (s, args) => 
                {
                    mainWindow.ActivePlayerWindow = null; 
                    if (_settings.MinimizeOnPlay) mainWindow.WindowState = WindowState.Normal;
                    mainWindow.Show(); 
                    Application.Current.Dispatcher.InvokeAsync(() => _lastFocusedMovieButton?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
                };
                
                if (_settings.MinimizeOnPlay) mainWindow.WindowState = WindowState.Minimized;
                else mainWindow.Hide(); 
                
                mainWindow.ActivePlayerWindow.Show(); 
                
                ModalOverlay.Visibility = Visibility.Collapsed;
                ToggleFiltersButton.Visibility = Visibility.Visible;
            }
        }

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Back && e.OriginalSource is TextBox)
            {
                return; 
            }

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
                e.Handled = true;

                if (FilterBar.Visibility == Visibility.Visible)
                {
                    ApplyAndClose_Click(null!, null!);
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
                if (Keyboard.FocusedElement is Button btn && btn.Tag is Movie)
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