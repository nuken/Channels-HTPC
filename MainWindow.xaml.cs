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

namespace FeralCode
{
    public partial class MainWindow : Window
    {
        public static LibVLC SharedLibVLC { get; private set; } = null!;
        
        private WebApplication? _webHost;
        public PlayerWindow? ActivePlayerWindow { get; set; }

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
            
            SharedLibVLC = new LibVLC(
                "--no-mouse-events",    
                "--no-keyboard-events", 
                "--avcodec-threads=0",  
                "--ts-trust-pcr",
                "--network-caching=3000",
                "--live-caching=3000"
            );

            StartWebServer();
            MainFrame.Navigate(new StartPage());
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
                using (var tcpListener = new TcpListener(IPAddress.Loopback, port))
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
                    int portToUse = settings.WebServerPort;

                    if (portToUse == 0)
                    {
                        portToUse = GetAvailablePort(12345, 12445);
                        settings.WebServerPort = portToUse;
                        SettingsManager.Save(settings);
                    }
                    else
                    {
                        int maxRetries = 30; 
                        int currentTry = 0;
                        while (!IsPortAvailable(portToUse) && currentTry < maxRetries)
                        {
                            await Task.Delay(1000);
                            currentTry++;
                        }
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

                    // --- EMBEDDED FILES LOGIC ---
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
                            if (ActivePlayerWindow != null) ActivePlayerWindow.Close();
                            if (MainFrame.Content is Page page && page.NavigationService.CanGoBack) page.NavigationService.GoBack();
                        }); 
                        return Results.Ok(); 
                    });
                    
                    _webHost.MapPost("/api/remote/stop", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.Close()); return Results.Ok(); });
                    _webHost.MapPost("/api/toggle_pip", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.TogglePiP()); return Results.Ok(); });
                    _webHost.MapPost("/api/remote/cc", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.ToggleClosedCaptions()); return Results.Ok(); });
                    _webHost.MapPost("/api/remote/volup", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.VolumeUp()); return Results.Ok(); });
                    _webHost.MapPost("/api/remote/voldown", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.VolumeDown()); return Results.Ok(); });
                    _webHost.MapPost("/api/remote/mute", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.ToggleMute()); return Results.Ok(); });
                    _webHost.MapPost("/api/remote/stats", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.ToggleStats()); return Results.Ok(); });

                    _webHost.MapPost("/api/remote/fullscreen", () =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (ActivePlayerWindow != null)
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

                    // --- CLEAN REMOTE D-PAD LOGIC ---
                    _webHost.MapPost("/api/remote/key/{direction}", (string direction) =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Window targetWindow = Application.Current.MainWindow;

                            if (ActivePlayerWindow != null && ActivePlayerWindow.IsVisible)
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
                            if (ActivePlayerWindow != null)
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

                    _webHost.MapGet("/api/remote/episodes/{showId}", async (string showId) => 
                    {
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

                    _webHost.MapPost("/api/remote/playapp/{id}", (string id) =>
                    {
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            var settings = SettingsManager.Load();
                            var stream = settings.ExternalStreams.FirstOrDefault(s => s.Id == id);
                            if (stream != null)
                            {
                                try
                                {
                                    if (stream.Service.ToLower() == "netflix")
                                    {
                                        string input = stream.StreamId.Trim();
                                        string finalUrl = input.StartsWith("http") ? input : $"https://www.netflix.com/watch/{input}";
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "msedge", Arguments = $"--app={finalUrl} --start-fullscreen", UseShellExecute = true });
                                    }
                                    else if (stream.Service.ToLower() == "disney+")
                                    {
                                        string input = stream.StreamId.Trim();
                                        string finalUrl = input.StartsWith("http") ? input : $"https://www.disneyplus.com/play/{input}";
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "msedge", Arguments = $"--app={finalUrl} --start-fullscreen", UseShellExecute = true });
                                    }
                                    else if (stream.Service.ToLower() == "prime video")
                                    {
                                        string input = stream.StreamId.Trim();
                                        string finalUrl = input.StartsWith("http") ? input : $"https://www.primevideo.com/watch/{input}";
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "msedge", Arguments = $"--app={finalUrl} --start-fullscreen", UseShellExecute = true });
                                    }
                                    else
                                    {
                                        string uri = stream.Service.ToLower() switch
                                        {
                                            "hulu" => $"hulu://w/{stream.StreamId.Trim()}",
                                            _ => stream.StreamId.Trim()
                                        };
                                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
                                    }
                                }
                                catch { }
                            }
                        });
                        return Results.Ok();
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
}