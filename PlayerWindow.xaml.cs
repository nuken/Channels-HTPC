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
        private Media? _currentMedia;
		private string _timeShiftDir = "";
        private System.Net.HttpListener? _hlsServer; // NEW: The micro web server!
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
		private HashSet<int> _disabledCommercialBlocks = new HashSet<int>();
        
        // --- Auto-Scrubbing Trackers ---
        private bool _isScrubbing = false;
        private long _scrubTargetTime = 0;
		private long _lastPauseTime = -1;
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
		
		private void CleanupSpooler()
        {
            StopFfmpegProxy();

            var oldMedia = _currentMedia;
            var oldServer = _hlsServer; // <--- ADD THIS
            var dirToDelete = _timeShiftDir;

            _currentMedia = null;
            _hlsServer = null; // <--- ADD THIS
            _timeShiftDir = "";

            _ = Task.Run(async () => 
            {
                oldServer?.Stop();  // <--- ADD THIS
                oldServer?.Close(); // <--- ADD THIS

                await Task.Delay(3000); 
                
                oldMedia?.Dispose();

                if (!string.IsNullOrEmpty(dirToDelete) && Directory.Exists(dirToDelete))
                {
                    try { Directory.Delete(dirToDelete, true); } catch { }
                }
            });
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
		
		// --- NEW: Added boolean flag 'useTranscode' ---
        private async Task<string> StartFfmpegProxyAsync(string sourceUrl, int offsetSeconds, string targetAudioCodec, bool useTranscode)
        {
            await Task.Delay(150);
            StopFfmpegProxy(); 

            int dynamicPort = GetAvailablePort();
            string ffmpegBindUrl = $"http://127.0.0.1:{dynamicPort}";
            string clientUrl = $"http://127.0.0.1:{dynamicPort}/stream.ts";

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localFfmpegPath = System.IO.Path.Combine(appData, "FeralHTPC", "ffmpeg", "ffmpeg.exe");
            string targetExecutable = System.IO.File.Exists(localFfmpegPath) ? localFfmpegPath : "ffmpeg";
            
            string audioArgs = targetAudioCodec == "copy" ? "-c:a copy" : "-c:a aac -ac 2";

            // --- THE MAGIC SWITCH ---
            // If transcode is true, use heavy x264 and ignore DTS. If false, copy video and keep DTS.
            string videoArgs = useTranscode ? "-vf yadif -c:v libx264 -preset ultrafast -tune zerolatency" : "-c:v copy";
            string timeFlags = useTranscode ? "+genpts+igndts+discardcorrupt" : "+genpts+discardcorrupt";

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = targetExecutable, 
                Arguments = $"-nostdin -hide_banner -loglevel warning -analyzeduration 3000000 -probesize 3000000 -fflags {timeFlags} -i \"{sourceUrl}\" -map 0:V:0? -map 0:a:0? -map 0:s? {videoArgs} {audioArgs} -c:s copy -ignore_unknown -max_muxing_queue_size 4096 -f mpegts -listen 1 {ffmpegBindUrl}",
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

            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine(); 
            
            string modeName = useTranscode ? "Transcode" : "Remux";
            LogDebug($"Started FFmpeg local proxy ({modeName}). Waiting for port {dynamicPort} to bind...");

            // ... (keep the rest of the while loop and port binding logic exactly the same) ...
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool isBound = false;
            while (sw.ElapsedMilliseconds < 20000) 
            {
                if (_ffmpegProcess == null || _ffmpegProcess.HasExited) break;
                if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(l => l.Port == dynamicPort))
                {
                    isBound = true;
                    break;
                }
                await Task.Delay(250);
            }

            if (!isBound)
            {
                StopFfmpegProxy();
                return "";
            }
            return clientUrl;
        }
		
		private void StartHlsServer(string dir, int port)
        {
            try
            {
                _hlsServer = new System.Net.HttpListener();
                _hlsServer.Prefixes.Add($"http://127.0.0.1:{port}/");
                _hlsServer.Start();

                Task.Run(async () =>
                {
                    while (_hlsServer != null && _hlsServer.IsListening)
                    {
                        try
                        {
                            var context = await _hlsServer.GetContextAsync();
                            var requestPath = context.Request.Url!.AbsolutePath.TrimStart('/');
                            var filePath = Path.Combine(dir, requestPath);
                            
                            if (File.Exists(filePath))
                            {
                                if (requestPath.EndsWith(".m3u8")) context.Response.ContentType = "application/vnd.apple.mpegurl";
                                else if (requestPath.EndsWith(".ts")) context.Response.ContentType = "video/MP2T";

                                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    // --- THE MAGIC: Handle HTTP Byte-Range Requests for Single-File HLS ---
                                    if (context.Request.Headers.AllKeys.Contains("Range"))
                                    {
                                        var rangeHeader = context.Request.Headers["Range"];
                                        var range = rangeHeader!.Replace("bytes=", "").Split('-');
                                        long start = long.Parse(range[0]);
                                        long end = range.Length > 1 && !string.IsNullOrWhiteSpace(range[1]) ? long.Parse(range[1]) : fs.Length - 1;
                                        if (end >= fs.Length) end = fs.Length - 1;
                                        long length = end - start + 1;

                                        context.Response.StatusCode = 206; // 206 Partial Content
                                        context.Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{fs.Length}");
                                        context.Response.ContentLength64 = length;

                                        fs.Position = start;
                                        byte[] buffer = new byte[64 * 1024];
                                        long bytesRemaining = length;
                                        while (bytesRemaining > 0)
                                        {
                                            int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                                            int bytesRead = await fs.ReadAsync(buffer, 0, bytesToRead);
                                            if (bytesRead == 0) break;
                                            await context.Response.OutputStream.WriteAsync(buffer, 0, bytesRead);
                                            bytesRemaining -= bytesRead;
                                        }
                                    }
                                    else
                                    {
                                        // Standard full-file request (for the .m3u8 playlist)
                                        context.Response.ContentLength64 = fs.Length;
                                        await fs.CopyToAsync(context.Response.OutputStream);
                                    }
                                }
                                context.Response.OutputStream.Close();
                            }
                            else
                            {
                                context.Response.StatusCode = 404;
                                context.Response.Close();
                            }
                        }
                        catch { } // Ignore client disconnects
                    }
                });
            }
            catch (Exception ex) { LogDebug($"Failed to start HLS server: {ex.Message}"); }
        }
		
		private async Task<string> StartTimeShiftFfmpegAsync(string sourceUrl, string targetAudioCodec)
        {
            await Task.Delay(150);
            StopFfmpegProxy(); 

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _timeShiftDir = System.IO.Path.Combine(appData, "FeralHTPC", "TimeShift", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(_timeShiftDir);

            // --- THE FIX: Standard Discrete Chunk HLS ---
            string m3u8Path = System.IO.Path.Combine(_timeShiftDir, "live.m3u8");
            string tsPattern = System.IO.Path.Combine(_timeShiftDir, "seg_%05d.ts"); // Creates seg_00001.ts, etc.
            
            string localFfmpegPath = System.IO.Path.Combine(appData, "FeralHTPC", "ffmpeg", "ffmpeg.exe");
            string targetExecutable = System.IO.File.Exists(localFfmpegPath) ? localFfmpegPath : "ffmpeg";

            string audioArgs = targetAudioCodec == "copy" ? "-c:a copy" : "-c:a aac -ac 2";

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = targetExecutable, 
                // Removed single_file. Added -hls_list_size 0 (Infinite TimeShift Playlist)
                Arguments = $"-nostdin -hide_banner -loglevel warning -analyzeduration 1500000 -probesize 1500000 -fflags +genpts+discardcorrupt -i \"{sourceUrl}\" -map 0:V:0? -map 0:a:0? -ignore_unknown -max_muxing_queue_size 4096 -c:v copy {audioArgs} -f hls -hls_time 2 -hls_list_size 0 -hls_segment_filename \"{tsPattern}\" \"{m3u8Path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            _ffmpegProcess = new System.Diagnostics.Process { StartInfo = startInfo };
            _ffmpegProcess.EnableRaisingEvents = true; 
            
            _ffmpegProcess.ErrorDataReceived += (s, e) => 
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogDebug($"[FFMPEG-TS] {e.Data}");
            };

            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine(); 
            
            LogDebug($"Started FFmpeg TimeShift Engine (Discrete HLS). Writing to {_timeShiftDir}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 35000) 
            {
                if (System.IO.File.Exists(m3u8Path))
                {
                    try
                    {
                        using (var fs = new FileStream(m3u8Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var sr = new StreamReader(fs))
                        {
                            string content = await sr.ReadToEndAsync();
                            
                            // Count how many .ts segments have been successfully written to the playlist
                            int segmentCount = content.Split(new[] { ".ts" }, StringSplitOptions.None).Length - 1;
                            
                            // Wait for at least 4 chunks (8+ seconds of video) to guarantee a stutter-free launch
                            if (segmentCount >= 4)
                            {
                                int dynamicPort = GetAvailablePort();
                                StartHlsServer(_timeShiftDir, dynamicPort);
                                return $"http://127.0.0.1:{dynamicPort}/live.m3u8"; 
                            }
                        }
                    }
                    catch { } // Ignore file lock collisions
                }
                await Task.Delay(250);
            }

            StopFfmpegProxy();
            return "";
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

            if (currentAiring != null && currentAiring.Duration.HasValue && !_isDraggingTimeline && !_isScrubbing)
            {
                double elapsedMs = (DateTime.Now - currentAiring.StartTime).TotalMilliseconds;

                // --- NEW: Subtract the TimeShift delay from the real-world clock! ---
                if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                {
                    long delayMs = _mediaPlayer.Length - _mediaPlayer.Time;
                    if (delayMs > 0) elapsedMs -= delayMs;
                }

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
            long safeLength = _mediaPlayer.Length; // Grab the length directly from the engine

            if (_isMovieMode && _movieCommercials != null && _movieCommercials.Count >= 2 && _settings.AutoSkipCommercials)
            {
                for (int i = 0; i < _movieCommercials.Count - 1; i += 2)
                {
                    long startMs = (long)(_movieCommercials[i] * 1000);
                    long endMs = (long)(_movieCommercials[i + 1] * 1000);
                    int blockIndex = i / 2; // Identify which commercial block this is

                    // --- NEW: Smart Commercial Skip Logic ---
                    // If the user manually scrubbed into the commercial, disable auto-skip for this block
                    if ((_isDraggingTimeline || _isScrubbing) && currentTime >= startMs - 5000 && currentTime <= endMs)
                    {
                        if (!_disabledCommercialBlocks.Contains(blockIndex))
                        {
                            _disabledCommercialBlocks.Add(blockIndex);
                            LogDebug($"Disabled auto-skip for commercial block {blockIndex}");
                        }
                    }
                    // If they rewind well before the commercial, re-enable auto-skip for it
                    else if (currentTime < startMs - 30000)
                    {
                        if (_disabledCommercialBlocks.Contains(blockIndex))
                        {
                            _disabledCommercialBlocks.Remove(blockIndex);
                            LogDebug($"Re-enabled auto-skip for commercial block {blockIndex}");
                        }
                    }

                    // Perform the skip if the block is active and NOT disabled
                    if (!_disabledCommercialBlocks.Contains(blockIndex) && currentTime >= startMs && currentTime < endMs - 1000)
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
                // Fallback in case VLC failed to fire the LengthChanged event on startup!
                if (safeLength > 0 && TimelineSlider.Maximum != safeLength)
                {
                    TimelineSlider.Maximum = safeLength;
                    TotalTimeText.Text = TimeSpan.FromMilliseconds(safeLength).ToString(@"h\:mm\:ss");
                }

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
		
		public void ToggleAudioTrack()
        {
            if (_mediaPlayer == null) return;

            var tracks = _mediaPlayer.AudioTrackDescription;
            if (tracks == null || tracks.Length <= 1)
            {
                ShowActionOverlay("🚫 No Other Audio Available");
                return;
            }

            int currentId = _mediaPlayer.AudioTrack;
            
            int currentIndex = 0;
            for (int i = 0; i < tracks.Length; i++)
            {
                if (tracks[i].Id == currentId)
                {
                    currentIndex = i;
                    break;
                }
            }
            
            int nextIndex = (currentIndex + 1) % tracks.Length;
            var nextTrack = tracks[nextIndex];
            
            _mediaPlayer.SetAudioTrack(nextTrack.Id);

            string statusText = nextTrack.Id == -1 ? "Audio: Off" : $"Audio: {nextTrack.Name}";
            ShowActionOverlay(statusText);
        }

        private void BtnAudio_Click(object sender, RoutedEventArgs e)
        {
            OpenAudioMenu();
        }

        private void OpenAudioMenu()
        {
            if (_mediaPlayer == null) return;

            var tracks = _mediaPlayer.AudioTrackDescription;
            if (tracks == null || tracks.Length <= 1)
            {
                ShowActionOverlay("🚫 No Other Audio Available");
                return;
            }

            ContextMenu audioMenu = new ContextMenu();
            
            audioMenu.Background = (System.Windows.Media.Brush)Application.Current.FindResource("PanelBackground");
            audioMenu.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimary");
            audioMenu.BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush");
            audioMenu.BorderThickness = new Thickness(2);

            int currentId = _mediaPlayer.AudioTrack;

            foreach (var track in tracks)
            {
                MenuItem item = new MenuItem();
                
                item.Header = track.Id == -1 ? "Disable Audio" : $"Track: {track.Name}";
                item.Tag = track.Id;
                item.FontSize = 16;
                item.Padding = new Thickness(10, 5, 10, 5);
                
                if (track.Id == currentId)
                {
                    item.IsChecked = true; 
                    item.FontWeight = FontWeights.Bold;
                }

                item.Click += (s, args) =>
                {
                    int selectedId = (int)((MenuItem)s!).Tag;
                    _mediaPlayer.SetAudioTrack(selectedId);
                    
                    string statusText = selectedId == -1 ? "Audio: Off" : $"Audio: {track.Name}";
                    ShowActionOverlay(statusText);
                };

                audioMenu.Items.Add(item);
            }

            BtnAudio.ContextMenu = audioMenu;
            audioMenu.PlacementTarget = BtnAudio;
            audioMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            audioMenu.IsOpen = true;
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
            if (_isMovieMode)
            {
                _mediaPlayer.Time = (long)TimelineSlider.Value;
            }
            else if (_channels != null && _channels.Any())
            {
                // --- NEW: Convert UI Guide Time to VLC Buffer Time ---
                var currentAiring = _channels[_currentIndex].CurrentAirings?.FirstOrDefault(a => a.IsAiringNow);
                if (currentAiring != null)
                {
                    double liveEdgeEpgMs = (DateTime.Now - currentAiring.StartTime).TotalMilliseconds;
                    double millisecondsBehindLive = liveEdgeEpgMs - TimelineSlider.Value;
                        
                    long targetVlcTime = _mediaPlayer.Length - (long)millisecondsBehindLive;
                        
                    if (targetVlcTime < 0) targetVlcTime = 0;
                    if (targetVlcTime > _mediaPlayer.Length && _mediaPlayer.Length > 0) targetVlcTime = _mediaPlayer.Length;
                        
                    _mediaPlayer.Time = targetVlcTime;
                    if (!_mediaPlayer.IsPlaying) _lastPauseTime = targetVlcTime;
                }
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

                if (_mediaPlayer.IsPlaying) 
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Media = null; 
                }

                CleanupSpooler();

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

                // --- THE NEW ROUTING LOGIC ---
                // You can easily change this IF statement in the future to test different channels!
                bool isVirtualChannel = currentChannel.Id != null && currentChannel.Id.StartsWith("virtual", StringComparison.OrdinalIgnoreCase);
                bool isPluto = currentChannel.Id != null && currentChannel.Id.Contains("pluto", StringComparison.OrdinalIgnoreCase);

                if (isVirtualChannel || isPluto)
                {
                    LogDebug("Routing to PlayHlsStream...");
                    await PlayHlsStream(currentChannel, currentAiring);
                }
                else
                {
                    LogDebug("Routing to PlayTsStream...");
                    await PlayTsStream(currentChannel, currentAiring);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"FATAL C# ERROR in PlayCurrentChannel: {ex.Message}");
                MessageBox.Show($"Failed to load channel: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
            }
        }

        private async Task PlayHlsStream(Channel currentChannel, Airing? currentAiring)
        {
            string streamUrl = "";
            int offsetSeconds = 0;
            bool isVirtualChannel = currentChannel.Id != null && currentChannel.Id.StartsWith("virtual", StringComparison.OrdinalIgnoreCase);

            if (isVirtualChannel)
            {
                LogDebug("PlayHlsStream: Virtual channel detected.");
                if (currentAiring != null && !string.IsNullOrWhiteSpace(currentAiring.Source))
                {
                    string fileId = currentAiring.Source.Split('/').Last(); 
                    streamUrl = $"{_baseUrl.TrimEnd('/')}/dvr/files/{fileId}/hls/stream.m3u8";
                    
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
                LogDebug("PlayHlsStream: Standard Live HLS detected.");
                // Request the pre-packaged HLS live stream directly from Channels DVR
                streamUrl = $"{_baseUrl.TrimEnd('/')}/devices/ANY/channels/{currentChannel.Number}/hls/master.m3u8";
            }
            
            LogDebug($"PlayHlsStream: Final HLS URL: {streamUrl}");

            string activeStreamUrl = streamUrl;
            
            // --- NEW: Check if the user is forcing a transcode or remux! ---
            bool useTranscode = _settings.ForceLocalTranscode || 
            (_settings.ForcedFfmpegChannels != null && _settings.ForcedFfmpegChannels.Contains(currentChannel.Number!));

            bool useRemux = _settings.ForceLocalRemux || 
            (_settings.ForcedFfmpegRemuxChannels != null && _settings.ForcedFfmpegRemuxChannels.Contains(currentChannel.Number!));
            
            if (useTranscode || useRemux)
            {
                LogDebug($"Routing HLS stream through FFmpeg proxy (Transcode: {useTranscode}).");
                string audioCodec = _settings.ForceAacAudio ? "aac" : "copy";
                
                // Pass the useTranscode flag so FFmpeg knows which arguments to use
                activeStreamUrl = await StartFfmpegProxyAsync(streamUrl, offsetSeconds, audioCodec, useTranscode);
                
                if (string.IsNullOrEmpty(activeStreamUrl))
                {
                    MessageBox.Show("Failed to start the proxy stream.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    this.Close();
                    return;
                }
            }

            _currentMedia = new Media(MainWindow.SharedLibVLC, new Uri(activeStreamUrl));
            _currentMedia.AddOption(":network-caching=3000");
            _currentMedia.AddOption(":live-caching=3000");
            _currentMedia.AddOption(":deinterlace=1");
            _currentMedia.AddOption(":deinterlace-mode=yadif");
            _currentMedia.AddOption(":avcodec-hw=none");
            _currentMedia.AddOption(":no-spu");
            _currentMedia.AddOption(":no-sub-autodetect-file");
            _currentMedia.AddOption(":freetype-rel-fontsize=12");
            
            // Only force 4k if we aren't routing it through the local proxy
            if (!useTranscode && !useRemux) 
            {
                _currentMedia.AddOption(":preferred-resolution=2160"); 
            }

            if (offsetSeconds > 0 && !useTranscode && !useRemux)
            {
                _currentMedia.AddOption($":start-time={offsetSeconds}");
            }

            _mediaPlayer.Play(_currentMedia);
        }

        private async Task PlayTsStream(Channel currentChannel, Airing? currentAiring)
        {
            var settings = SettingsManager.Load();
            string audioCodec = "aac"; 

            if (!settings.ForceAacAudio)
            {
                audioCodec = "copy";
                if (double.TryParse(currentChannel.Number, out double chNum) && chNum >= 100 && chNum < 200)
                    audioCodec = "aac";
            }

            string streamUrl = $"{_baseUrl.TrimEnd('/')}/devices/ANY/channels/{currentChannel.Number}/stream.mpg?format=ts&vcodec=copy&acodec={audioCodec}";
            LogDebug($"PlayTsStream: Final Stream URL: {streamUrl}");

            string activeStreamUrl = streamUrl;
            
            // --- NEW: Evaluate Proxy Settings ---
            bool useTranscode = _settings.ForceLocalTranscode || 
            (_settings.ForcedFfmpegChannels != null && _settings.ForcedFfmpegChannels.Contains(currentChannel.Number!));

            bool useRemux = _settings.ForceLocalRemux || 
            (_settings.ForcedFfmpegRemuxChannels != null && _settings.ForcedFfmpegRemuxChannels.Contains(currentChannel.Number!));
            
            // TimeShift has its own FFmpeg engine, so disable standard proxy if TimeShift is on
            if (_settings.EnableTimeShiftBuffer)
            {
                LogDebug("PlayTsStream: Time-Shift enabled. Starting FFmpeg Spooler.");
                string m3u8LocalPath = await StartTimeShiftFfmpegAsync(activeStreamUrl, audioCodec);

                if (string.IsNullOrEmpty(m3u8LocalPath))
                {
                    MessageBox.Show("Failed to initialize TimeShift buffer.", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    this.Close();
                    return;
                }

                _currentMedia = new Media(MainWindow.SharedLibVLC, new Uri(m3u8LocalPath));

                // --- TWEAK: Increased cache from 3000 to 8000 to absorb live-edge updates! ---
                _currentMedia.AddOption(":network-caching=8000"); 
                _currentMedia.AddOption(":live-caching=8000");
                
                _currentMedia.AddOption(":deinterlace=1");
                _currentMedia.AddOption(":deinterlace-mode=yadif");
                _currentMedia.AddOption(":avcodec-hw=none");

                _currentMedia.AddOption(":clock-jitter=5000");     
                _currentMedia.AddOption(":no-ts-cc-check");         
                _currentMedia.AddOption(":no-avcodec-hurry-up");    
            }
            else
            {
                LogDebug("PlayTsStream: Streaming directly.");
                _currentMedia = new Media(MainWindow.SharedLibVLC, new Uri(activeStreamUrl));
                _currentMedia.AddOption(":network-caching=3000");
                _currentMedia.AddOption(":live-caching=3000");
                _currentMedia.AddOption(":deinterlace=1");
                _currentMedia.AddOption(":deinterlace-mode=yadif");
                
                LogDebug("Applying global stream leniency flags.");
                _currentMedia.AddOption(":clock-jitter=5000");     
                _currentMedia.AddOption(":no-ts-cc-check");         
                _currentMedia.AddOption(":no-avcodec-hurry-up");    
                
                if (currentChannel.Number != null && currentChannel.Number.Contains("."))
                {
                    LogDebug("Applying strict clock overrides for OTA broadcast.");
                    _currentMedia.AddOption(":no-ts-trust-pcr");       
                    _currentMedia.AddOption(":no-ts-seek-percent");    
                    _currentMedia.AddOption(":clock-synchro=0");       
                    _currentMedia.AddOption(":no-drop-late-frames");    
                    _currentMedia.AddOption(":no-skip-frames"); 
                }
            }

            _currentMedia.AddOption(":avcodec-hw=none");
            _currentMedia.AddOption(":no-spu");
            _currentMedia.AddOption(":no-sub-autodetect-file");
            _currentMedia.AddOption(":freetype-rel-fontsize=12");

            _mediaPlayer.Play(_currentMedia);
        }
		
        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsPlaying)
            {
                _lastPauseTime = _mediaPlayer.Time; 
                _mediaPlayer.Pause();
                BtnPlayPause.Content = "\u25B6"; 
            }
            else
            {
                _mediaPlayer.Play();
                BtnPlayPause.Content = "\u23F8"; 

                if (!_isMovieMode && _lastPauseTime > 0)
                {
                    long targetTime = _lastPauseTime;
                    _lastPauseTime = -1; // Clear it out so it only fires once
                    
                    // --- NEW: Wait for VLC's engine to wake up before forcing the time! ---
                    Task.Run(async () =>
                    {
                        await Task.Delay(400); // 400ms is the sweet spot for HLS streams
                        
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            _mediaPlayer.Time = targetTime; // Removed IsSeekable check
                        });
                    });
                }
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
            _mediaPlayer.Time -= 10000; 
            ShowActionOverlay("⏪ -10s");
        }

        private void FastForward_Click(object sender, RoutedEventArgs e)
        {
            _mediaPlayer.Time += 30000;
            ShowActionOverlay("⏩ +30s");
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
                if (ControlBar.Visibility == Visibility.Collapsed)
                {
                    if (!_isScrubbing)
                    {
                        _isScrubbing = true;
                        _scrubTargetTime = _mediaPlayer.Time;
                        _accumulatedScrubSeconds = 0; // Reset for live TV
                        ActionOverlayText.BeginAnimation(UIElement.OpacityProperty, null); 
                        ActionOverlayText.Opacity = 1.0;
                    }

                    long jumpAmount = (e.Key == System.Windows.Input.Key.Right) ? 
                                      (e.IsRepeat ? 5000 : 10000) : 
                                      (e.IsRepeat ? -5000 : -10000);

                    _scrubTargetTime += jumpAmount;
                    
                    if (_scrubTargetTime < 0) _scrubTargetTime = 0;
                    if (_mediaPlayer.Length > 0 && _scrubTargetTime > _mediaPlayer.Length) 
                        _scrubTargetTime = _mediaPlayer.Length;

                    if (_isMovieMode)
                    {
                        TimelineSlider.Value = _scrubTargetTime;
                        CurrentTimeText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
                        ActionOverlayText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
                    }
                    else
                    {
                        // Live TV: Show the relative scrub jump (+/- seconds) so it doesn't snap the EPG slider
                        _accumulatedScrubSeconds += (jumpAmount / 1000.0);
                        string sign = _accumulatedScrubSeconds > 0 ? "+" : "";
                        ActionOverlayText.Text = $"Scrubbing: {sign}{_accumulatedScrubSeconds}s";
                    }
                    
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
			else if (e.Key == System.Windows.Input.Key.A) 
            {
                ToggleAudioTrack();
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
                _mediaPlayer.Time = _scrubTargetTime; // Removed IsSeekable check

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
            
            // Prevent scrubbing past the live edge
            if (_mediaPlayer.Length > 0 && _scrubTargetTime > _mediaPlayer.Length) 
                _scrubTargetTime = _mediaPlayer.Length;

            if (_isMovieMode)
            {
                TimelineSlider.Value = _scrubTargetTime;
                CurrentTimeText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
                ActionOverlayText.Text = TimeSpan.FromMilliseconds(_scrubTargetTime).ToString(@"h\:mm\:ss");
            }
            else
            {
                // Live TV: Show the relative scrub jump (+/- seconds) so it doesn't snap the EPG slider
                _accumulatedScrubSeconds += (jumpAmount / 1000.0);
                string sign = _accumulatedScrubSeconds > 0 ? "+" : "";
                ActionOverlayText.Text = $"Scrubbing: {sign}{_accumulatedScrubSeconds}s";
            }
        }

       public void StopRemoteScrub()
        {
            if (!_isScrubbing) return;
            
            _remoteScrubTimer.Stop();
            _isScrubbing = false;
            
            _mediaPlayer.Time = _scrubTargetTime; // Removed IsSeekable check
                
            // --- NEW: If we are paused, update the lock so play resumes from here! ---
            if (!_mediaPlayer.IsPlaying) _lastPauseTime = _scrubTargetTime;

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
                string audioInfo = "Unknown"; // NEW: To hold the audio channel count
                
                var tracks = _mediaPlayer.Media.Tracks;
                if (tracks != null)
                {
                    foreach (var t in tracks)
                    {
                        if (t.TrackType == TrackType.Video)
                        {
                            resolution = $"{t.Data.Video.Width}x{t.Data.Video.Height}";
                        }
                        else if (t.TrackType == TrackType.Audio)
                        {
                            // NEW: Identify Stereo vs 5.1 Surround
                            string channelType = t.Data.Audio.Channels >= 6 ? "5.1 Surround" : (t.Data.Audio.Channels == 2 ? "Stereo" : "Mono");
                            audioInfo = $"{t.Data.Audio.Channels} ch {channelType} ({t.Data.Audio.Rate} Hz)";
                        }
                    }
                }
                
                StatsText.Text = $"=== STATS FOR NERDS ===\n" +
                                 $"Resolution    : {resolution}\n" +
                                 $"Audio Format  : {audioInfo}\n" + // NEW: Display it on the overlay!
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