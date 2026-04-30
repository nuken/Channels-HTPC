using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FeralCode
{
    public partial class QuadPlayerWindow : Window
    {
        // --- NEW: Toggle this to false to disable logging! ---
        private bool _enableLogging;

        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer[] _players = new LibVLCSharp.Shared.MediaPlayer[4];
		
        private LibVLCSharp.WPF.VideoView[] _views;
        private Border[] _borders;
        private TextBlock[] _errorTexts;
        private int _activeIndex = 0;
        private int _totalActive = 0;
        public int TotalActive => _totalActive;
        public int ActiveIndex => _activeIndex;

        private bool _isFullscreen = true;
        private DispatcherTimer _audioTimer;
        private DispatcherTimer _uiTimer;
        private DateTime _lastMouseMove = DateTime.MinValue;
        private Window? _glassOverlay;

        // --- NEW: Watchdog & Cast Trackers ---
        private bool _isWaitingForCast = false;

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

        public QuadPlayerWindow(string baseUrl, List<Channel> channels)
        {
            // Load settings FIRST so the logger knows if it should be awake!
            var initialSettings = SettingsManager.Load();
            _enableLogging = initialSettings.EnableDebugLogging;

            LogDebug($"QuadPlayerWindow: Initializing with {channels.Count} channels.");
            InitializeComponent();

            this.Loaded += (s, e) =>
            {
                this.Activate();
                this.Topmost = true;

                var settings = SettingsManager.Load();
                if (!settings.StartPlayersFullscreen)
                {
                    this.Topmost = false;
                    if (_isFullscreen) ToggleFullscreen();
                }

                SetupGlassOverlay();

                this.Focus();

                // --- ADD THIS: Force layout update AFTER native video players fully load ---
                Application.Current.Dispatcher.InvokeAsync(() => 
                {
                    UpdateDynamicLayout();
                }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            };

            _libVLC = MainWindow.SharedLibVLC;
            _totalActive = channels.Count;

            _views = new[] { View0, View1, View2, View3 };
            _borders = new[] { Border0, Border1, Border2, Border3 };
            _errorTexts = new[] { ErrorText0, ErrorText1, ErrorText2, ErrorText3 };

            _audioTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _audioTimer.Tick += (s, e) =>
            {
                _audioTimer.Stop();
                ApplyAudioFocus();
            };

            for (int i = 0; i < 4; i++)
            {
                if (i < channels.Count)
                {
                    int playerIndex = i; // Create local copy for async closure
                    LogDebug($"QuadPlayerWindow: Setting up player {playerIndex} for CH {channels[playerIndex].Number}");

                    _players[playerIndex] = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                    _views[playerIndex].MediaPlayer = _players[playerIndex];

                    string audioCodec = "aac";
                    var settings = SettingsManager.Load();
                    if (!settings.ForceAacAudio)
                    {
                        audioCodec = "copy";
                        if (double.TryParse(channels[playerIndex].Number, out double chNum) && chNum >= 100 && chNum < 200)
                        {
                            audioCodec = "aac";
                        }
                    }

                    string streamUrl = "";
int offsetSeconds = 0;

if (channels[playerIndex].Id != null && channels[playerIndex].Id!.StartsWith("virtual", StringComparison.OrdinalIgnoreCase))
{
    var currentAiring = channels[playerIndex].CurrentAirings?.FirstOrDefault(a => a.IsAiringNow);

    if (currentAiring != null && !string.IsNullOrWhiteSpace(currentAiring.Source))
    {
        // Extract the raw file ID from the source path
        string fileId = currentAiring.Source.Split('/').Last();
        streamUrl = $"{baseUrl.TrimEnd('/')}/dvr/files/{fileId}/hls/master.m3u8";

        // Calculate how far into the episode we should jump to simulate "Live TV"
        offsetSeconds = (int)(DateTime.Now - currentAiring.StartTime).TotalSeconds;
        if (offsetSeconds < 0) offsetSeconds = 0;

        LogDebug($"QuadPlayer: Virtual CH {channels[playerIndex].Number} -> File {fileId} @ {offsetSeconds}s");
    }
    else
    {
        LogDebug("QuadPlayer: Virtual channel missing guide data. Fallback to default HLS.");
        streamUrl = $"{baseUrl.TrimEnd('/')}/devices/ANY/channels/{channels[playerIndex].Number}/hls/master.m3u8?vcodec=copy&acodec={audioCodec}";
    }
}
else
{
    streamUrl = $"{baseUrl.TrimEnd('/')}/devices/ANY/channels/{channels[playerIndex].Number}/stream.mpg?format=ts&vcodec=copy&acodec={audioCodec}";
}

                    _players[playerIndex].EncounteredError += (sender, args) =>
                    {
                        LogDebug($"VLC CALLBACK: QuadPlayer {playerIndex} EncounteredError.");

                        // --- SHOW THE ERROR TEXT ---
                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            _errorTexts[playerIndex].Visibility = Visibility.Visible;
                        });
                    };

                    _players[playerIndex].Playing += async (sender, args) =>
                    {
                        // --- HIDE THE ERROR TEXT IF IT SUCCESSFULLY PLAYS ---
                        _ = Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            _errorTexts[playerIndex].Visibility = Visibility.Collapsed;
                        });

                        await Task.Delay(1000);
                        Application.Current.Dispatcher.Invoke(() => ApplyAudioFocus());

                        await Task.Delay(2500);
                        Application.Current.Dispatcher.Invoke(() => ApplyAudioFocus());
                    };

                    // --- THE FIX: Clean Native Loading & STAGGERED TUNING ---
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Stagger each tuner by 1.5 seconds to prevent hardware collisions!
                            if (playerIndex > 0)
                            {
                                await Task.Delay(playerIndex * 1500);
                            }

                            // --- NEW: Respect Remux/Transcode Settings ---
                            bool useTranscode = settings.ForceLocalTranscode || 
                                (settings.ForcedFfmpegChannels != null && settings.ForcedFfmpegChannels.Contains(channels[playerIndex].Number!));

                            bool useRemux = settings.ForceLocalRemux || 
                                (settings.ForcedFfmpegRemuxChannels != null && settings.ForcedFfmpegRemuxChannels.Contains(channels[playerIndex].Number!));
                            
                            if (useTranscode || useRemux)
                            {
                                LogDebug($"QuadPlayer {playerIndex}: Routing through local FFmpeg proxy (Transcode: {useTranscode}).");
                                streamUrl = await StartFfmpegProxyAsync(playerIndex, streamUrl, offsetSeconds, audioCodec, useTranscode);
                            }

                            var media = new Media(_libVLC, new Uri(streamUrl));

                            // Bump caching to 5 seconds to survive massive OTA signal gaps
                            media.AddOption(":network-caching=5000");
media.AddOption(":live-caching=5000");
media.AddOption(":avcodec-hw=none");

// --- THE MAGIC BULLET FOR MISSING CLOCKS ---
// Force VLC to draw the video frames even if the timestamps are broken!
media.AddOption(":no-drop-late-frames");

// Prevent corrupted subtitles from natively crashing VLC
media.AddOption(":no-spu");
media.AddOption(":no-sub-autodetect-file");

// Apply the jump-in time for Virtual Channels
if (offsetSeconds > 0)
{
    media.AddOption($":start-time={offsetSeconds}");
}

// DO NOT use a 'using' statement here! Let VLC hold the reference.
_players[playerIndex].Play(media);
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"QuadPlayer {playerIndex} Background Task Error: {ex.Message}");
                        }
                    });
                }
                else
                {
                    _borders[i].Visibility = Visibility.Collapsed;
                }
            }

            DebounceAudioSwitch();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();
        }
		
		private int GetAvailablePort()
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private async Task<string> StartFfmpegProxyAsync(int playerIndex, string sourceUrl, int offsetSeconds, string targetAudioCodec, bool useTranscode)
        {
            int dynamicPort = GetAvailablePort();
            string ffmpegBindUrl = $"http://127.0.0.1:{dynamicPort}";
            string clientUrl = $"http://127.0.0.1:{dynamicPort}/stream.ts";

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localFfmpegPath = System.IO.Path.Combine(appData, "FeralHTPC", "ffmpeg", "ffmpeg.exe");
            string targetExecutable = System.IO.File.Exists(localFfmpegPath) ? localFfmpegPath : "ffmpeg";
            
            string audioArgs = targetAudioCodec == "copy" ? "-c:a copy" : "-c:a aac -ac 2";
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

            _ffmpegProcesses[playerIndex] = new System.Diagnostics.Process { StartInfo = startInfo };
            _ffmpegProcesses[playerIndex]!.Start();
            
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool isBound = false;
            while (sw.ElapsedMilliseconds < 20000) 
            {
                if (_ffmpegProcesses[playerIndex] == null || _ffmpegProcesses[playerIndex]!.HasExited) break;
                if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(l => l.Port == dynamicPort))
                {
                    isBound = true;
                    break;
                }
                await Task.Delay(250);
            }

            return isBound ? clientUrl : sourceUrl; // Fallback to raw stream if FFmpeg fails
        }

        private void UpdateDynamicLayout()
        {
            if (_totalActive == 4)
            {
                PlayerGrid.RowDefinitions.Clear();
                PlayerGrid.ColumnDefinitions.Clear();
                PlayerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                PlayerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                PlayerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                PlayerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                SetGridPos(_borders[0], 0, 0, 1, 1, 0, false);
                SetGridPos(_borders[1], 0, 1, 1, 1, 0, false);
                SetGridPos(_borders[2], 1, 0, 1, 1, 0, false);
                SetGridPos(_borders[3], 1, 1, 1, 1, 0, false);
            }
            else if (_totalActive == 3)
            {
                PlayerGrid.RowDefinitions.Clear();
                PlayerGrid.ColumnDefinitions.Clear();
                PlayerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                PlayerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                PlayerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); // Large Left
                PlayerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Small Right

                int smallRow = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (i == _activeIndex)
                    {
                        SetGridPos(_borders[i], 0, 0, 2, 1, 0, false); // Active is large
                    }
                    else
                    {
                        SetGridPos(_borders[i], smallRow, 1, 1, 1, 0, false); // Inactive are small
                        smallRow++;
                    }
                }
            }
            else if (_totalActive == 2)
            {
                PlayerGrid.RowDefinitions.Clear();
                PlayerGrid.ColumnDefinitions.Clear();
                PlayerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                PlayerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                Border bgBorder = _borders[_activeIndex];
                Border pipBorder = _borders[_activeIndex == 0 ? 1 : 0];

                // --- THE NATIVE AIRSPACE FIX ---
                // Because LibVLC uses native hardware video surfaces, it ignores standard WPF Z-Index.
                // We must physically remove and re-add the borders to the Grid to force the OS 
                // to stack the hardware layers in the correct order (last added = top layer).
                if (PlayerGrid.Children.Contains(bgBorder)) PlayerGrid.Children.Remove(bgBorder);
                if (PlayerGrid.Children.Contains(pipBorder)) PlayerGrid.Children.Remove(pipBorder);

                PlayerGrid.Children.Add(bgBorder);   // Renders underneath
                PlayerGrid.Children.Add(pipBorder);  // Renders on top

                SetGridPos(bgBorder, 0, 0, 1, 1, 0, false); // Background Fullscreen
                SetGridPos(pipBorder, 0, 0, 1, 1, 99, true); // Foreground PiP
            }
        }

        private void SetGridPos(Border b, int row, int col, int rowSpan, int colSpan, int zIndex, bool isPip)
        {
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Grid.SetRowSpan(b, rowSpan);
            Grid.SetColumnSpan(b, colSpan);
            Panel.SetZIndex(b, zIndex);

            if (isPip)
            {
                b.HorizontalAlignment = HorizontalAlignment.Right;
                b.VerticalAlignment = VerticalAlignment.Bottom;
                b.Width = 426; // PiP Width
                b.Height = 240; // PiP Height
                b.Margin = new Thickness(0, 0, 20, 20);
                
                // --- THE FIX: Force WPF Composition Layering ---
                // This tricks the graphics card into strictly obeying the Z-Index!
                b.Opacity = 0.99; 
            }
            else
            {
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.VerticalAlignment = VerticalAlignment.Stretch;
                b.Width = double.NaN;
                b.Height = double.NaN;
                b.Margin = new Thickness(0);
                
                // Restore normal hardware rendering for the background
                b.Opacity = 1.0; 
            }
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e)
{
    if ((DateTime.Now - _lastMouseMove).TotalMilliseconds < 100) return;
    _lastMouseMove = DateTime.Now;

    if (ControlBar.Visibility != Visibility.Visible)
    {
        ControlBar.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = null; // Restore globally
    }

    _uiTimer.Stop();
    _uiTimer.Start();
}

        private void SetupGlassOverlay()
        {
            LayoutRoot.Children.Remove(OverlayGrid);

            _glassOverlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Owner = this,
                Content = OverlayGrid
            };

            _glassOverlay.PreviewKeyDown += Window_PreviewKeyDown;

