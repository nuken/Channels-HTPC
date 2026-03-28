using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace FeralCode
{
    public partial class PlayerWindow : Window
    {
        // --- NEW: Toggle this to false to disable logging! ---
        private bool _enableLogging;
		private CancellationTokenSource? _spoolCts;
        private string _spoolFilePath = "";
        private bool _isSpooling = false;
        private StreamMediaInput? _currentMediaInput;
		private Media? _currentMedia;
		private LiveTailStream? _liveTailStream;
		private bool _isRebuildingMedia = false;
        private double _accumulatedScrubSeconds = 0;

        private MediaPlayer _mediaPlayer;
        private string _baseUrl = ""; 
        private List<Channel>? _channels;
        private int _currentIndex;
        private DateTime _lastMouseMove = DateTime.MinValue;
        
        // --- Movie Mode Variables ---
        private bool _isMovieMode = false;
        private string _movieStreamUrl = "";
        private string _movieTitle = "";
        private string _moviePosterUrl = "";
        private List<double>? _movieCommercials;
        private UserSettings _settings;
        private bool _isDraggingTimeline = false;
        
        // --- Auto-Scrubbing Trackers ---
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
        private bool _isFullscreen = false;
        private bool _isWaitingToBuffer = false;
        private bool _isWaitingForCast = false;
        private DispatcherTimer? _tuneTimeoutTimer;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        private const byte VK_LWIN = 0x5B; 
        private const byte VK_K = 0x4B;    
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // --- NEW: Local Logger Helper ---
        private void LogDebug(string msg)
        {
            if (_enableLogging) AppLogger.Log(msg);
        }

        private void CastButton_Click(object sender, RoutedEventArgs e)
        {
            LogDebug("UI Action: CastButton_Click triggered.");
            keybd_event(VK_LWIN, 0, 0, 0);
            keybd_event(VK_K, 0, 0, 0);
            keybd_event(VK_K, 0, KEYEVENTF_KEYUP, 0);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
            
            Overlay_MouseMove(null!, null!);
            if (!_isWaitingForCast)
            {
                _isWaitingForCast = true;
                SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            }
        }
		
		private async Task SpoolStreamAsync(string streamUrl, string filePath, CancellationToken token)
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromHours(12); 

                using var response = await client.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                _isSpooling = true; // Set to true right as download starts
                using var networkStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);

                byte[] buffer = new byte[81920]; 
                int bytesRead;

                while ((bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                    await fileStream.FlushAsync(token); // CRITICAL FIX: Force OS to write to disk instantly
                }
            }
            catch (OperationCanceledException)
            {
                LogDebug("SpoolStreamAsync: Download canceled via CancellationToken.");
            }
            catch (Exception ex)
            {
                LogDebug($"SpoolStreamAsync Error: {ex.Message}");
            }
            finally
            {
                _isSpooling = false;
            }
        }

        private void CleanupSpooler()
        {
            _spoolCts?.Cancel();
            _spoolCts?.Dispose();
            _spoolCts = null;

            // Grab references to the active objects
            var oldInput = _currentMediaInput;
            var oldMedia = _currentMedia;
            var oldStream = _liveTailStream;

            // Clear the active pointers so the app can move on
            _currentMediaInput = null;
            _currentMedia = null;
            _liveTailStream = null;

            // --- FIX: Safely spin down unmanaged threads ---
            Task.Run(async () => 
            {
                // Give VLC's native C-threads plenty of time to detach from the delegates
                await Task.Delay(3000); 
                
                oldMedia?.Dispose();
                oldInput?.Dispose();
                oldStream?.Dispose();
            });

            if (!string.IsNullOrEmpty(_spoolFilePath) && File.Exists(_spoolFilePath))
            {
                try
                {
                    File.Delete(_spoolFilePath);
                    LogDebug($"CleanupSpooler: Deleted temp file {_spoolFilePath}");
                }
                catch (Exception ex)
                {
                    LogDebug($"CleanupSpooler: Could not delete temp file. {ex.Message}");
                }
            }
        }
		
		private async Task PerformSafeSeek(double seconds)
        {
            if (_liveTailStream == null || _isRebuildingMedia) return;
            
            _isRebuildingMedia = true;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "Seeking...";

            try
            {
                // 1. Stop playback to release the imem native pointers cleanly
                _mediaPlayer.Stop();
                
                // Grab references to the old pointers
                var oldInput = _currentMediaInput;
                var oldMedia = _currentMedia;

                // 2. Wait slightly for VLC to spin down its native C-threads
                await Task.Delay(150); 

                // 3. Move the stream read head while no one is reading it
                _liveTailStream.ForceSeek(seconds);

                // 4. FIX: Delayed Disposal for the old wrappers!
                _ = Task.Run(async () => 
                {
                    await Task.Delay(3000);
                    oldMedia?.Dispose();
                    oldInput?.Dispose();
                });

                // 5. Re-wrap the existing stream in fresh Media to reset the PCR clock
                _currentMediaInput = new StreamMediaInput(_liveTailStream);
                _currentMedia = new Media(MainWindow.SharedLibVLC, _currentMediaInput);
                
                _currentMedia.AddOption(":file-caching=1000"); 
                _currentMedia.AddOption(":demux=ts");
                _currentMedia.AddOption(":avcodec-hw=none");
                _currentMedia.AddOption(":no-spu");
                _currentMedia.AddOption(":no-sub-autodetect-file");
                
                // NEW: Tell VLC not to aggressively drop frames when it encounters the TS jump
                _currentMedia.AddOption(":clock-jitter=0"); 

                _mediaPlayer.Play(_currentMedia);
            }
            finally
            {
                _isRebuildingMedia = false;
            }
        }
		
		private async Task ForceClockResync()
        {
            if (_mediaPlayer != null)
            {
                bool wasPlaying = _mediaPlayer.IsPlaying;
                
                // Briefly toggle pause to flush the A/V pipeline
                if (wasPlaying) _mediaPlayer.Pause();
                
                // Nudging the rate forces VLC's PCR clock to instantly reset to the new TS packets
                _mediaPlayer.SetRate(1.05f); 
                await Task.Delay(50);
                _mediaPlayer.SetRate(1.0f);

                if (wasPlaying) _mediaPlayer.Play();
            }
        }

        // --- ORIGINAL CONSTRUCTOR: Live TV Mode ---
        public PlayerWindow(string baseUrl, List<Channel> channels, int startIndex)
        {
            LogDebug($"PlayerWindow Constructor (Live TV): Initializing for baseUrl {baseUrl}, startIndex {startIndex}");
            InitializeComponent();
            this.Loaded += (s, e) =>
            {
                this.Activate();
                this.Topmost = true; 
                
                var settings = SettingsManager.Load();
                if (!settings.StartPlayersFullscreen)
                {
                    this.Topmost = false; 
                }
                
                this.Focus();
            };
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
            _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
            
            _tuneTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _tuneTimeoutTimer.Tick += TuneTimeoutTimer_Tick;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += StatsTimer_Tick;

            this.Loaded += PlayerWindow_Loaded;
            this.Closed += PlayerWindow_Closed;
            this.PreviewKeyUp += Window_PreviewKeyUp;
            this.PreviewKeyDown += Window_PreviewKeyDown;
            
            if (_settings.StartPlayersFullscreen)
            {
                this.WindowState = WindowState.Maximized;
                this.WindowStyle = WindowStyle.None;
                this.ResizeMode = ResizeMode.NoResize;
                this.Topmost = true; 
                _isFullscreen = true; 
            }
        }
        
        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            LogDebug("Event: SystemEvents_DisplaySettingsChanged fired (Cast connected)");
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _isWaitingForCast = false;

            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2000); 

                if (!_isFullscreen)
                {
                    ToggleFullscreen();
                }
            });
        }
        
        private void LiveProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (_channels == null || !_channels.Any()) return;

            var currentChannel = _channels[_currentIndex];
            var currentAiring = currentChannel.CurrentAirings?.FirstOrDefault(a => a.IsAiringNow);

            if (currentAiring != null && currentAiring.Duration.HasValue && !_isDraggingTimeline)
            {
                double elapsedMs = (DateTime.Now - currentAiring.StartTime).TotalMilliseconds;
                double maxMs = currentAiring.Duration.Value * 1000;

                if (elapsedMs > maxMs) elapsedMs = maxMs;
                if (elapsedMs < 0) elapsedMs = 0;

                TimelineSlider.Value = elapsedMs;
                CurrentTimeText.Text = TimeSpan.FromMilliseconds(elapsedMs).ToString(@"h\:mm\:ss");
            }
        }

        // --- NEW CONSTRUCTOR: Movie Mode! ---
        public PlayerWindow(string streamUrl, string movieTitle, string posterUrl, List<double>? commercials)
        {
            LogDebug($"PlayerWindow Constructor (Movie Mode): Initializing for title '{movieTitle}'");
            InitializeComponent();
            this.Loaded += (s, e) =>
            {
                this.Activate();
                this.Topmost = true; 
                
                var settings = SettingsManager.Load();
                if (!settings.StartPlayersFullscreen)
                {
                    this.Topmost = false; 
                }
                
                this.Focus();
            };
            _settings = SettingsManager.Load();
			_enableLogging = _settings.EnableDebugLogging;
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
            _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
            
            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            _mediaPlayer.LengthChanged += MediaPlayer_LengthChanged;
            
            _tuneTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _tuneTimeoutTimer.Tick += TuneTimeoutTimer_Tick;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsTimer.Tick += StatsTimer_Tick;

            this.Loaded += PlayerWindow_Loaded;
            this.Closed += PlayerWindow_Closed;
            this.PreviewKeyUp += Window_PreviewKeyUp;
            this.PreviewKeyDown += Window_PreviewKeyDown;
            
            if (_settings.StartPlayersFullscreen)
            {
                this.WindowState = WindowState.Maximized;
                this.WindowStyle = WindowStyle.None;
                this.ResizeMode = ResizeMode.NoResize;
                this.Topmost = true; 
                _isFullscreen = true; 
            }
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (!_isMovieMode) return; 

            long currentTime = e.Time;

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

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isDraggingTimeline && !_isScrubbing) 
                {
                    TimelineSlider.Value = currentTime;
                    CurrentTimeText.Text = TimeSpan.FromMilliseconds(currentTime).ToString(@"h\:mm\:ss");
                }
            }));
        }
        
        private void MediaPlayer_LengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
        {
            if (!_isMovieMode) return; 

            long safeLength = e.Length;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                TimelineSlider.Maximum = safeLength;
                TotalTimeText.Text = TimeSpan.FromMilliseconds(safeLength).ToString(@"h\:mm\:ss");
            }));
        }
        
        private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e)
        {
            float safeCache = e.Cache; 

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (safeCache < 100)
                {
                    if (LoadingOverlay.Visibility != Visibility.Visible && !_isWaitingToBuffer)
                    {
                        _isWaitingToBuffer = true;
                        
                        await Task.Delay(750);
                        
                        if (_isWaitingToBuffer)
                        {
                            LoadingOverlay.Visibility = Visibility.Visible;
                        }
                    }

                    if (LoadingOverlay.Visibility == Visibility.Visible)
                    {
                        LoadingText.Text = $"Buffering... {(int)safeCache}%";
                    }
                }
                else
                {
                    _isWaitingToBuffer = false;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                }
            }));
        }
        
        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            LogDebug("VLC CALLBACK: MediaPlayer_Playing Fired!");
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                _tuneTimeoutTimer?.Stop();
                await Task.Delay(300);

                _isWaitingToBuffer = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }));
        }

        private void TuneTimeoutTimer_Tick(object? sender, EventArgs e)
        {
            LogDebug("TuneTimeoutTimer_Tick: Fired! 15 seconds elapsed without playback locking on.");
            _tuneTimeoutTimer?.Stop();
            
            if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Stop(); 
            }

            LoadingOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show("Tuning timed out. The OTA signal might be too weak to lock onto.", 
                            "Weak Signal", MessageBoxButton.OK, MessageBoxImage.Warning);
            
            this.Close(); 
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            LogDebug("VLC CALLBACK: MediaPlayer_EncounteredError Fired!");
            Dispatcher.InvokeAsync(() =>
            {
                _tuneTimeoutTimer?.Stop();
                LoadingOverlay.Visibility = Visibility.Collapsed;
                
                MessageBox.Show("A playback error occurred. The stream may be unavailable or the signal was lost.", 
                                "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                
                this.Close(); 
            });
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
        
        public void ToggleClosedCaptions()
        {
            if (_mediaPlayer == null) return;

            if (_mediaPlayer.Spu == -1)
            {
                var firstCcTrack = _mediaPlayer.SpuDescription.FirstOrDefault(track => track.Id > -1);
                
                if (firstCcTrack.Id > -1)
                {
                    _mediaPlayer.SetSpu(firstCcTrack.Id);
                }
            }
            else
            {
                _mediaPlayer.SetSpu(-1);
            }
        }
        
        public void ToggleSubtitles()
        {
            if (_mediaPlayer == null) return;

            var spus = _mediaPlayer.SpuDescription;
            
            if (spus == null || spus.Length <= 1)
            {
                ShowActionOverlay("🚫 No CC Available");
                return;
            }

            int currentId = _mediaPlayer.Spu;
            
            int currentIndex = 0;
            for (int i = 0; i < spus.Length; i++)
            {
                if (spus[i].Id == currentId)
                {
                    currentIndex = i;
                    break;
                }
            }
            
            int nextIndex = (currentIndex + 1) % spus.Length;
            var nextSpu = spus[nextIndex];
            
            _mediaPlayer.SetSpu(nextSpu.Id);

            string statusText = nextSpu.Id == -1 ? "CC: Off" : $"CC: {nextSpu.Name}";
            ShowActionOverlay(statusText);
        }

        private void BtnCC_Click(object sender, RoutedEventArgs e)
        {
            OpenSubtitlesMenu();
        }

        private void OpenSubtitlesMenu()
        {
            if (_mediaPlayer == null) return;

            var spus = _mediaPlayer.SpuDescription;
            if (spus == null || spus.Length <= 1)
            {
                ShowActionOverlay("🚫 No CC Available");
                return;
            }

            ContextMenu ccMenu = new ContextMenu();
            
            ccMenu.Background = (System.Windows.Media.Brush)Application.Current.FindResource("PanelBackground");
            ccMenu.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary");
            ccMenu.BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush");
            ccMenu.BorderThickness = new Thickness(2);

            int currentId = _mediaPlayer.Spu;

            foreach (var spu in spus)
            {
                MenuItem item = new MenuItem();
                
                item.Header = spu.Id == -1 ? "Off" : $"Track: {spu.Name}";
                item.Tag = spu.Id;
                item.FontSize = 16;
                item.Padding = new Thickness(10, 5, 10, 5);
                
                if (spu.Id == currentId)
                {
                    item.IsChecked = true; 
                    item.FontWeight = FontWeights.Bold;
                }

                item.Click += (s, args) =>
                {
                    int selectedId = (int)((MenuItem)s!).Tag;
                    _mediaPlayer.SetSpu(selectedId);
                    
                    string statusText = selectedId == -1 ? "CC: Off" : $"CC: {spu.Name}";
                    ShowActionOverlay(statusText);
                };

                ccMenu.Items.Add(item);
            }

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
            LogDebug("PlayMovie: Method invoked.");
            UiChannelNumber.Text = "🎬 MOVIE";
            UiShowTitle.Text = _movieTitle;
            
            if (!string.IsNullOrWhiteSpace(_moviePosterUrl))
                UiChannelLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(_moviePosterUrl));
            else
                UiChannelLogo.Source = null;
            
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingText.Text = "Connecting...";

            _tuneTimeoutTimer?.Stop(); 
            _tuneTimeoutTimer?.Start();

            var media = new Media(MainWindow.SharedLibVLC, new Uri(_movieStreamUrl));
            media.AddOption(":network-caching=2000");

            _mediaPlayer.Play(media);
            LogDebug("PlayMovie: _mediaPlayer.Play() called.");
        }
        
        private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingTimeline = true;
        }

        private void TimelineSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
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

        private void ShowActionOverlay(string text)
        {
            ActionOverlayText.Text = text;
            ActionOverlayText.Opacity = 1.0;
            
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0, To = 0.0,
                Duration = new Duration(TimeSpan.FromSeconds(0.75)),
                BeginTime = TimeSpan.FromSeconds(0.5) 
            };
            ActionOverlayText.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            
            Overlay_MouseMove(null!, null!); 
        }

        private async void PlayCurrentChannel()
        {
            try
            {
                LogDebug("PlayCurrentChannel: Method invoked.");
                if (_channels == null || !_channels.Any()) return;

                var currentChannel = _channels[_currentIndex];
                var currentAiring = currentChannel.CurrentAirings?.FirstOrDefault(a => a.IsAiringNow);
                
                UiChannelNumber.Text = $"CH {currentChannel.Number}";
                UiShowTitle.Text = currentAiring != null ? currentAiring.DisplayTitle : currentChannel.Name;
                
                // --- THE FIX: Safely load the image ---
                try
                {
                    if (!string.IsNullOrWhiteSpace(currentChannel.ImageUrl))
                    {
                        // Ignore case issues, ensure it's absolute, and catch 404s
                        UiChannelLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(currentChannel.ImageUrl, UriKind.Absolute));
                    }
                    else
                    {
                        UiChannelLogo.Source = null;
                    }
                }
                catch (Exception imgEx)
                {
                    LogDebug($"Warning: Failed to load logo for CH {currentChannel.Number}. Error: {imgEx.Message}");
                    UiChannelLogo.Source = null; // Fallback gracefully
                }
                
                string streamUrl = "";
                int offsetSeconds = 0;

                if (currentChannel.Id != null && currentChannel.Id.StartsWith("virtual", StringComparison.OrdinalIgnoreCase))
                {
                    LogDebug("PlayCurrentChannel: Virtual channel detected.");
                    if (currentAiring != null && !string.IsNullOrWhiteSpace(currentAiring.Source))
                    {
                        string fileId = currentAiring.Source.Split('/').Last(); 
                        streamUrl = $"{_baseUrl}/dvr/files/{fileId}/hls/master.m3u8";
                        
                        offsetSeconds = (int)(DateTime.Now - currentAiring.StartTime).TotalSeconds;
                        if (offsetSeconds < 0) offsetSeconds = 0;
                    }
                    else
                    {
                        LogDebug("PlayCurrentChannel: Virtual channel missing source media.");
                        MessageBox.Show("No active media is scheduled for this Virtual Channel right now.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        this.Close();
                        return;
                    }
                }
                else
                {
                    var settings = SettingsManager.Load();
                    string audioCodec = "aac"; 

                    if (!settings.ForceAacAudio)
                    {
                        audioCodec = "copy";
                        if (double.TryParse(currentChannel.Number, out double chNum) && chNum >= 100 && chNum < 200)
                            audioCodec = "aac";
                    }

                    streamUrl = $"{_baseUrl.TrimEnd('/')}/devices/ANY/channels/{currentChannel.Number}/stream.mpg?format=ts&vcodec=copy&acodec={audioCodec}";
                }

                LogDebug($"PlayCurrentChannel: Final Stream URL: {streamUrl}");

                if (_mediaPlayer.IsPlaying) 
                {
                    LogDebug("PlayCurrentChannel: Stopping previously playing _mediaPlayer.");
                    _mediaPlayer.Stop();
                    _mediaPlayer.Media = null; // Detach it, but do NOT dispose it here
                }

                // CleanupSpooler will now handle the safe, delayed 3-second disposal
                CleanupSpooler();

                bool isVirtualChannel = currentChannel.Id != null && currentChannel.Id.StartsWith("virtual", StringComparison.OrdinalIgnoreCase);

                if (_settings.EnableTimeShiftBuffer && !isVirtualChannel)
                {
                    LogDebug("PlayCurrentChannel: Time-Shift enabled. Starting disk spooler.");
                    _spoolCts = new CancellationTokenSource();
                    _spoolFilePath = Path.Combine(Path.GetTempPath(), $"feral_spool_{Guid.NewGuid():N}.ts");
                    
                    // 1. Fire and forget the background download task
                    _ = Task.Run(() => SpoolStreamAsync(streamUrl, _spoolFilePath, _spoolCts.Token));

                    LoadingOverlay.Visibility = Visibility.Visible;
                    LoadingText.Text = "Buffering Live Stream...";

                    // 2. CRITICAL FIX: Wait until at least 64KB is written to disk so VLC can sniff the format
                    int waitAttempts = 0;
                    while (waitAttempts < 50) // Max 5 seconds wait
                    {
                        if (File.Exists(_spoolFilePath))
                        {
                            try 
                            { 
                                if (new FileInfo(_spoolFilePath).Length > 64000) break; 
                            } 
                            catch { } // Briefly ignore OS file lock exceptions
                        }
                        await Task.Delay(100);
                        waitAttempts++;
                    }

                    // 3. Wrap the file and keep references alive
                    // FIX: Use the class-level variable here so the GC doesn't delete it
                    _liveTailStream = new LiveTailStream(_spoolFilePath, () => _isSpooling);
                    _currentMediaInput = new StreamMediaInput(_liveTailStream);
                    
                    _currentMedia = new Media(MainWindow.SharedLibVLC, _currentMediaInput);
                    _currentMedia.AddOption(":file-caching=2000"); 
                    _currentMedia.AddOption(":demux=ts");
                }
                else
                {
                    if (isVirtualChannel) LogDebug("PlayCurrentChannel: Virtual Channel detected. Bypassing spooler.");
                    else LogDebug("PlayCurrentChannel: Time-Shift disabled. Streaming directly.");
                    
                    _currentMedia = new Media(MainWindow.SharedLibVLC, new Uri(streamUrl));
                    _currentMedia.AddOption(":network-caching=2000");
                    _currentMedia.AddOption(":live-caching=2000");
                }

                _currentMedia.AddOption(":avcodec-hw=none");
                _currentMedia.AddOption(":no-spu");
                _currentMedia.AddOption(":no-sub-autodetect-file");

                if (offsetSeconds > 0)
                {
                    _currentMedia.AddOption($":start-time={offsetSeconds}");
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

                LogDebug("PlayCurrentChannel: Starting 15s TuneTimeoutTimer and calling _mediaPlayer.Play().");
                _tuneTimeoutTimer?.Stop();
                _tuneTimeoutTimer?.Start();

                // Play using our safely retained media object
                _mediaPlayer.Play(_currentMedia);
                LogDebug("PlayCurrentChannel: _mediaPlayer.Play() completed.");
            }
            catch (Exception ex)
            {
                LogDebug($"FATAL C# ERROR in PlayCurrentChannel: {ex.Message}\n{ex.StackTrace}");
                MessageBox.Show($"Failed to load channel: {ex.Message}", "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
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

        private async void Rewind_Click(object sender, RoutedEventArgs e)
        {
            if (_isMovieMode && _mediaPlayer.IsSeekable) 
            {
                _mediaPlayer.Time -= 10000; 
                ShowActionOverlay("⏪ -10s");
            }
            else if (!_isMovieMode && _liveTailStream != null)
            {
                ShowActionOverlay("⏪ -10s");
                await PerformSafeSeek(-10.0);
            }
        }

        private async void FastForward_Click(object sender, RoutedEventArgs e)
        {
            if (_isMovieMode && _mediaPlayer.IsSeekable) 
            {
                _mediaPlayer.Time += 30000;
                ShowActionOverlay("⏩ +30s");
            }
            else if (!_isMovieMode && _liveTailStream != null)
            {
                ShowActionOverlay("⏩ +30s");
                await PerformSafeSeek(30.0);
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
            else if (e.Key == System.Windows.Input.Key.C) 
            {
                ToggleSubtitles();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape || e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.BrowserBack)
            {
                if (_isFullscreen) 
                {
                    ToggleFullscreen(); 
                }
                else 
                {
                    this.Close(); 
                }
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.BrowserHome)
            {
                if (Application.Current.MainWindow is MainWindow main && main.MainFrame.Content is Page page)
                {
                    page.NavigationService?.Navigate(new StartPage());
                }
                
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
        
        public void StartRemoteScrub(string direction)
        {
            if (ControlBar.Visibility != Visibility.Collapsed) return;
            
            _remoteScrubDirection = direction;
            if (!_isScrubbing)
            {
                _isScrubbing = true;
                _scrubTargetTime = _mediaPlayer.Time; 
                _accumulatedScrubSeconds = 0; // Reset live TV accumulation
                
                ActionOverlayText.BeginAnimation(UIElement.OpacityProperty, null); 
                ActionOverlayText.Opacity = 1.0;
            }
            _remoteScrubTimer.Start();
        }

        private void RemoteScrubTimer_Tick(object? sender, EventArgs e)
        {
            if (_isMovieMode)
            {
                long jumpAmount = (_remoteScrubDirection == "right") ? 5000 : -5000;
                _scrubTargetTime += jumpAmount;
                
                if (_scrubTargetTime < 0) _scrubTargetTime = 0;
                if (_scrubTargetTime > _mediaPlayer.Length) _scrubTargetTime = _mediaPlayer.Length;

                TimelineSlider.Value = _scrubTargetTime;
                CurrentTimeText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
                ActionOverlayText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
            }
            else
            {
                // For Live TV, just accumulate the jump and show a relative indicator
                double jumpSec = (_remoteScrubDirection == "right") ? 5.0 : -5.0;
                _accumulatedScrubSeconds += jumpSec;
                
                string sign = _accumulatedScrubSeconds > 0 ? "+" : "";
                ActionOverlayText.Text = $"Scrubbing: {sign}{_accumulatedScrubSeconds}s";
            }
        }

        public async void StopRemoteScrub()
        {
            if (!_isScrubbing) return;
            
            _remoteScrubTimer.Stop();
            _isScrubbing = false;
            
            if (_isMovieMode && _mediaPlayer.IsSeekable) 
            {
                _mediaPlayer.Time = _scrubTargetTime;
            }
            else if (!_isMovieMode && _liveTailStream != null && _accumulatedScrubSeconds != 0)
            {
                // Execute ONE safe jump now that the user has let go of the button
                await PerformSafeSeek(_accumulatedScrubSeconds);
            }

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
            LogDebug("PlayerWindow_Closed: Cleaning up resources.");
            System.Windows.Input.Mouse.OverrideCursor = null;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            
            _uiTimer?.Stop();
            _statsTimer?.Stop();
            _tuneTimeoutTimer?.Stop();
            _liveProgressTimer?.Stop();
            
            if (_mediaPlayer != null)
            {
                _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
                _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged; 
                _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;

                if (_mediaPlayer.IsPlaying) 
                {
                    LogDebug("PlayerWindow_Closed: Stopping _mediaPlayer.");
                    _mediaPlayer.Stop();
                }
                LogDebug("PlayerWindow_Closed: Disposing _mediaPlayer.");
                _mediaPlayer.Dispose();
            }
			CleanupSpooler();
        }
        
        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            LogDebug("VLC CALLBACK: MediaPlayer_EndReached Fired!");
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
                        LogDebug($"Error transitioning to next program: {ex.Message}");
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