using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using System.Windows.Controls;

namespace FeralCode
{
    public partial class PlayerWindow : Window
    {
        private MediaPlayer _mediaPlayer;
        private string _baseUrl = ""; 
        private List<Channel>? _channels;
        private int _currentIndex;
		private DateTime _lastMouseMove = DateTime.MinValue;
        
        // --- NEW: Movie Mode Variables ---
        private bool _isMovieMode = false;
        private string _movieStreamUrl = "";
        private string _movieTitle = "";
        private string _moviePosterUrl = "";
        private List<double>? _movieCommercials;
		private UserSettings _settings;
        private bool _isDraggingTimeline = false;
        // --- NEW: Auto-Scrubbing Trackers ---
        private bool _isScrubbing = false;
        private long _scrubTargetTime = 0;
		private DispatcherTimer _remoteScrubTimer;
        private string _remoteScrubDirection = "";
        
        private DispatcherTimer _uiTimer;
        private DispatcherTimer _statsTimer;
		private DispatcherTimer? _liveProgressTimer;
        private bool _showStats = false;
        private bool _isPipMode = false;
        private WindowState _previousState;
        private WindowStyle _previousStyle;
        private double _previousWidth;
        private double _previousHeight;
        private double _previousTop;
        private double _previousLeft;
        private bool _previousTopmost;

        // --- ORIGINAL CONSTRUCTOR: Live TV Mode ---
        public PlayerWindow(string baseUrl, List<Channel> channels, int startIndex)
        {
            InitializeComponent();
            _settings = SettingsManager.Load();
            _baseUrl = baseUrl;
            _channels = channels;
            _currentIndex = startIndex;
			_remoteScrubTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _remoteScrubTimer.Tick += RemoteScrubTimer_Tick;
			_liveProgressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveProgressTimer.Tick += LiveProgressTimer_Tick;

            _mediaPlayer = new MediaPlayer(MainWindow.SharedLibVLC);
            VlcVideoView.MediaPlayer = _mediaPlayer;
            _mediaPlayer.EndReached += MediaPlayer_EndReached;
			_mediaPlayer.Buffering += MediaPlayer_Buffering;
			_mediaPlayer.Playing += MediaPlayer_Playing;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += StatsTimer_Tick;

            this.Loaded += PlayerWindow_Loaded;
            this.Closed += PlayerWindow_Closed;
			this.PreviewKeyUp += Window_PreviewKeyUp;
			// If the user wants full screen, take over the monitor immediately!
            if (_settings.StartPlayersFullscreen)
            {
                this.WindowState = WindowState.Maximized;
                this.WindowStyle = WindowStyle.None;
                this.ResizeMode = ResizeMode.NoResize;
                this.Topmost = true; // Keeps it above the Windows taskbar
                _isFullscreen = true; // Syncs with your existing toggle logic
            }
        }
		
		private void LiveProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_channels == null || !_channels.Any()) return;

            var currentChannel = _channels[_currentIndex];
            var currentAiring = currentChannel.CurrentAirings?.FirstOrDefault(a => a.IsAiringNow);

