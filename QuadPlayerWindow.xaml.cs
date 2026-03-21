using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace FeralCode
{
    public partial class QuadPlayerWindow : Window
    {
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer[] _players = new LibVLCSharp.Shared.MediaPlayer[4];
        private LibVLCSharp.WPF.VideoView[] _views;
        private Border[] _borders;
        private int _activeIndex = 0;
        private int _totalActive = 0;
        
        private bool _isFullscreen = true;
        private DispatcherTimer _audioTimer;
        
        // --- NEW: The Glass Overlay Window ---
        private Window? _glassOverlay;

        public QuadPlayerWindow(string baseUrl, List<Channel> channels)
        {
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
            };
            
            _libVLC = MainWindow.SharedLibVLC;
            _totalActive = channels.Count;

            _views = new[] { View0, View1, View2, View3 };
            _borders = new[] { Border0, Border1, Border2, Border3 };

            // Initialize the 250ms debouncer to protect the audio pipeline!
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
                    _players[i] = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                    _views[i].MediaPlayer = _players[i];

                    string audioCodec = "copy";
                    if (double.TryParse(channels[i].Number, out double chNum) && chNum >= 100 && chNum < 200)
                    {
                        audioCodec = "aac";
                    }

                    string streamUrl = $"{baseUrl.TrimEnd('/')}/devices/ANY/channels/{channels[i].Number}/hls/master.m3u8?vcodec=copy&acodec={audioCodec}";
                    var media = new Media(_libVLC, new Uri(streamUrl));
                    
                    media.AddOption(":network-caching=2000");
                    media.AddOption(":live-caching=2000");
                    media.AddOption(":http-reconnect");
                    media.AddOption(":avcodec-hw=none");
                    
                    _players[i].Playing += async (sender, args) => 
                    {
                        await System.Threading.Tasks.Task.Delay(1000); 
                        Application.Current.Dispatcher.Invoke(() => ApplyAudioFocus());
                        
                        await System.Threading.Tasks.Task.Delay(2500); 
                        Application.Current.Dispatcher.Invoke(() => ApplyAudioFocus());
                    };
                    
                    using (media)
                    {
                        _players[i].Play(media);
                    }
                }
                else
                {
                    _borders[i].Visibility = Visibility.Collapsed;
                }
            }

            DebounceAudioSwitch();
        }

        // --- THE MOUSE FIX: Floating Glass Overlay ---
        private void SetupGlassOverlay()
        {
            // Create a completely transparent window that floats above VLC
            _glassOverlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), // 1% opacity black catches clicks!
                ShowInTaskbar = false,
                ShowActivated = false,
                Owner = this // This links it to the main window so they minimize/maximize together
            };

            _glassOverlay.PreviewMouseLeftButtonDown += GlassOverlay_PreviewMouseLeftButtonDown;
            
            // Pass any stray keyboard presses through to our main logic
            _glassOverlay.PreviewKeyDown += Window_PreviewKeyDown;

            // Keep the glass exactly matched to the video player size
            this.LocationChanged += (s, e) => SyncOverlay();
            this.SizeChanged += (s, e) => SyncOverlay();
            
            _glassOverlay.Show();
            SyncOverlay();
        }

        private void SyncOverlay()
        {
            if (_glassOverlay != null)
            {
                _glassOverlay.Left = this.Left;
                _glassOverlay.Top = this.Top;
                _glassOverlay.Width = this.ActualWidth;
                _glassOverlay.Height = this.ActualHeight;
            }
        }

        private void GlassOverlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (e.ClickCount == 1)
            {
                Point pos = e.GetPosition(_glassOverlay);
                
                int col = pos.X < (_glassOverlay.ActualWidth / 2) ? 0 : 1;
                int row = pos.Y < (_glassOverlay.ActualHeight / 2) ? 0 : 1;
                int index = (row * 2) + col;

                if (index < _totalActive && _activeIndex != index)
                {
                    _activeIndex = index;
                    DebounceAudioSwitch();
                }
                e.Handled = true;
            }
        }

        public void SetActiveQuadrant(int index)
        {
            if (index >= 0 && index < _totalActive)
            {
                _activeIndex = index;
                DebounceAudioSwitch();
            }
        }

        // 1. Move the visual border INSTANTLY
        private void DebounceAudioSwitch()
        {
            for (int i = 0; i < 4; i++)
            {
                _borders[i].BorderBrush = (i == _activeIndex) 
                    ? (Brush)Application.Current.FindResource("StatusConnecting") 
                    : Brushes.Transparent;
            }
            
            // Start the 250ms countdown to switch the actual audio tracks safely
            _audioTimer.Stop();
            _audioTimer.Start();
        }

        // --- THE AUDIO FIX: Safe Hardware Track Switching ---
        private void ApplyAudioFocus()
        {
            for (int i = 0; i < _totalActive; i++)
            {
                if (_players[i] != null)
                {
                    if (i == _activeIndex)
                    {
                        // Jiggle the mute to ensure Windows recognizes it
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
                        // Safely kill the background decoders to prevent audio bleed
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

        // --- API Gateway Methods ---
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

            if (e.Key == Key.Left)
            {
                if (_activeIndex == 1) _activeIndex = 0;
                else if (_activeIndex == 3) _activeIndex = 2;
            }
            else if (e.Key == Key.Right)
            {
                if (_activeIndex == 0 && _totalActive > 1) _activeIndex = 1;
                else if (_activeIndex == 2 && _totalActive > 3) _activeIndex = 3;
            }
            else if (e.Key == Key.Up)
            {
                if (_activeIndex == 2) _activeIndex = 0;
                else if (_activeIndex == 3) _activeIndex = 1;
            }
            else if (e.Key == Key.Down)
            {
                if (_activeIndex == 0 && _totalActive > 2) _activeIndex = 2;
                else if (_activeIndex == 1 && _totalActive > 3) _activeIndex = 3;
            }
            
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
            _audioTimer?.Stop();

            // Destroy the glass overlay safely
            if (_glassOverlay != null)
            {
                _glassOverlay.Close();
            }

            for (int i = 0; i < 4; i++)
            {
                if (_players[i] != null)
                {
                    if (_players[i].IsPlaying) _players[i].Stop();
                    _players[i].Dispose();
                }
            }
            base.OnClosed(e);
            Application.Current.MainWindow?.Show();
            Application.Current.MainWindow?.Activate();
        }
    }
}