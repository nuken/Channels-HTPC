using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        
        // Tracking Fullscreen state
        private bool _isFullscreen = true;

        public QuadPlayerWindow(string baseUrl, List<Channel> channels)
        {
            InitializeComponent();
            _libVLC = MainWindow.SharedLibVLC;
            _totalActive = channels.Count;

            _views = new[] { View0, View1, View2, View3 };
            _borders = new[] { Border0, Border1, Border2, Border3 };

            for (int i = 0; i < 4; i++)
            {
                if (i < channels.Count)
                {
                    _players[i] = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                    _views[i].MediaPlayer = _players[i];

                    // Force Windows to leave the app's master volume alone!
                    _players[i].Mute = false;
                    _players[i].Volume = 100;

                    string streamUrl = $"{baseUrl.TrimEnd('/')}/devices/ANY/channels/{channels[i].Number}/hls/master.m3u8?vcodec=copy&acodec=copy";
                    var media = new Media(_libVLC, new Uri(streamUrl));
                    
                    media.AddOption(":network-caching=2000");
                    media.AddOption(":live-caching=2000");
                    media.AddOption(":http-reconnect");
                    media.AddOption(":avcodec-hw=none");
                    
                    // Wait for the stream to connect and parse its tracks, then apply focus
                    _players[i].Playing += async (sender, args) => 
                    {
                        await System.Threading.Tasks.Task.Delay(1500); 
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            UpdateFocus();
                        });
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

            UpdateFocus();
        }
		
		// --- NEW: API Gateway for the Mobile Remote ---
        public void SetActiveQuadrant(int index)
        {
            if (index >= 0 && index < _totalActive)
            {
                _activeIndex = index;
                UpdateFocus();
            }
        }

        private void UpdateFocus()
        {
            for (int i = 0; i < _totalActive; i++)
            {
                if (_players[i] != null)
                {
                    if (i == _activeIndex)
                    {
                        // UNMUTE: Find the active audio track and turn the decoder ON
                        var tracks = _players[i].AudioTrackDescription;
                        if (tracks != null)
                        {
                            foreach (var track in tracks)
                            {
                                if (track.Id != -1) // -1 is the "Disabled" track
                                {
                                    _players[i].SetAudioTrack(track.Id);
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        // MUTE: Force the decoder to turn the audio track OFF (-1)
                        _players[i].SetAudioTrack(-1);
                    }
                }
                
                // Highlight the active quadrant
                _borders[i].BorderBrush = (i == _activeIndex) 
                    ? (Brush)Application.Current.FindResource("StatusConnecting") 
                    : Brushes.Transparent;
            }
        }
        
        // --- NEW: Double Click to Toggle Fullscreen ---
        private void UniformGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }

        // --- NEW: Toggle Fullscreen Logic ---
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

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape || e.Key == Key.Back || e.Key == Key.BrowserBack)
            {
                // NEW: If they hit escape while fullscreen, just drop to a window instead of instantly killing the streams!
                if (_isFullscreen) 
                {
                    ToggleFullscreen();
                }
                else 
                {
                    this.Close();
                }
                e.Handled = true;
                return;
            }
            
            // --- NEW: Hardware Hotkeys ---
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

            // D-Pad Navigation between the quadrants
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
            
            if (e.Key == Key.VolumeMute)
            {
                if (_players[_activeIndex] != null)
                {
                    _players[_activeIndex].Mute = !_players[_activeIndex].Mute;
                }
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.VolumeUp)
            {
                if (_players[_activeIndex] != null)
                {
                    if (_players[_activeIndex].Mute) _players[_activeIndex].Mute = false;
                    int newVol = _players[_activeIndex].Volume + 10;
                    _players[_activeIndex].Volume = newVol > 100 ? 100 : newVol;
                }
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.VolumeDown)
            {
                if (_players[_activeIndex] != null)
                {
                    int newVol = _players[_activeIndex].Volume - 10;
                    _players[_activeIndex].Volume = newVol < 0 ? 0 : newVol;
                }
                e.Handled = true;
                return;
            }

            if (previousIndex != _activeIndex)
            {
                UpdateFocus();
                e.Handled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            for (int i = 0; i < 4; i++)
            {
                if (_players[i] != null)
                {
                    if (_players[i].IsPlaying) _players[i].Stop();
                    _players[i].Dispose();
                }
            }
            base.OnClosed(e);
        }
    }
}