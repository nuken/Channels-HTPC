using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using LibVLCSharp.Shared;
using System.Linq;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.FileProviders;
using System.Reflection;
using System.IO;
using System.Diagnostics;

namespace FeralCode
{
    public partial class MainWindow : Window
    {
        public static LibVLC SharedLibVLC { get; private set; } = null!;
        
        private WebApplication? _webHost;

        private PlayerWindow? _activePlayerWindow;
        public PlayerWindow? ActivePlayerWindow 
        { 
            get => _activePlayerWindow;
            set
            {
                _activePlayerWindow = value;
                if (_activePlayerWindow != null)
                {
                    this.Hide(); 

                    _activePlayerWindow.Closed += (s, e) => 
                    {
                        Application.Current.Dispatcher.InvokeAsync(() => 
                        {
                            if (ActivePlayerWindow == null)
                            {
                                this.Show();     
                                this.Activate(); 
                            }
                        }, System.Windows.Threading.DispatcherPriority.Normal);
                    };
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
        
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        public struct POINT { public int X; public int Y; }
        public class MouseDelta { public int dx { get; set; } public int dy { get; set; } }

        public MainWindow()
        {
            InitializeComponent();
            Core.Initialize(); 
            AppLogger.IsEnabled = SettingsManager.Load().EnableDebugLogging;
            AppLogger.Log("=== APPLICATION STARTED ===");

            SharedLibVLC = new LibVLC(
                "--no-mouse-events",    
                "--no-keyboard-events", 
                "--avcodec-threads=0",  
                "--network-caching=3000",
                "--live-caching=3000",
                "--file-caching=3000"
            );

            // A list of harmless native VLC errors/warnings to hide from end-users
            string[] ignoredVlcMessages = new[]
            {
                "SetThumbNailClip failed",           // Harmless D3D11 error caused by WindowStyle="None"
                "unsupported control query",         // Harmless WPF integration warning
                "too low audio sample frequency",    // Normal during initial tuner lock
                "failed to create audio output",     // Normal during initial tuner lock
                "non-dated audio buffer received",   // Normal stream transition warning
                "Timestamp conversion failed",       // Harmless missing clock warning
                "Could not convert timestamp",       // Harmless missing clock warning
                "buffer deadlock prevented",         // Harmless auto-recovery from weak signal
                "buffer too late",                   // Harmless auto-recovery from weak signal
                "mpeg4audio: AAC channels",          // Harmless audio format notice
                "No PCR received",                   // Harmless missing clock warning
                "Broken stream",                     // Harmless clock delay workaround
                "waiting for SPS/PPS"                // Harmless waiting for H.264 keyframe
            };
           
            SharedLibVLC.Log += (sender, e) => 
            {
                if (e.Level == LogLevel.Warning || e.Level == LogLevel.Error || e.Level == LogLevel.Notice)
                {
                    bool isIgnored = ignoredVlcMessages.Any(ignored => 
                        e.Message.Contains(ignored, StringComparison.OrdinalIgnoreCase));
                    
                    if (!isIgnored)
                    {
                        AppLogger.Log($"[VLC ENGINE] [{e.Level}] {e.Module}: {e.Message}");
                    }
                }
            };

            StartWebServer();
            MainFrame.Navigate(new StartPage());

            this.PreviewKeyDown += MainWindow_PreviewKeyDown;

            this.Activated += (s, e) =>
            {
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (System.Windows.Input.Keyboard.FocusedElement == null || 
                        System.Windows.Input.Keyboard.FocusedElement is Window || 
                        System.Windows.Input.Keyboard.FocusedElement is Frame)
                    {
                        if (MainFrame.Content is UIElement page)
                        {
                            page.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Input);
            };
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            bool isNavKey = e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down || 
                            e.Key == System.Windows.Input.Key.Left || e.Key == System.Windows.Input.Key.Right ||
                            e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.BrowserBack;

            if (isNavKey && (System.Windows.Input.Keyboard.FocusedElement == null || 
                             System.Windows.Input.Keyboard.FocusedElement is Window || 
                             System.Windows.Input.Keyboard.FocusedElement is Frame))
            {
                if (MainFrame.Content is UIElement page)
                {
                    page.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
                    
                    if (e.Key == System.Windows.Input.Key.Back || e.Key == System.Windows.Input.Key.BrowserBack)
                    {
                        var keyArgs = new System.Windows.Input.KeyEventArgs(
                            System.Windows.Input.Keyboard.PrimaryDevice, 
                            System.Windows.PresentationSource.FromVisual(this) ?? new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero), 
                            0, e.Key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                        page.RaiseEvent(keyArgs);
                        e.Handled = true; 
                    }
                }
            }
        }

        protected override async void OnClosed(EventArgs e)
        {
            if (_webHost != null)
            {
                await _webHost.StopAsync();
                await _webHost.DisposeAsync();
            }
            base.OnClosed(e);
        }

        private bool IsPortAvailable(int port)
{
    try
    {
        // Changed from IPAddress.Loopback to IPAddress.Any
        // This ensures it checks all network interfaces, matching Kestrel's behavior.
        using (var tcpListener = new TcpListener(IPAddress.Any, port))
        {
            tcpListener.Start();
            tcpListener.Stop();
            return true;
        }
    }
    catch
    {
        return false;
    }
}

        private int GetAvailablePort(int startPort = 12345, int endPort = 12445)
        {
            for (int port = startPort; port <= endPort; port++)
            {
                if (IsPortAvailable(port)) return port;
            }
            throw new Exception($"No available ports found in the range {startPort}-{endPort}.");
        }

        private void StartWebServer()
        {
            Task.Run(async () =>
            {
                try
                {
                    var settings = SettingsManager.Load();
                    
                    int portToUse = settings.WebServerPort == 0 ? 12345 : settings.WebServerPort;

                    int retry = 0;
                    while (!IsPortAvailable(portToUse) && retry < 3)
                    {
                        await Task.Delay(1000);
                        retry++;
                    }

                    if (!IsPortAvailable(portToUse))
                    {
                        portToUse = GetAvailablePort(12345, 12445); 
                        settings.WebServerPort = portToUse;
                        SettingsManager.Save(settings);
                    }

                    var options = new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory };
                    var builder = WebApplication.CreateBuilder(options);
                    
                    builder.WebHost.UseUrls($"http://*:{portToUse}");

                    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(opt => 
                    {
                        opt.SerializerOptions.IncludeFields = true;
                        opt.SerializerOptions.PropertyNamingPolicy = null; 
                    });

                    _webHost = builder.Build();

                    var embeddedProvider = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
                    _webHost.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embeddedProvider });
                    _webHost.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedProvider });

                    _webHost.MapGet("/api/remote/collections", async () => 
                    {
                        var settings = SettingsManager.Load();
                        if (string.IsNullOrWhiteSpace(settings.LastServerAddress)) return Results.Ok(new string[] { "All Channels" });
                        
                        try {
                            var api = new ChannelsApi();
                            var collections = await api.GetChannelCollectionsAsync(settings.LastServerAddress);
                            var names = collections.Select(c => c.name).ToList();
                            names.Insert(0, "All Channels");
                            return Results.Ok(names);
                        } catch { return Results.Ok(new string[] { "All Channels" }); }
                    });

                    _webHost.MapGet("/api/remote/guide", async (string? collection, string? search) => 
                    {
                        var settings = SettingsManager.Load();
                        if (string.IsNullOrWhiteSpace(settings.LastServerAddress)) return Results.Ok(new object[] { });
                        
                        try {
                            var api = new ChannelsApi();
                            var channels = await api.GetChannelsAsync(settings.LastServerAddress);
                            
                            var stations = await api.GetStationsAsync(settings.LastServerAddress);
                            var stationLogoDict = stations
                                .Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Logo))
                                .GroupBy(s => s.Id!)
                                .ToDictionary(g => g.Key, g => g.First().Logo!);

                            var guide = await api.GetGuideAsync(settings.LastServerAddress, 1);
                            
                            foreach (var c in channels)
                            {
                                string targetId = !string.IsNullOrWhiteSpace(c.StationId) ? c.StationId : c.CallSign;
                                if (string.IsNullOrWhiteSpace(c.ImageUrl) && !string.IsNullOrWhiteSpace(targetId))
                                {
                                    if (stationLogoDict.TryGetValue(targetId, out string? mappedLogo)) c.ImageUrl = mappedLogo;
                                }

                                if (!string.IsNullOrWhiteSpace(c.ImageUrl))
                                {
                                    if (c.ImageUrl.StartsWith("/")) 
                                        c.ImageUrl = $"{settings.LastServerAddress.TrimEnd('/')}{c.ImageUrl}";
                                    else if (c.ImageUrl.StartsWith("tmsimg://", StringComparison.OrdinalIgnoreCase))
                                        c.ImageUrl = c.ImageUrl.Replace("tmsimg://", $"{settings.LastServerAddress.TrimEnd('/')}/tmsimg/", StringComparison.OrdinalIgnoreCase);
                                }

                                var match = guide.FirstOrDefault(g => g.ChannelNumber == c.Number);
                                if (match != null && match.Airings != null) c.CurrentAirings = match.Airings;
                            }
                            
                            if (!string.IsNullOrEmpty(collection) && collection != "All Channels")
                            {
                                var collections = await api.GetChannelCollectionsAsync(settings.LastServerAddress);
                                var selected = collections.FirstOrDefault(c => c.name == collection);
                                if (selected != null && selected.items != null)
                                {
                                    channels = channels.Where(c => selected.items.Any(item => c.HasIdentifier(item))).ToList();
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(search))
                            {
                                channels = channels.Where(c => c.HasIdentifier(search)).ToList();
                            }

                            return Results.Ok(channels);
                        } catch { return Results.Ok(new object[] { }); }
                    });

                    _webHost.MapPost("/api/remote/play/{channelNumber}", async (string channelNumber) => 
                    {
                        var settings = SettingsManager.Load();
                        var api = new ChannelsApi();
                        var channels = await api.GetChannelsAsync(settings.LastServerAddress);
                        
                        int startIndex = channels.FindIndex(c => c.Number == channelNumber);
                        if (startIndex == -1) startIndex = 0;

                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            if (ActivePlayerWindow != null) ActivePlayerWindow.Close();

                            ActivePlayerWindow = new PlayerWindow(settings.LastServerAddress, channels, startIndex);
                            ActivePlayerWindow.Closed += (s, args) => ActivePlayerWindow = null;
                            ActivePlayerWindow.Show();
                        });
                        return Results.Ok();
                    });