// --- NEW: Safety rails for QuadPlayer ---
_glassOverlay.MouseLeave += (s, ev) => Mouse.OverrideCursor = null;
this.Deactivated += (s, ev) => Mouse.OverrideCursor = null;

this.LocationChanged += (s, e) => SyncOverlay();
            this.SizeChanged += (s, e) => SyncOverlay();
            this.StateChanged += (s, e) => SyncOverlay();

            _glassOverlay.Show();
            SyncOverlay();
        }

        private void SyncOverlay()
        {
            if (_glassOverlay != null)
            {
                if (this.WindowState == WindowState.Maximized)
                {
                    _glassOverlay.WindowState = WindowState.Maximized;
                }
                else
                {
                    _glassOverlay.WindowState = WindowState.Normal;
                    _glassOverlay.Left = this.Left;
                    _glassOverlay.Top = this.Top;
                    _glassOverlay.Width = this.ActualWidth;
                    _glassOverlay.Height = this.ActualHeight;
                }
            }
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

        private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (sender is FrameworkElement overlay)
            {
                int clickedIndex = -1;

                // If PiP mode, ensure we check the smaller foreground screen first!
                if (_totalActive == 2)
                {
                    int pipIndex = _activeIndex == 0 ? 1 : 0;
                    Point pipPos = e.GetPosition(_borders[pipIndex]);
                    if (pipPos.X >= 0 && pipPos.X <= _borders[pipIndex].ActualWidth &&
                        pipPos.Y >= 0 && pipPos.Y <= _borders[pipIndex].ActualHeight)
                    {
                        clickedIndex = pipIndex;
                    }
                    else
                    {
                        clickedIndex = _activeIndex; // Clicked the background
                    }
                }
                else
                {
                    for (int i = 0; i < _totalActive; i++)
                    {
                        Point bPos = e.GetPosition(_borders[i]);
                        if (bPos.X >= 0 && bPos.X <= _borders[i].ActualWidth &&
                            bPos.Y >= 0 && bPos.Y <= _borders[i].ActualHeight)
                        {
                            clickedIndex = i;
                            break;
                        }
                    }
                }

                if (clickedIndex != -1 && clickedIndex < _totalActive && _activeIndex != clickedIndex)
                {
                    _activeIndex = clickedIndex;
                    DebounceAudioSwitch();
                }

                Overlay_MouseMove(overlay, e);
            }
        }

        // --- NEW: Auto-Cast Auto-Fullscreen Feature ---
        private void CastButton_Click(object sender, RoutedEventArgs e)
        {
            LogDebug("UI Action: CastButton_Click triggered in QuadPlayer.");
            keybd_event(VK_LWIN, 0, 0, 0);
            keybd_event(VK_K, 0, 0, 0);
            keybd_event(VK_K, 0, KEYEVENTF_KEYUP, 0);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);

            Overlay_MouseMove(null!, null!);

            if (!_isWaitingForCast)
            {
                _isWaitingForCast = true;
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
            }
        }

        private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
        {
            LogDebug("Event: SystemEvents_DisplaySettingsChanged fired in QuadPlayer (Cast connected)");
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _isWaitingForCast = false;

            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2000);

                if (!_isFullscreen)
                {
                    ToggleFullscreen();
                }
            });
        }

        private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();
        private void BtnCC_Click(object sender, RoutedEventArgs e) => ToggleClosedCaptions();
        private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();


        public void SetActiveQuadrant(int index)
        {
            if (index >= 0 && index < _totalActive)
            {
                _activeIndex = index;
                DebounceAudioSwitch();
            }
        }

        private void DebounceAudioSwitch()
        {
            UpdateDynamicLayout();

            for (int i = 0; i < 4; i++)
            {
                _borders[i].BorderBrush = (i == _activeIndex)
                    ? (Brush)Application.Current.FindResource("StatusConnecting")
                    : Brushes.Transparent;
            }

            _audioTimer.Stop();
            _audioTimer.Start();
        }
		
		private System.Diagnostics.Process?[] _ffmpegProcesses = new System.Diagnostics.Process?[4];

        private void ApplyAudioFocus()
        {
            for (int i = 0; i < _totalActive; i++)
            {
                if (_players[i] != null)
                {
                    if (i == _activeIndex)
                    {
                        // --- THE FIX: Force the volume back up! ---
                        // VLC remembered the "Volume = 0" state from our previous test.
                        if (_players[i].Volume == 0)
                        {
                            _players[i].Volume = 100;
                        }

                        // Cycle mute to force VLC to re-evaluate the audio device
                        _players[i].Mute = true;
                        _players[i].Mute = false;

                        var tracks = _players[i].AudioTrackDescription;
                        if (tracks != null)
                        {
                            foreach (var track in tracks)
                            {
                                if (track.Id != -1)
                                {
                                    _players[i].SetAudioTrack(track.Id);
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        // Completely destroy the audio track for background players
                        _players[i].SetAudioTrack(-1);
                    }
                }
            }
        }

        public void ToggleFullscreen()
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

        public void ToggleMute()
        {
            if (_players[_activeIndex] != null)
            {
                _players[_activeIndex].Mute = !_players[_activeIndex].Mute;
            }
        }

        public void VolumeUp()
        {
            if (_players[_activeIndex] != null)
            {
                if (_players[_activeIndex].Mute) _players[_activeIndex].Mute = false;
                int newVol = _players[_activeIndex].Volume + 10;
                _players[_activeIndex].Volume = newVol > 100 ? 100 : newVol;
            }
        }

        public void VolumeDown()
        {
            if (_players[_activeIndex] != null)
            {
                int newVol = _players[_activeIndex].Volume - 10;
                _players[_activeIndex].Volume = newVol < 0 ? 0 : newVol;
            }
        }

        public void ToggleClosedCaptions()
        {
            if (_players[_activeIndex] == null) return;

            if (_players[_activeIndex].Spu == -1)
            {
                var spus = _players[_activeIndex].SpuDescription;
                if (spus != null)
                {
                    foreach (var track in spus)
                    {
                        if (track.Id > -1)
                        {
                            _players[_activeIndex].SetSpu(track.Id);
                            break;
                        }
                    }
                }
            }
            else
            {
                _players[_activeIndex].SetSpu(-1);
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                if (_isFullscreen) ToggleFullscreen();
                else this.Close();

                e.Handled = true;
                return;
            }

            if (e.Key == Key.BrowserHome)
            {
                if (Application.Current.MainWindow is MainWindow main && main.MainFrame.Content is Page page)
                {
                    page.NavigationService?.Navigate(new StartPage());
                }

                this.Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F || e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.M)
            {
                this.WindowState = WindowState.Minimized;
                e.Handled = true;
                return;
            }

            if (e.Key == Key.MediaStop)
            {
                if (_isFullscreen) ToggleFullscreen();
                else this.Close();
                e.Handled = true;
                return;
            }

            int previousIndex = _activeIndex;

            // --- NEW: Dynamic Navigation Logic ---
            if (_totalActive == 4)
            {
                // Original 2x2 logic
                if (e.Key == Key.Left) { if (_activeIndex == 1) _activeIndex = 0; else if (_activeIndex == 3) _activeIndex = 2; }
                else if (e.Key == Key.Right) { if (_activeIndex == 0) _activeIndex = 1; else if (_activeIndex == 2) _activeIndex = 3; }
                else if (e.Key == Key.Up) { if (_activeIndex == 2) _activeIndex = 0; else if (_activeIndex == 3) _activeIndex = 1; }
                else if (e.Key == Key.Down) { if (_activeIndex == 0) _activeIndex = 2; else if (_activeIndex == 1) _activeIndex = 3; }
            }
            else
            {
                // Simple cycle for 2 or 3 channels (left/up goes back, right/down goes forward)
                if (e.Key == Key.Right || e.Key == Key.Down)
                {
                    _activeIndex = (_activeIndex + 1) % _totalActive;
                }
                else if (e.Key == Key.Left || e.Key == Key.Up)
                {
                    _activeIndex = (_activeIndex - 1 + _totalActive) % _totalActive;
                }
            }
            // -------------------------------------

            if (e.Key == Key.VolumeMute) { ToggleMute(); e.Handled = true; return; }
            else if (e.Key == Key.VolumeUp) { VolumeUp(); e.Handled = true; return; }
            else if (e.Key == Key.VolumeDown) { VolumeDown(); e.Handled = true; return; }

            if (previousIndex != _activeIndex)
            {
                DebounceAudioSwitch();
                e.Handled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
{
    LogDebug("QuadPlayerWindow: OnClosed triggered. Cleaning up resources.");
    Mouse.OverrideCursor = null; // Restore globally
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;

            _audioTimer?.Stop();
            _uiTimer?.Stop();

            if (_glassOverlay != null)
            {
                _glassOverlay.Close();
            }

            // --- THE FIX: Detach before stopping in background! ---
            var playersToDispose = _players.ToArray();

            for (int i = 0; i < 4; i++)
            {
                if (_views[i] != null) _views[i].MediaPlayer = null;
            }

            Task.Run(() =>
            {
                // --- NEW: Kill all FFmpeg proxies ---
                for (int i = 0; i < 4; i++)
                {
                    try { _ffmpegProcesses[i]?.Kill(); } catch { }
                    _ffmpegProcesses[i]?.Dispose();
                }

                for (int i = 0; i < 4; i++)
                {
                    if (playersToDispose[i] != null)
                    {
                        if (playersToDispose[i].IsPlaying) playersToDispose[i].Stop();
                        System.Threading.Thread.Sleep(50); // Let VLC release D3D locks
                        playersToDispose[i].Dispose();
                    }
                }
                                
                LogDebug("QuadPlayerWindow: Background teardown complete.");
            });

            base.OnClosed(e);
            Application.Current.MainWindow?.Show();
            Application.Current.MainWindow?.Activate();
        }
    }
}