            if (currentAiring != null && currentAiring.Duration.HasValue && !_isDraggingTimeline)
            {
                // Calculate how many milliseconds have passed since the show started
                double elapsedMs = (DateTime.Now - currentAiring.StartTime).TotalMilliseconds;
                double maxMs = currentAiring.Duration.Value * 1000;

                // Keep the bar within bounds
                if (elapsedMs > maxMs) elapsedMs = maxMs;
                if (elapsedMs < 0) elapsedMs = 0;

                TimelineSlider.Value = elapsedMs;
                CurrentTimeText.Text = TimeSpan.FromMilliseconds(elapsedMs).ToString(@"h\:mm\:ss");
            }
        }

        // --- NEW CONSTRUCTOR: Movie Mode! ---
        public PlayerWindow(string streamUrl, string movieTitle, string posterUrl, List<double>? commercials)
        {
            InitializeComponent();
            _settings = SettingsManager.Load();
            _isMovieMode = true;
            _movieStreamUrl = streamUrl;
            _movieTitle = movieTitle;
            _moviePosterUrl = posterUrl;
            _movieCommercials = commercials;
			_remoteScrubTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _remoteScrubTimer.Tick += RemoteScrubTimer_Tick;

            _mediaPlayer = new MediaPlayer(MainWindow.SharedLibVLC);
            VlcVideoView.MediaPlayer = _mediaPlayer;
            _mediaPlayer.EndReached += MediaPlayer_EndReached;
			_mediaPlayer.Buffering += MediaPlayer_Buffering;
			_mediaPlayer.Playing += MediaPlayer_Playing;
            
            // Listen to the VLC clock tick for commercial skipping!
            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
			_mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += StatsTimer_Tick;

            this.Loaded += PlayerWindow_Loaded;
            this.Closed += PlayerWindow_Closed;
			this.PreviewKeyUp += Window_PreviewKeyUp;
			// If the user wants full screen, take over the monitor immediately!
            if (_settings.StartPlayersFullscreen)
            {
                this.WindowState = WindowState.Maximized;
                this.WindowStyle = WindowStyle.None;
                this.ResizeMode = ResizeMode.NoResize;
                this.Topmost = true; // Keeps it above the Windows taskbar
                _isFullscreen = true; // Syncs with your existing toggle logic
            }
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (!_isMovieMode) return; // Ignore VLC's internal stream time for Live TV

            long currentTime = e.Time;

            // 1. Commercial Skip Logic
            if (_isMovieMode && _movieCommercials != null && _movieCommercials.Count >= 2 && _settings.AutoSkipCommercials)
            {
                for (int i = 0; i < _movieCommercials.Count - 1; i += 2)
                {
                    long startMs = (long)(_movieCommercials[i] * 1000);
                    long endMs = (long)(_movieCommercials[i + 1] * 1000);

                    if (currentTime >= startMs && currentTime < endMs - 1000)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _mediaPlayer.Time = endMs; 
                            ShowActionOverlay("⏭️ Commercial Skipped");
                        }));
                        break;
                    }
                }
            }

            // 2. Timeline Slider Update Logic
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // --- THIS is where !_isScrubbing belongs! ---
                if (!_isDraggingTimeline && !_isScrubbing) 
                {
                    TimelineSlider.Value = currentTime;
                    CurrentTimeText.Text = TimeSpan.FromMilliseconds(currentTime).ToString(@"h\:mm\:ss");
                }
            }));
        }
        
        // We need to know how long the movie is to set the Slider's maximum length!
        private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            if (!_isMovieMode) return; // Ignore VLC's internal length for Live TV

            long safeLength = e.Length;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                TimelineSlider.Maximum = safeLength;
                TotalTimeText.Text = TimeSpan.FromMilliseconds(safeLength).ToString(@"h\:mm\:ss");
            }));
        }
		
		// --- NEW: Debounce flag for micro-stutters ---
        private bool _isWaitingToBuffer = false;

        // --- NEW: Real-time Buffering Intercept ---
        private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e)
        {
            // FIXED: Grab the value immediately before VLC destroys the memory!
            float safeCache = e.Cache; 

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (safeCache < 100)
                {
                    // If the curtain is hidden and we aren't already waiting...
                    if (LoadingOverlay.Visibility != Visibility.Visible && !_isWaitingToBuffer)
                    {
                        _isWaitingToBuffer = true;
                        
                        // Give VLC a 750ms grace period to recover on its own!
                        await Task.Delay(750);
                        
                        // If it is STILL buffering after the grace period, drop the curtain!
                        if (_isWaitingToBuffer)
                        {
                            LoadingOverlay.Visibility = Visibility.Visible;
                        }
                    }

                    // If the curtain actually became visible, update the text
                    if (LoadingOverlay.Visibility == Visibility.Visible)
                    {
                        LoadingText.Text = $"Buffering... {(int)safeCache}%";
                    }
                }
                else
                {
                    // Hit 100%! Cancel the waiting flag and hide the curtain instantly.
                    _isWaitingToBuffer = false;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                }
            }));
        }
		
        // --- NEW: Bulletproof Failsafe for VOD/Movies ---
        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                // --- NEW: Give the video renderer 300ms to snap to full screen and draw the first frame ---
                await Task.Delay(300);

                // Now that the video is actually visible behind the scenes, drop the curtain!
                _isWaitingToBuffer = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }));
        }
		
        public void TogglePiP()
        {
            if (!_isPipMode)
            {
                _previousState = this.WindowState;
                _previousStyle = this.WindowStyle;
                _previousWidth = this.Width;
                _previousHeight = this.Height;
                _previousTop = this.Top;
                _previousLeft = this.Left;
                _previousTopmost = this.Topmost;

                this.WindowState = WindowState.Normal;
                this.WindowStyle = WindowStyle.None; 
                this.Topmost = true; 
                this.Width = 426;    
                this.Height = 240;   
                
                var workArea = System.Windows.SystemParameters.WorkArea;
                this.Left = workArea.Right - this.Width - 20;
                this.Top = workArea.Bottom - this.Height - 20;

                _isPipMode = true;
            }
            else
            {
                this.WindowStyle = _previousStyle;
                this.Topmost = _previousTopmost;
                this.WindowState = _previousState;
                this.Width = _previousWidth;
                this.Height = _previousHeight;
                this.Top = _previousTop;
                this.Left = _previousLeft;
                
                _isPipMode = false;
            }
        }
        
        public void VolumeUp()
        {
            if (_mediaPlayer != null)
            {
                if (_mediaPlayer.Mute) _mediaPlayer.Mute = false;
                int newVol = _mediaPlayer.Volume + 10;
                _mediaPlayer.Volume = newVol > 100 ? 100 : newVol;
            }
        }

        public void VolumeDown()
        {
            if (_mediaPlayer != null)
            {
                int newVol = _mediaPlayer.Volume - 10;
                _mediaPlayer.Volume = newVol < 0 ? 0 : newVol;
            }
        }
		
		// --- NEW: CLOSED CAPTION TOGGLE ---
        public void ToggleClosedCaptions()
        {
            if (_mediaPlayer == null) return;

            // In VLC, -1 means subtitles/CC are currently disabled
            if (_mediaPlayer.Spu == -1)
            {
                // Grab the track list and find the first real subtitle track (Id > -1)
                var firstCcTrack = _mediaPlayer.SpuDescription.FirstOrDefault(track => track.Id > -1);
                
                // If we found a valid track, turn it on!
                if (firstCcTrack.Id > -1)
                {
                    _mediaPlayer.SetSpu(firstCcTrack.Id);
                }
            }
            else
            {
                // If it is anything other than -1, they are currently on, so turn them off!
                _mediaPlayer.SetSpu(-1);
            }
        }
		
		// --- NEW: Closed Captions / Subtitles Toggle ---
        public void ToggleSubtitles()
        {
            if (_mediaPlayer == null) return;

            var spus = _mediaPlayer.SpuDescription;
            
            // FIX 1: Use .Length for arrays instead of .Count
            if (spus == null || spus.Length <= 1)
            {
                ShowActionOverlay("🚫 No CC Available");
                return;
            }

            int currentId = _mediaPlayer.Spu;
            
            // FIX 2: Use a safe standard loop to find the current track index
            int currentIndex = 0;
            for (int i = 0; i < spus.Length; i++)
            {
                if (spus[i].Id == currentId)
                {
                    currentIndex = i;
                    break;
                }
            }
            
            // FIX 3: Use .Length for the modulo wrap-around logic
            int nextIndex = (currentIndex + 1) % spus.Length;
            var nextSpu = spus[nextIndex];
            
            // Apply the new track
            _mediaPlayer.SetSpu(nextSpu.Id);

            // Show a sleek overlay message using your existing modern UI!
            string statusText = nextSpu.Id == -1 ? "CC: Off" : $"CC: {nextSpu.Name}";
            ShowActionOverlay(statusText);
        }

        private void BtnCC_Click(object sender, RoutedEventArgs e)
        {
            OpenSubtitlesMenu();
        }

        // --- NEW: Dynamic Popup Menu for Subtitles ---
        private void OpenSubtitlesMenu()
        {
            if (_mediaPlayer == null) return;

            var spus = _mediaPlayer.SpuDescription;
            if (spus == null || spus.Length <= 1)
            {
                ShowActionOverlay("🚫 No CC Available");
                return;
            }

            // Create a brand new Context Menu on the fly
            ContextMenu ccMenu = new ContextMenu();
            
            // Apply your custom theme colors so it looks professional!
            ccMenu.Background = (System.Windows.Media.Brush)Application.Current.FindResource("PanelBackground");
            ccMenu.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary");
            ccMenu.BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush");
            ccMenu.BorderThickness = new Thickness(2);

            int currentId = _mediaPlayer.Spu;

            foreach (var spu in spus)
            {
                MenuItem item = new MenuItem();
                
                // Clean up the name for the UI
                item.Header = spu.Id == -1 ? "Off" : $"Track: {spu.Name}";
                item.Tag = spu.Id;
                item.FontSize = 16;
                item.Padding = new Thickness(10, 5, 10, 5);
                
                // Put a checkmark next to the one currently playing
                if (spu.Id == currentId)
                {
                    item.IsChecked = true; 
                    item.FontWeight = FontWeights.Bold;
                }

                // What happens when the user clicks an option in the menu
                item.Click += (s, args) =>
                {
                    int selectedId = (int)((MenuItem)s!).Tag;
                    _mediaPlayer.SetSpu(selectedId);
                    
                    string statusText = selectedId == -1 ? "CC: Off" : $"CC: {spu.Name}";
                    ShowActionOverlay(statusText);
                };

                ccMenu.Items.Add(item);
            }

            // Bind it to the CC Button and pop it open ABOVE the control bar
            BtnCC.ContextMenu = ccMenu;
            ccMenu.PlacementTarget = BtnCC;
            ccMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            ccMenu.IsOpen = true;
        }

        public void ToggleMute()
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Mute = !_mediaPlayer.Mute;
            }
        }
        
        private void PlayerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isMovieMode) PlayMovie();
            else PlayCurrentChannel();
        }

        private void PlayMovie()
        {
            UiChannelNumber.Text = "🎬 MOVIE";
            UiShowTitle.Text = _movieTitle;
            
            if (!string.IsNullOrWhiteSpace(_moviePosterUrl))
                UiChannelLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(_moviePosterUrl));
            else
                UiChannelLogo.Source = null;
            
            if (_mediaPlayer.IsPlaying) 
            {
                _mediaPlayer.Stop();
                // --- NEW: Hard kill the old network stream before starting the new one! ---
                if (_mediaPlayer.Media != null)
                {
                    _mediaPlayer.Media.Dispose();
                    _mediaPlayer.Media = null;
                }
            }

            var media = new Media(MainWindow.SharedLibVLC, new Uri(_movieStreamUrl));
            media.AddOption(":network-caching=2000");
			
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "Connecting...";

            using (media)
            {
                _mediaPlayer.Play(media);
            }
        }
		
		// --- NEW: Slider Dragging Logic ---
        private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingTimeline = true;
        }

        private void TimelineSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Only seek if we are in Movie/Recording mode
            if (_isMovieMode && _mediaPlayer.IsSeekable) 
            {
                _mediaPlayer.Time = (long)TimelineSlider.Value;
            }
            _isDraggingTimeline = false;
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingTimeline) 
                CurrentTimeText.Text = TimeSpan.FromMilliseconds(TimelineSlider.Value).ToString(@"h\:mm\:ss");
        }

        // --- NEW: Modern Overlay Animation ---
        private void ShowActionOverlay(string text)
        {
            ActionOverlayText.Text = text;
            ActionOverlayText.Opacity = 1.0;
            
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0, To = 0.0,
                Duration = new Duration(TimeSpan.FromSeconds(0.75)),
                BeginTime = TimeSpan.FromSeconds(0.5) // Wait half a second, then fade out
            };
            ActionOverlayText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            
            Overlay_MouseMove(null!, null!); // Keep the control bar visible
        }

        private void PlayCurrentChannel()
        {
            if (_channels == null || !_channels.Any()) return;

            var currentChannel = _channels[_currentIndex];
            var currentAiring = currentChannel.CurrentAirings?.FirstOrDefault(a => a.IsAiringNow);
            
            UiChannelNumber.Text = $"CH {currentChannel.Number}";
            UiShowTitle.Text = currentAiring != null ? currentAiring.DisplayTitle : currentChannel.Name;
            
            if (!string.IsNullOrWhiteSpace(currentChannel.ImageUrl))
            {
                UiChannelLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(currentChannel.ImageUrl));
            }
            else
            {
                UiChannelLogo.Source = null;
            }
            
            string streamUrl = "";
            int offsetSeconds = 0;

            if (currentChannel.Id != null && currentChannel.Id.StartsWith("virtual", StringComparison.OrdinalIgnoreCase))
            {
                if (currentAiring != null && !string.IsNullOrWhiteSpace(currentAiring.Source))
                {
                    string fileId = currentAiring.Source.Split('/').Last(); 
                    streamUrl = $"{_baseUrl}/dvr/files/{fileId}/hls/master.m3u8";
                    
                    offsetSeconds = (int)(DateTime.Now - currentAiring.StartTime).TotalSeconds;
                    if (offsetSeconds < 0) offsetSeconds = 0;
                }
                else
                {
                    MessageBox.Show("No active media is scheduled for this Virtual Channel right now.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    this.Close();
                    return;
                }
            }
            else
            {
                streamUrl = $"{_baseUrl}/devices/ANY/channels/{currentChannel.Number}/stream.mpg?format=ts";
            }

            if (_mediaPlayer.IsPlaying) 
            {
                _mediaPlayer.Stop();
                // --- NEW: Hard kill the old network stream before starting the new one! ---
                if (_mediaPlayer.Media != null)
                {
                    _mediaPlayer.Media.Dispose();
                    _mediaPlayer.Media = null;
                }
            }

            var media = new Media(MainWindow.SharedLibVLC, new Uri(streamUrl));

            media.AddOption(":network-caching=2000");
            media.AddOption(":live-caching=2000");
            media.AddOption(":http-reconnect");
			media.AddOption(":avcodec-hw=none");
            
            if (offsetSeconds > 0)
            {
                media.AddOption($":start-time={offsetSeconds}");
            }

           LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "Connecting...";
            
           if (currentAiring != null && currentAiring.Duration.HasValue)
            {
                TimelineSlider.Maximum = currentAiring.Duration.Value * 1000;
                TotalTimeText.Text = TimeSpan.FromSeconds(currentAiring.Duration.Value).ToString(@"h\:mm\:ss");
                _liveProgressTimer?.Start();
            }
            else
            {
                _liveProgressTimer?.Stop();
                TimelineSlider.Value = 0;
                TimelineSlider.Maximum = 1; 
                CurrentTimeText.Text = "LIVE";
                TotalTimeText.Text = "";
            }
            // ------------------------------------------

            using (media)
            {
                _mediaPlayer.Play(media);
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                BtnPlayPause.Content = "\u23F8"; 
            }
            else
            {
                _mediaPlayer.Play();
                BtnPlayPause.Content = "\u25B6"; 
            }
        }
        
        private void ChUp_Click(object sender, RoutedEventArgs e)
        {
            if (_isMovieMode || _channels == null) return; 

            _currentIndex++;
            if (_currentIndex >= _channels.Count) _currentIndex = 0; 
            
            PlayCurrentChannel();
            Overlay_MouseMove(null!, null!); 
        }

        private void ChDn_Click(object sender, RoutedEventArgs e)
        {
            if (_isMovieMode || _channels == null) return; 

            _currentIndex--;
            if (_currentIndex < 0) _currentIndex = _channels.Count - 1; 
            
            PlayCurrentChannel();
            Overlay_MouseMove(null!, null!); 
        }

        private void Rewind_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsSeekable) 
            {
                _mediaPlayer.Time -= 10000; 
                ShowActionOverlay("⏪ -10s");
            }
        }

        private void FastForward_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsSeekable) 
            {
                _mediaPlayer.Time += 30000;
                ShowActionOverlay("⏩ +30s");
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Volume = (int)e.NewValue;
            }
        }

        private void Overlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Debounce: Only process this once every 100 milliseconds
            if ((DateTime.Now - _lastMouseMove).TotalMilliseconds < 100) return;
            _lastMouseMove = DateTime.Now;

            if (ControlBar.Visibility != Visibility.Visible)
            {
                ControlBar.Visibility = Visibility.Visible;
                System.Windows.Input.Mouse.OverrideCursor = null;
            }

            _uiTimer.Stop();
            _uiTimer.Start();
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            ControlBar.Visibility = Visibility.Collapsed;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
            _uiTimer.Stop();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // --- NEW: Physical Keyboard Auto-Scrubbing ---
            if (e.Key == System.Windows.Input.Key.Right || e.Key == System.Windows.Input.Key.Left)
            {
                if (ControlBar.Visibility == Visibility.Collapsed && _isMovieMode)
                {
                    if (!_isScrubbing)
                    {
                        _isScrubbing = true;
                        _scrubTargetTime = _mediaPlayer.Time;
                        ActionOverlayText.BeginAnimation(UIElement.OpacityProperty, null); 
                        ActionOverlayText.Opacity = 1.0;
                    }

                    // Jump 10s on the first press, then 5s for every automatic repeat for smooth scrolling
                    long jumpAmount = (e.Key == System.Windows.Input.Key.Right) ? 
                                      (e.IsRepeat ? 5000 : 10000) : 
                                      (e.IsRepeat ? -5000 : -10000);

                    _scrubTargetTime += jumpAmount;
                    
                    if (_scrubTargetTime < 0) _scrubTargetTime = 0;
                    if (_scrubTargetTime > _mediaPlayer.Length) _scrubTargetTime = _mediaPlayer.Length;

                    TimelineSlider.Value = _scrubTargetTime;
                    CurrentTimeText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
                    ActionOverlayText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
                    
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == System.Windows.Input.Key.Up) { ChUp_Click(null!, null!); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Down) { ChDn_Click(null!, null!); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Space || e.Key == System.Windows.Input.Key.Enter)
            {
                if (ControlBar.Visibility == Visibility.Collapsed)
                {
                    PlayPause_Click(null!, null!);
                    Overlay_MouseMove(null!, null!);
                    e.Handled = true;
                }
            }
            else if (e.Key == System.Windows.Input.Key.F || e.Key == System.Windows.Input.Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            // --- NEW: Toggle CC with the 'C' key ---
            else if (e.Key == System.Windows.Input.Key.C) 
            {
                ToggleSubtitles();
                e.Handled = true;
            }
            // --- NEW: Safe Handling of Escape & Back Keys ---
            else if (e.Key == System.Windows.Input.Key.Escape || e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.BrowserBack)
            {
                if (_isFullscreen) 
                {
                    ToggleFullscreen(); // Simply drop out of fullscreen if they are in it
                }
                else 
                {
                    this.Close(); // Safely shut the player down and reveal the background UI
                }
                e.Handled = true;
            }
            // --- NEW: Safely Trap 'BrowserHome' so it doesn't background-navigate! ---
            else if (e.Key == System.Windows.Input.Key.BrowserHome)
            {
                // Force the background MainWindow to navigate Home
                if (Application.Current.MainWindow is MainWindow main && main.MainFrame.Content is Page page)
                {
                    page.NavigationService?.Navigate(new StartPage());
                }
                
                // Then immediately close the player window so the user sees the StartPage
                this.Close();
                e.Handled = true;
            }
			else if (e.Key == System.Windows.Input.Key.MediaPlayPause)
            {
                PlayPause_Click(null!, null!);
                Overlay_MouseMove(null!, null!);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.MediaStop)
            {
                if (_isFullscreen) ToggleFullscreen();
                else this.Close();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.VolumeMute)
            {
                ToggleMute();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.VolumeUp)
            {
                VolumeUp();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.VolumeDown)
            {
                VolumeDown();
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // --- NEW: Apply the scrubbed time to VLC the absolute second the key is released! ---
            if ((e.Key == System.Windows.Input.Key.Right || e.Key == System.Windows.Input.Key.Left) && _isScrubbing)
            {
                _isScrubbing = false;
                if (_mediaPlayer.IsSeekable) _mediaPlayer.Time = _scrubTargetTime;

                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 1.0, To = 0.0,
                    Duration = new Duration(TimeSpan.FromSeconds(0.75)),
                    BeginTime = TimeSpan.FromSeconds(0.5)
                };
                ActionOverlayText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                
                e.Handled = true;
            }
        }
		
		// --- NEW: Mobile Remote Scrubbing Engine ---
        public void StartRemoteScrub(string direction)
        {
            if (!_isMovieMode || ControlBar.Visibility != Visibility.Collapsed) return;
            
            _remoteScrubDirection = direction;
            if (!_isScrubbing)
            {
                _isScrubbing = true;
                _scrubTargetTime = _mediaPlayer.Time;
                ActionOverlayText.BeginAnimation(UIElement.OpacityProperty, null); 
                ActionOverlayText.Opacity = 1.0;
            }
            _remoteScrubTimer.Start();
        }

        private void RemoteScrubTimer_Tick(object? sender, EventArgs e)
        {
            long jumpAmount = (_remoteScrubDirection == "right") ? 5000 : -5000;
            _scrubTargetTime += jumpAmount;
            
            if (_scrubTargetTime < 0) _scrubTargetTime = 0;
            if (_scrubTargetTime > _mediaPlayer.Length) _scrubTargetTime = _mediaPlayer.Length;

            TimelineSlider.Value = _scrubTargetTime;
            CurrentTimeText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
            ActionOverlayText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
        }

        public void StopRemoteScrub()
        {
            if (!_isScrubbing) return;
            
            _remoteScrubTimer.Stop();
            _isScrubbing = false;
            
            if (_mediaPlayer.IsSeekable) _mediaPlayer.Time = _scrubTargetTime;

            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0, To = 0.0,
                Duration = new Duration(TimeSpan.FromSeconds(0.75)),
                BeginTime = TimeSpan.FromSeconds(0.5)
            };
            ActionOverlayText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        }
        
        private void Overlay_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.IsOpen = true;
            }
        }

        private bool _isFullscreen = false;
       
        // --- NEW: Direct Remote Control Gateway ---
        public bool HandleRemoteKey(string key)
        {
            if (key == "right" && ControlBar.Visibility == Visibility.Collapsed)
            {
                FastForward_Click(null!, null!);
                return true;
            }
            if (key == "left" && ControlBar.Visibility == Visibility.Collapsed)
            {
                Rewind_Click(null!, null!);
                return true;
            }
            if (key == "up" && !_isMovieMode && ControlBar.Visibility == Visibility.Collapsed)
            {
                ChUp_Click(null!, null!);
                return true;
            }
            if (key == "down" && !_isMovieMode && ControlBar.Visibility == Visibility.Collapsed)
            {
                ChDn_Click(null!, null!);
                return true;
            }
            if (key == "enter" && ControlBar.Visibility == Visibility.Collapsed)
            {
                PlayPause_Click(null!, null!);
                Overlay_MouseMove(null!, null!);
                return true;
            }
            
            // If the UI is open, wake it up and let the standard D-Pad logic take over!
            Overlay_MouseMove(null!, null!);
            return false; 
        }
		
		private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                this.WindowStyle = WindowStyle.None; 
                this.ResizeMode = ResizeMode.NoResize; 
                this.WindowState = WindowState.Maximized;
                this.Topmost = true; 
                _isFullscreen = true;
            }
            else
            {
                this.Topmost = false;
                
                // FIXED: Explicitly force the standard Windows title bar to reappear!
                this.WindowStyle = WindowStyle.SingleBorderWindow;
                this.ResizeMode = ResizeMode.CanResize;
                this.WindowState = WindowState.Normal;
                
                _isFullscreen = false;
            }
        }

        private void Overlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        private void PlayerWindow_Closed(object? sender, EventArgs e)
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
            
            _uiTimer?.Stop();
            _statsTimer?.Stop();
            
            if (_mediaPlayer != null)
            {
                _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
                _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged; // <-- Fixed to "-="
                _mediaPlayer.Stop();
                _mediaPlayer.Dispose();
				_liveProgressTimer?.Stop();
            }
        }
        
        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            if (_isMovieMode)
            {
                Dispatcher.Invoke(() => this.Close());
                return;
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500);

                try
                {
                    var api = new ChannelsApi();
                    var freshGuide = await api.GetGuideAsync(_baseUrl);
                    
                    var currentChannelNum = _channels![_currentIndex].Number;
                    var updatedChannelData = freshGuide.FirstOrDefault(g => g.ChannelNumber == currentChannelNum);

                    if (updatedChannelData != null && updatedChannelData.Airings != null)
                    {
                        _channels[_currentIndex].CurrentAirings = updatedChannelData.Airings;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        PlayCurrentChannel();
                        Overlay_MouseMove(null!, null!); 
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Failed to load the next show: {ex.Message}");
                        this.Close();
                    });
                }
            });
        }

        private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();
        
        public void ToggleStats()
        {
            _showStats = !_showStats;
            StatsOverlay.Visibility = _showStats ? Visibility.Visible : Visibility.Collapsed;
            
            if (_showStats) 
            {
                StatsText.Text = "⏳ Loading stats from VLC engine...";
                _statsTimer.Start();
            }
            else _statsTimer.Stop();
        }
        
        private void ToggleStats_Click(object sender, RoutedEventArgs e) => ToggleStats();

        private void StatsTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (!_showStats || _mediaPlayer == null || _mediaPlayer.Media == null) return;

                var stats = _mediaPlayer.Media.Statistics;
                string resolution = "Unknown";
                
                var tracks = _mediaPlayer.Media.Tracks;
                if (tracks != null)
                {
                    foreach (var t in tracks)
                    {
                        if (t.TrackType == TrackType.Video)
                        {
                            resolution = $"{t.Data.Video.Width}x{t.Data.Video.Height}";
                            break;
                        }
                    }
                }
                
                StatsText.Text = $"=== STATS FOR NERDS ===\n" +
                                 $"Resolution    : {resolution}\n" +
                                 $"Input Bitrate : {Math.Round(stats.InputBitrate * 8000, 0):N0} bps\n" +
                                 $"Demux Bitrate : {Math.Round(stats.DemuxBitrate * 8000, 0):N0} bps\n" +
                                 $"Read Bytes    : {stats.ReadBytes / 1024 / 1024:N2} MB\n" +
                                 $"-----------------------\n" +
                                 $"Decoded Video : {stats.DecodedVideo} blocks\n" +
                                 $"Decoded Audio : {stats.DecodedAudio} blocks\n" +
                                 $"Played Frames : {stats.DisplayedPictures}\n" +
                                 $"Lost Frames   : {stats.LostPictures}\n" +
                                 $"Audio Buffers : {stats.PlayedAudioBuffers}\n" +
                                 $"Lost Audio    : {stats.LostAudioBuffers}";
            }
            catch (Exception ex)
            {
                StatsText.Text = $"⚠️ Error reading stats:\n{ex.Message}";
            }
        }
    }
}