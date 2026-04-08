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
		private System.Diagnostics.Process? _ffmpegProcess;
                
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
		private int _tuneRetryCount = 0;
        private const int MAX_TUNE_RETRIES = 3;

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
		
		private int GetAvailablePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
		
		private async Task SpoolStreamAsync(string streamUrl, string filePath, CancellationToken token)
        {
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromHours(12); 

                using var response = await client.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                _isSpooling = true; 
                using var networkStream = await response.Content.ReadAsStreamAsync();
                
                // Restored to a sensible 64KB buffer
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 65536, useAsync: true);

                byte[] buffer = new byte[81920]; 
                int bytesRead;
                
                var sw = System.Diagnostics.Stopwatch.StartNew();

                while ((bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    await fileStream.WriteAsync(buffer, 0, bytesRead, token);
                    
                    // CRITICAL FIX: Flush every 200ms. This updates the NTFS file size 
                    // so the reader thread can instantly see and play the new video bytes.
                    if (sw.ElapsedMilliseconds > 200)
                    {
                        await fileStream.FlushAsync(token);
                        sw.Restart();
                    }
                }
                
                await fileStream.FlushAsync(token);
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
            StopFfmpegProxy();
            _spoolCts?.Cancel();

            // Grab references to the active objects
            var oldInput = _currentMediaInput;
            var oldMedia = _currentMedia;
            var oldStream = _liveTailStream;
            var oldCts = _spoolCts;
            var tempFilePath = _spoolFilePath; // Capture local path

            // Clear the active pointers so the app can move on
            _currentMediaInput = null;
            _currentMedia = null;
            _liveTailStream = null;
            _spoolCts = null;

            // --- FIX: Safely spin down threads AND delete file in background ---
            _ = Task.Run(async () => 
            {
                // Give VLC's native C-threads plenty of time to detach from the file
                await Task.Delay(3000); 
                
                oldMedia?.Dispose();
                oldInput?.Dispose();
                oldStream?.Dispose();
                oldCts?.Dispose();

                // NOW safely delete the temp file after all handles are released
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try
                    {
                        File.Delete(tempFilePath);
                        LogDebug($"CleanupSpooler: Deleted temp file {tempFilePath}");
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"CleanupSpooler: Could not delete temp file. {ex.Message}");
                    }
                }
            });
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
                
                _currentMedia.AddOption(":freetype-rel-fontsize=12");
                _currentMedia.AddOption(":deinterlace=1");             
                _currentMedia.AddOption(":deinterlace-mode=yadif");    
                
                // --- TIER 1: GLOBAL LENIENCY FOR TIMESHIFT SEEKS ---
                _currentMedia.AddOption(":clock-jitter=0");      
                _currentMedia.AddOption(":no-ts-cc-check");         
                _currentMedia.AddOption(":no-drop-late-frames");    
                _currentMedia.AddOption(":no-skip-frames");         
                _currentMedia.AddOption(":no-avcodec-hurry-up"); 

                // --- TIER 2: OTA NUCLEAR OPTION FOR TIMESHIFT SEEKS ---
                // We define what an OTA channel is right here so this method understands it!
                bool isOtaChannel = _channels != null && _channels[_currentIndex].Number != null && _channels[_currentIndex].Number.Contains(".");

                if (isOtaChannel)
                {
                    _currentMedia.AddOption(":no-ts-trust-pcr");       
                    _currentMedia.AddOption(":no-ts-seek-percent");    
                    _currentMedia.AddOption(":clock-synchro=0");
                }

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
			_enableLogging = _settings.EnableDebugLogging;
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
            
            _tuneTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
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
		
		// Update the method signature to accept the offset
        private async Task<string> StartFfmpegProxyAsync(string sourceUrl, int offsetSeconds, string targetAudioCodec)
        {
            // Give VLC's native thread 150ms to gracefully close the HTTP socket 
            // before we forcefully kill the old FFmpeg process feeding it.
            await Task.Delay(150);
            StopFfmpegProxy(); 

            int dynamicPort = GetAvailablePort();
            string ffmpegBindUrl = $"http://127.0.0.1:{dynamicPort}";
            string clientUrl = $"http://127.0.0.1:{dynamicPort}/stream.ts";

            // --- FIX: Point to the new AppData directory instead of Program Files ---
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localFfmpegPath = System.IO.Path.Combine(appData, "FeralHTPC", "ffmpeg", "ffmpeg.exe");
            string audioArgs = targetAudioCodec == "copy" ? "-c:a copy" : "-c:a aac -ac 2";
            // Fallback to global PATH if the auto-download somehow failed
            string targetExecutable = System.IO.File.Exists(localFfmpegPath) ? localFfmpegPath : "ffmpeg";

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = targetExecutable, 
                
                // CLEANED UP: We removed the aggressive -r 30 and -fps_mode commands. 
                // This is now a lightweight, hyper-fast proxy strictly for normalizing 
                // standard Live TV audio/video codecs.
                Arguments = $"-nostdin -hide_banner -loglevel warning -analyzeduration 3000000 -probesize 3000000 -fflags +genpts+igndts+discardcorrupt -i \"{sourceUrl}\" -map 0:V:0? -map 0:a:0? -map 0:s? -vf yadif -c:v libx264 -preset ultrafast -tune zerolatency {audioArgs} -c:s copy -ignore_unknown -max_muxing_queue_size 1024 -f mpegts -listen 1 {ffmpegBindUrl}",
                
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            _ffmpegProcess = new System.Diagnostics.Process { StartInfo = startInfo };
            _ffmpegProcess.EnableRaisingEvents = true; 
            
            _ffmpegProcess.ErrorDataReceived += (s, e) => 
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogDebug($"[FFMPEG] {e.Data}");
            };

            _ffmpegProcess.Exited += (s, e) =>
            {
                if (_ffmpegProcess != null && _ffmpegProcess.ExitCode != 0)
                {
                    LogDebug($"[FFMPEG FATAL] Process exited unexpectedly with code: {_ffmpegProcess.ExitCode}");
                }
            };

            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine(); 
            
            LogDebug($"Started FFmpeg local proxy. Waiting for port {dynamicPort} to bind...");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool isBound = false;
            
            while (sw.ElapsedMilliseconds < 20000) 
            {
                if (_ffmpegProcess == null || _ffmpegProcess.HasExited)
                {
                    LogDebug("FFmpeg exited before port bind.");
                    break;
                }

                var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                var listeners = properties.GetActiveTcpListeners();
                
                if (listeners.Any(l => l.Port == dynamicPort))
                {
                    isBound = true;
                    break;
                }
                await Task.Delay(250);
            }

            if (!isBound)
            {
                LogDebug("FFmpeg proxy failed to bind within the timeout period.");
                StopFfmpegProxy();
                return "";
            }

            LogDebug($"FFmpeg successfully bound to port {dynamicPort}. Proceeding to playback.");
            return clientUrl;
        }
		
        private void StopFfmpegProxy()
        {
            if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
            {
                try 
                { 
                    _ffmpegProcess.Kill(); 
                    LogDebug("Killed active FFmpeg proxy process.");
                } 
                catch { }
                _ffmpegProcess.Dispose();
            }
            _ffmpegProcess = null;
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
            
            _tuneTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
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
                _tuneRetryCount = 0; // --- NEW: Reset retries on successful play! ---
                _tuneTimeoutTimer?.Stop();
                await Task.Delay(300);

                _isWaitingToBuffer = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }));
        }

        private void TuneTimeoutTimer_Tick(object? sender, EventArgs e)
        {
            LogDebug("TuneTimeoutTimer_Tick: Fired! Time elapsed without playback locking on.");
            _tuneTimeoutTimer?.Stop();
            
            if (_tuneRetryCount < MAX_TUNE_RETRIES)
            {
                _tuneRetryCount++;
                LogDebug($"Timeout. Retrying... Attempt {_tuneRetryCount} of {MAX_TUNE_RETRIES}");
                LoadingText.Text = $"Tuning timeout. Retrying... ({_tuneRetryCount}/{MAX_TUNE_RETRIES})";
                PlayCurrentChannel();
            }
            else
            {
                if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Stop(); 
                }

                LoadingOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show("Tuning timed out. The OTA signal might be too weak to lock onto.", 
                                "Weak Signal", MessageBoxButton.OK, MessageBoxImage.Warning);
                this.Close(); 
            }
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            LogDebug("VLC CALLBACK: MediaPlayer_EncounteredError Fired!");
            Dispatcher.InvokeAsync(async () =>
            {
                _tuneTimeoutTimer?.Stop();

                if (_tuneRetryCount < MAX_TUNE_RETRIES)
                {
                    _tuneRetryCount++;
                    LogDebug($"Stream error. Retrying... Attempt {_tuneRetryCount} of {MAX_TUNE_RETRIES}");
                    LoadingText.Text = $"Connection lost. Retrying... ({_tuneRetryCount}/{MAX_TUNE_RETRIES})";
                    
                    await Task.Delay(2000); // Give the server a moment to recover
                    PlayCurrentChannel(); // Re-initialize the stream
                }
                else
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    MessageBox.Show("A playback error occurred. The stream may be unavailable or the signal was lost after multiple attempts.", 
                                    "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    this.Close(); 
                }
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
    // --- NEW: Safety rails to restore the cursor if it leaves the player! ---
    this.MouseLeave += (s, ev) => System.Windows.Input.Mouse.OverrideCursor = null;
    this.Deactivated += (s, ev) => System.Windows.Input.Mouse.OverrideCursor = null;

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
			media.AddOption(":freetype-rel-fontsize=12");

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
                
                try
                {
                    if (!string.IsNullOrWhiteSpace(currentChannel.ImageUrl))
                        UiChannelLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(currentChannel.ImageUrl, UriKind.Absolute));
                    else
                        UiChannelLogo.Source = null;
                }
                catch { UiChannelLogo.Source = null; }
                
                string streamUrl = "";
                int offsetSeconds = 0;
                // Use the proxy if the global setting is checked OR if this specific channel was right-clicked and flagged
                bool useProxy = _settings.ForceLocalTranscode || 
                (_settings.ForcedFfmpegChannels != null && _settings.ForcedFfmpegChannels.Contains(currentChannel.Number!));
                string audioCodec = "copy"; 

                bool isVirtualChannel = currentChannel.Id != null && currentChannel.Id.StartsWith("virtual", StringComparison.OrdinalIgnoreCase);

                if (isVirtualChannel)
                {
                    LogDebug("PlayCurrentChannel: Virtual channel detected.");
                    if (currentAiring != null && !string.IsNullOrWhiteSpace(currentAiring.Source))
                    {
                        string fileId = currentAiring.Source.Split('/').Last(); 
                        streamUrl = $"{_baseUrl.TrimEnd('/')}/dvr/files/{fileId}/hls/stream.m3u8";
                        
                        offsetSeconds = (int)(DateTime.Now - currentAiring.StartTime).TotalSeconds;
                        if (offsetSeconds < 0) offsetSeconds = 0;
                        
                        useProxy = false; 
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
                    // --- ABANDONED HLS FALLBACK. REVERTED TO RAW TS FOR ALL LIVE CHANNELS ---
                    var settings = SettingsManager.Load();
                    audioCodec = "aac"; 

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
                    _mediaPlayer.Stop();
                    _mediaPlayer.Media = null; 
                }

                CleanupSpooler();

                string activeStreamUrl = streamUrl;

                if (useProxy)
                {
                    LogDebug("Routing stream through FFmpeg proxy to normalize formats.");
                    activeStreamUrl = await StartFfmpegProxyAsync(streamUrl, offsetSeconds, audioCodec);
                    
                    if (string.IsNullOrEmpty(activeStreamUrl))
                    {
                        MessageBox.Show("Failed to start the proxy stream. The signal may be dead or took too long to load.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        this.Close();
                        return;
                    }
                }

                if (_settings.EnableTimeShiftBuffer && !isVirtualChannel)
                {
                    LogDebug("PlayCurrentChannel: Time-Shift enabled. Starting disk spooler.");
                    _spoolCts = new CancellationTokenSource();
                    _spoolFilePath = Path.Combine(Path.GetTempPath(), $"feral_spool_{Guid.NewGuid():N}.ts");
                    
                    _ = Task.Run(() => SpoolStreamAsync(activeStreamUrl, _spoolFilePath, _spoolCts.Token));

                    LoadingOverlay.Visibility = Visibility.Visible;
                    LoadingText.Text = "Buffering Live Stream...";

                    int waitAttempts = 0;
                    while (waitAttempts < 150) 
                    {
                        if (File.Exists(_spoolFilePath))
                        {
                            try { if (new FileInfo(_spoolFilePath).Length > 2000000) break; } 
                            catch { } 
                        }
                        await Task.Delay(100);
                        waitAttempts++;
                    }

                    _liveTailStream = new LiveTailStream(_spoolFilePath, () => _isSpooling);
                    _currentMediaInput = new StreamMediaInput(_liveTailStream);
                    
                    _currentMedia = new Media(MainWindow.SharedLibVLC, _currentMediaInput);
                    
                    _currentMedia.AddOption(":network-caching=5000"); 
                    _currentMedia.AddOption(":live-caching=5000");
                    _currentMedia.AddOption(":demux=ts");
                    _currentMedia.AddOption(":deinterlace=1");
                    _currentMedia.AddOption(":deinterlace-mode=yadif");
                    
                    // --- TIER 1: GLOBAL LENIENCY FOR TIMESHIFT ---
                    _currentMedia.AddOption(":clock-jitter=0");      
                    _currentMedia.AddOption(":no-ts-cc-check");         
                    _currentMedia.AddOption(":no-drop-late-frames");    
                    _currentMedia.AddOption(":no-skip-frames");         
                    _currentMedia.AddOption(":no-avcodec-hurry-up");    
                    
                    // --- TIER 2: OTA NUCLEAR OPTION FOR TIMESHIFT ---
                    if (currentChannel.Number != null && currentChannel.Number.Contains("."))
                    {
                        _currentMedia.AddOption(":no-ts-trust-pcr");       
                        _currentMedia.AddOption(":no-ts-seek-percent");    
                        _currentMedia.AddOption(":clock-synchro=0");       
                    }
                }
                else
                {
                    LogDebug("PlayCurrentChannel: Streaming directly.");
                    
                    _currentMedia = new Media(MainWindow.SharedLibVLC, new Uri(activeStreamUrl));
                    _currentMedia.AddOption(":network-caching=3000");
                    _currentMedia.AddOption(":live-caching=3000");
                    _currentMedia.AddOption(":deinterlace=1");
                    _currentMedia.AddOption(":deinterlace-mode=yadif");
                    
                    // --- TIER 1: GLOBAL LENIENCY (Helps Pluto/TVE survive commercial breaks) ---
                    LogDebug("Applying global stream leniency flags.");
                    _currentMedia.AddOption(":clock-jitter=0");      // Integer value, so =5000 is correct
                    _currentMedia.AddOption(":no-ts-cc-check");         // Boolean flag (False)
                    _currentMedia.AddOption(":no-drop-late-frames");    // Boolean flag (False)
                    _currentMedia.AddOption(":no-skip-frames");         // Boolean flag (False)
                    _currentMedia.AddOption(":no-avcodec-hurry-up");    // Boolean flag (False)
                    
                    // --- TIER 2: THE TRUE NUCLEAR OPTION (Only for broken antenna clocks) ---
                                       
                    if (currentChannel.Number != null && currentChannel.Number.Contains("."))
                    {
                        LogDebug("Applying strict clock overrides for OTA broadcast.");
                        _currentMedia.AddOption(":no-ts-trust-pcr");       // Boolean flag (False)
                        _currentMedia.AddOption(":no-ts-seek-percent");    // Boolean flag (False)
                        _currentMedia.AddOption(":clock-synchro=0");       // Integer mode (-1, 0, 1), so =0 is correct
                    }
                }

                _currentMedia.AddOption(":avcodec-hw=none");
                _currentMedia.AddOption(":no-spu");
                _currentMedia.AddOption(":no-sub-autodetect-file");
                _currentMedia.AddOption(":freetype-rel-fontsize=12");

                if (offsetSeconds > 0 && !useProxy)
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

                _tuneTimeoutTimer?.Stop();
                _tuneTimeoutTimer?.Start();

                _mediaPlayer.Play(_currentMedia);
            }
            catch (Exception ex)
            {
                LogDebug($"FATAL C# ERROR in PlayCurrentChannel: {ex.Message}");
                MessageBox.Show($"Failed to load channel: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        System.Windows.Input.Mouse.OverrideCursor = null; // Restore globally
    }

    _uiTimer.Stop();
    _uiTimer.Start();
}

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            ControlBar.Visibility = Visibility.Collapsed;
            
            // --- FIX: The native VLC video surface breaks WPF's "IsMouseOver" hit-testing.
            // Checking "IsActive" guarantees we only hide the cursor if this window is currently in focus.
            if (this.IsActive) 
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
            }
            
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

        private async void PlayerWindow_Closed(object? sender, EventArgs e)
        {
            LogDebug("PlayerWindow_Closed: Cleaning up resources.");
            System.Windows.Input.Mouse.OverrideCursor = null; // Restore globally
            
            // --- NEW: Restore the Main Window! ---
            if (Application.Current.MainWindow != null)
            {
                Application.Current.MainWindow.Show();
                if (Application.Current.MainWindow.WindowState == WindowState.Minimized)
                {
                    Application.Current.MainWindow.WindowState = WindowState.Normal;
                }
                Application.Current.MainWindow.Activate();
            }
    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            
            _uiTimer?.Stop();
            _statsTimer?.Stop();
            _tuneTimeoutTimer?.Stop();
            _liveProgressTimer?.Stop();
            
            var playerToDispose = _mediaPlayer;

            if (_mediaPlayer != null)
            {
                _mediaPlayer.EndReached -= MediaPlayer_EndReached;
                _mediaPlayer.Buffering -= MediaPlayer_Buffering;
                _mediaPlayer.Playing -= MediaPlayer_Playing;
                _mediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
                _mediaPlayer.LengthChanged -= MediaPlayer_LengthChanged; 
                _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;

                if (_mediaPlayer.IsPlaying) 
                {
                    LogDebug("PlayerWindow_Closed: Stopping _mediaPlayer.");
                    _mediaPlayer.Stop();
                }

                _mediaPlayer.Media = null;
                VlcVideoView.MediaPlayer = null;
            }

            // CRITICAL FIX: Give VLC 250ms to properly close its network sockets before we
            // invoke CleanupSpooler (which aggressively terminates the FFmpeg process).
            await Task.Delay(250);
            CleanupSpooler();

            _ = Task.Run(async () =>
            {
                await Task.Delay(4000); 
                LogDebug("PlayerWindow_Closed: Safely disposing _mediaPlayer in background.");
                playerToDispose?.Dispose();
            });
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