                    _webHost.MapPost("/api/remote/home", () => 
                    { 
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            var quadWindow = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quadWindow != null) quadWindow.Close();

                            if (ActivePlayerWindow != null) ActivePlayerWindow.Close();
                            
                            if (MainFrame.Content is Page page)
                            {
                                if (page.NavigationService.CanGoBack) 
                                    page.NavigationService.GoBack();
                                else if (page is not StartPage) 
                                    page.NavigationService.Navigate(new StartPage());
                            }
                        }); 
                        return Results.Ok(); 
                    });
                    
                    _webHost.MapPost("/api/remote/multiview/setup", () => 
                    {
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            if (ActivePlayerWindow != null) ActivePlayerWindow.Close();
                            
                            var quadWindow = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quadWindow != null) quadWindow.Close();

                            if (MainFrame.Content is not MultiviewSetupPage)
                            {
                                MainFrame.Navigate(new MultiviewSetupPage());
                            }
                            
                            Application.Current.MainWindow.Activate();
                        });
                        return Results.Ok();
                    });
                    
                    _webHost.MapPost("/api/remote/multiview/audio/{index}", (int index) => 
                    {
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            var quadWindow = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quadWindow != null && quadWindow.IsVisible)
                            {
                                quadWindow.SetActiveQuadrant(index);
                            }
                        });
                        return Results.Ok();
                    });

                    _webHost.MapPost("/api/remote/minimize", () => 
                    {
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            var quadWindow = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();

                            Window targetWindow = Application.Current.MainWindow;
                            if (quadWindow != null && quadWindow.IsVisible) targetWindow = quadWindow;
                            else if (ActivePlayerWindow != null && ActivePlayerWindow.IsVisible) targetWindow = ActivePlayerWindow;

                            if (targetWindow.WindowState == WindowState.Minimized)
                            {
                                targetWindow.WindowState = WindowState.Normal;
                                targetWindow.Show();
                                targetWindow.Activate();
                            }
                            else
                            {
                                targetWindow.WindowState = WindowState.Minimized;
                            }
                        });
                        return Results.Ok();
                    });
                    
                    _webHost.MapPost("/api/remote/stop", () => { 
                        Application.Current.Dispatcher.Invoke(() => {
                            var quad = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quad != null) quad.Close();
                            else ActivePlayerWindow?.Close();
                        }); 
                        return Results.Ok(); 
                    });

                    _webHost.MapPost("/api/toggle_pip", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.TogglePiP()); return Results.Ok(); });
                    
                    _webHost.MapPost("/api/remote/cc", () => { 
                        Application.Current.Dispatcher.Invoke(() => {
                            var quad = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quad != null) quad.ToggleClosedCaptions();
                            else ActivePlayerWindow?.ToggleClosedCaptions();
                        }); 
                        return Results.Ok(); 
                    });

                    _webHost.MapPost("/api/remote/volup", () => { 
                        Application.Current.Dispatcher.Invoke(() => {
                            var quad = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quad != null) quad.VolumeUp();
                            else ActivePlayerWindow?.VolumeUp();
                        }); 
                        return Results.Ok(); 
                    });

                    _webHost.MapPost("/api/remote/voldown", () => { 
                        Application.Current.Dispatcher.Invoke(() => {
                            var quad = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quad != null) quad.VolumeDown();
                            else ActivePlayerWindow?.VolumeDown();
                        }); 
                        return Results.Ok(); 
                    });

                    _webHost.MapPost("/api/remote/mute", () => { 
                        Application.Current.Dispatcher.Invoke(() => {
                            var quad = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quad != null) quad.ToggleMute();
                            else ActivePlayerWindow?.ToggleMute();
                        }); 
                        return Results.Ok(); 
                    });

                    _webHost.MapPost("/api/remote/stats", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.ToggleStats()); return Results.Ok(); });
                    
                    _webHost.MapPost("/api/remote/fullscreen", () =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var quad = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quad != null)
                            {
                                quad.ToggleFullscreen();
                            }
                            else if (ActivePlayerWindow != null)
                            {
                                var keyArgs = new System.Windows.Input.KeyEventArgs(
                                    System.Windows.Input.Keyboard.PrimaryDevice, 
                                    System.Windows.PresentationSource.FromVisual(ActivePlayerWindow) ?? new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero), 
                                    0, System.Windows.Input.Key.F) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                                ActivePlayerWindow.RaiseEvent(keyArgs);
                            }
                        });
                        return Results.Ok();
                    });

                    _webHost.MapPost("/api/remote/key/{direction}", (string direction) =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Window targetWindow = Application.Current.MainWindow;

                            var quadWindow = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            
                            if (quadWindow != null && quadWindow.IsVisible)
                            {
                                targetWindow = quadWindow;
                            }
                            else if (ActivePlayerWindow != null && ActivePlayerWindow.IsVisible)
                            {
                                targetWindow = ActivePlayerWindow;
                                if (ActivePlayerWindow.HandleRemoteKey(direction)) return; 
                            }

                            if (!targetWindow.IsActive) targetWindow.Activate();
                            
                            byte vk = 0;
                            if (direction == "up") vk = 0x26;
                            else if (direction == "down") vk = 0x28;
                            else if (direction == "left") vk = 0x25;
                            else if (direction == "right") vk = 0x27;
                            else if (direction == "enter") vk = 0x0D;
                            else if (direction == "back") vk = 0x08;

                            if (vk != 0)
                            {
                                keybd_event(vk, 0, 0, 0); 
                                keybd_event(vk, 0, 0x0002, 0); 
                            }
                        });
                        return Results.Ok();
                    });
                    
                    _webHost.MapGet("/api/remote/movies", async () => 
                    {
                        var settings = SettingsManager.Load();
                        if (string.IsNullOrWhiteSpace(settings.LastServerAddress)) 
                            return Results.Ok(new List<Movie>());

                        var api = new ChannelsApi();
                        var movies = await api.GetMoviesAsync(settings.LastServerAddress);
                        return Results.Ok(movies);
                    });

                    _webHost.MapPost("/api/remote/playmovie/{id}", async (string id) =>
                    {
                        var settings = SettingsManager.Load();
                        var api = new ChannelsApi();
                        var movies = await api.GetMoviesAsync(settings.LastServerAddress);
                        var selectedMovie = movies.FirstOrDefault(m => m.Id == id);

                        if (selectedMovie != null)
                        {
                            Application.Current.Dispatcher.Invoke(() => 
                            {
                                string streamUrl = $"{settings.LastServerAddress.TrimEnd('/')}/dvr/files/{selectedMovie.Id}/hls/master.m3u8?vcodec=copy&acodec=copy";
                                
                                if (ActivePlayerWindow != null) ActivePlayerWindow.Close();

                                ActivePlayerWindow = new PlayerWindow(streamUrl, selectedMovie.Title, selectedMovie.PosterUrl, selectedMovie.Commercials);
                                ActivePlayerWindow.Closed += (s, args) => ActivePlayerWindow = null; 
                                ActivePlayerWindow.Show();
                            });
                            return Results.Ok();
                        }
                        return Results.NotFound();
                    });

                    _webHost.MapPost("/api/remote/toggleview", () =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var quad = Application.Current.Windows.OfType<QuadPlayerWindow>().FirstOrDefault();
                            if (quad != null)
                            {
                                if (quad.IsActive) Application.Current.MainWindow.Activate();
                                else quad.Activate();
                            }
                            else if (ActivePlayerWindow != null)
                            {
                                if (ActivePlayerWindow.IsActive) Application.Current.MainWindow.Activate();
                                else ActivePlayerWindow.Activate();
                            }
                        });
                        return Results.Ok();
                    });

                    _webHost.MapPost("/api/remote/scrub/start/{direction}", (string direction) =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (ActivePlayerWindow != null && ActivePlayerWindow.IsVisible)
                                ActivePlayerWindow.StartRemoteScrub(direction);
                        });
                        return Results.Ok();
                    });
                    
                    _webHost.MapPost("/api/remote/scrub/stop", () =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (ActivePlayerWindow != null && ActivePlayerWindow.IsVisible)
                                ActivePlayerWindow.StopRemoteScrub();
                        });
                        return Results.Ok();
                    });
                    
                    _webHost.MapPost("/api/system/mouse", async (HttpContext context) =>
                    {
                        var delta = await context.Request.ReadFromJsonAsync<MouseDelta>();
                        if (delta != null && GetCursorPos(out POINT p))
                        {
                            SetCursorPos(p.X + delta.dx, p.Y + delta.dy);
                        }
                        return Results.Ok();
                    });

                    _webHost.MapPost("/api/system/click/{type}", (string type) =>
                    {
                        if (type == "left") 
                        {
                            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                        }
                        else if (type == "right")
                        {
                            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                        }
                        return Results.Ok();
                    });
                    
                    _webHost.MapGet("/api/remote/shows", async () => 
                    {
                        var settings = SettingsManager.Load();
                        if (string.IsNullOrWhiteSpace(settings.LastServerAddress)) return Results.Ok(new List<TvShow>());
                        var api = new ChannelsApi();
                        return Results.Ok(await api.GetShowsAsync(settings.LastServerAddress));
                    });

                    _webHost.MapGet("/api/remote/episodes", async (string? showId) => 
                    {
                        showId ??= ""; 

                        var settings = SettingsManager.Load();
                        if (string.IsNullOrWhiteSpace(settings.LastServerAddress)) return Results.Ok(new List<Episode>());
                        
                        var api = new ChannelsApi();
                        var allEpisodes = await api.GetEpisodesAsync(settings.LastServerAddress);
                        
                        var showEpisodes = allEpisodes.Where(e => e.ShowId == showId)
                                                      .OrderByDescending(e => e.SeasonNumber)
                                                      .ThenByDescending(e => e.EpisodeNumber).ToList();
                        
                        return Results.Ok(showEpisodes);
                    });

                    _webHost.MapPost("/api/remote/playepisode/{id}", async (string id) =>
                    {
                        var settings = SettingsManager.Load();
                        var api = new ChannelsApi();
                        var allEpisodes = await api.GetEpisodesAsync(settings.LastServerAddress);
                        var ep = allEpisodes.FirstOrDefault(e => e.Id == id);

                        if (ep != null)
                        {
                            Application.Current.Dispatcher.Invoke(() => 
                            {
                                string streamUrl = $"{settings.LastServerAddress.TrimEnd('/')}/dvr/files/{ep.Id}/hls/master.m3u8?vcodec=copy&acodec=copy";
                                string displayTitle = $"{ep.Title} - S{ep.SeasonNumber:D2}E{ep.EpisodeNumber:D2} - {ep.EpisodeTitle}";
                                
                                if (ActivePlayerWindow != null) ActivePlayerWindow.Close();

                                ActivePlayerWindow = new PlayerWindow(streamUrl, displayTitle, ep.ImageUrl, ep.Commercials);
                                ActivePlayerWindow.Closed += (s, args) => ActivePlayerWindow = null; 
                                ActivePlayerWindow.Show();
                            });
                            return Results.Ok();
                        }
                        return Results.NotFound();
                    });
                    
                    _webHost.MapGet("/api/remote/apps", () => 
                    {
                        var settings = SettingsManager.Load();
                        return Results.Ok(settings.ExternalStreams);
                    });

                    _webHost.MapPost("/api/remote/playapp/{id}", async (string id) =>
                    {
                        var settings = SettingsManager.Load();
                        var stream = settings.ExternalStreams.FirstOrDefault(s => s.Id == id);
                        
                        if (stream != null)
                        {
                            // 1. Launch the app on the UI thread
                            Application.Current.Dispatcher.Invoke(() => 
                            {
                                try
                                {
                                    var windowStyle = settings.StartPlayersFullscreen ? ProcessWindowStyle.Maximized : ProcessWindowStyle.Normal;

                                    if (stream.Service.ToLower() == "netflix")
                                    {
                                        string input = stream.StreamId.Trim();
                                        string finalUrl = input.StartsWith("http") ? input : $"https://www.netflix.com/watch/{input}";
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "msedge", Arguments = $"--app=\"{finalUrl}\" --start-fullscreen", UseShellExecute = true, WindowStyle = windowStyle });
                                    }
                                    else if (stream.Service.ToLower() == "disney+")
                                    {
                                        string input = stream.StreamId.Trim();
                                        string finalUrl = input.StartsWith("http") ? input : $"https://www.disneyplus.com/play/{input}";
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "msedge", Arguments = $"--app=\"{finalUrl}\" --start-fullscreen", UseShellExecute = true, WindowStyle = windowStyle });
                                    }
                                    else if (stream.Service.ToLower() == "youtube")
                                    {
                                        string input = stream.StreamId.Trim();
                                        string finalUrl = (string.IsNullOrWhiteSpace(input) || input.Equals("app", StringComparison.OrdinalIgnoreCase)) 
                                            ? "https://www.youtube.com/tv" 
                                            : (input.StartsWith("http") ? input : $"https://www.youtube.com/watch?v={input}");

                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { 
                                            FileName = "msedge", 
                                            Arguments = $"--app=\"{finalUrl}\" --start-fullscreen", 
                                            UseShellExecute = true, 
                                            WindowStyle = windowStyle 
                                        });
                                    }
                                    else
                                    {
                                        // Custom URI
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(stream.StreamId.Trim()) { UseShellExecute = true, WindowStyle = windowStyle });
                                    }
                                }
                                catch { }
                            });

                            // 2. Inject Keystrokes for Fullscreen Asynchronously (so UI doesn't freeze)
                            if (settings.StartPlayersFullscreen)
                            {
                                await Task.Delay(1500); 
                                
                                keybd_event(0x7A, 0, 0, 0);      
                                await Task.Delay(50);
                                keybd_event(0x7A, 0, 0x0002, 0); 

                                if (stream.Service.ToLower() == "youtube")
                                {
                                    await Task.Delay(2500); 

                                    int screenWidth = (int)SystemParameters.PrimaryScreenWidth;
                                    int screenHeight = (int)SystemParameters.PrimaryScreenHeight;

                                    SetCursorPos(screenWidth / 2, screenHeight / 3);

                                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

                                    await Task.Delay(100); 

                                    keybd_event(0x46, 0, 0, 0);      
                                    await Task.Delay(50);
                                    keybd_event(0x46, 0, 0x0002, 0); 
                                    
                                    await Task.Delay(100);

                                    keybd_event(0x4B, 0, 0, 0);      
                                    await Task.Delay(50);
                                    keybd_event(0x4B, 0, 0x0002, 0); 

                                    SetCursorPos(9999, 9999); 
                                }
                            }

                            return Results.Ok();
                        }
                        return Results.NotFound();
                    });
                    
                    await _webHost.RunAsync();
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => MessageBox.Show($"Web Server failed:\n{ex.Message}", "Server Error", MessageBoxButton.OK, MessageBoxImage.Error));
                }
            });
        }
    }

    public static class AppLogger
    {
        public static bool IsEnabled { get; set; } = false;

        private static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "feral_debug.log");
        private static readonly object _lock = new object();

        public static void Log(string message)
        {
            if (!IsEnabled) return;

            try
            {
                lock (_lock)
                {
                    File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}