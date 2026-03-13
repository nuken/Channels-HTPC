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

namespace ChannelsNativeTest
{
    public partial class MainWindow : Window
    {
        public static LibVLC SharedLibVLC { get; private set; } = null!;
        
        // 1. MainWindow now permanently owns the Web Server and tracks the Active Player!
        private WebApplication? _webHost;
        public PlayerWindow? ActivePlayerWindow { get; set; }
		// --- NEW: WINDOWS NATIVE MOUSE CONTROLS ---
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        public struct POINT { public int X; public int Y; }
        public class MouseDelta { public int dx { get; set; } public int dy { get; set; } }
        // ------------------------------------------

        public MainWindow()
        {
            InitializeComponent();
            Core.Initialize(); 
            
            SharedLibVLC = new LibVLC(
                "--no-mouse-events",    
                "--no-keyboard-events", 
                "--avcodec-threads=0",  
                "--ts-trust-pcr"        
            );

            // 2. Start the server the absolute second the app launches!
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

        private void StartWebServer()
        {
            Task.Run(async () =>
            {
                try
                {
                    var options = new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory };
                    var builder = WebApplication.CreateBuilder(options);
                    builder.WebHost.UseUrls("http://*:12345");

                    builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(opt => 
                    {
                        opt.SerializerOptions.IncludeFields = true;
                    });

                    _webHost = builder.Build();
                    _webHost.UseStaticFiles();

                    _webHost.MapGet("/", () => 
                    {
                        string filePath = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
                        if (System.IO.File.Exists(filePath)) return Results.File(filePath, "text/html");
                        return Results.Content("<h1>Error: wwwroot/index.html not found!</h1>", "text/html");
                    });

                    // --- DELEGATE DATA TO THE ACTIVE PAGE ---

                    _webHost.MapGet("/api/remote/collections", () => 
                    {
                        var names = new List<string> { "All Channels" };
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            if (MainFrame.Content is GuidePage guide) names = guide.GetCollections();
                        });
                        return Results.Json(names);
                    });

                    _webHost.MapGet("/api/remote/guide", (string? collection, string? search) => 
                    {
                        object mobileData = new List<object>();
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            if (MainFrame.Content is GuidePage guide) mobileData = guide.GetMobileGuideData(collection, search);
                        });
                        return Results.Json(mobileData);
                    });

                    _webHost.MapPost("/api/remote/play/{channelNumber}", (string channelNumber) =>
                    {
                        Application.Current.Dispatcher.Invoke(() => 
                        {
                            if (MainFrame.Content is GuidePage guide) guide.RemotePlayChannel(channelNumber);
                        });
                        return Results.Ok(new { status = "playing", channel = channelNumber });
                    });

                    // --- GLOBAL COMMANDS ---

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
                    _webHost.MapPost("/api/remote/pip", () => { Application.Current.Dispatcher.Invoke(() => ActivePlayerWindow?.TogglePiP()); return Results.Ok(); });
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

                   // This D-Pad logic natively works on ANY page perfectly!
                    _webHost.MapPost("/api/remote/key/{direction}", (string direction) =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Window targetWindow = Application.Current.MainWindow;

                            // 1. If the Player is open, it gets absolute priority!
                            if (ActivePlayerWindow != null && ActivePlayerWindow.IsVisible)
                            {
                                targetWindow = ActivePlayerWindow;
                                
                                // First, see if the player wants to consume the key as a media shortcut (FF/RW/Pause)
                                if (ActivePlayerWindow.HandleRemoteKey(direction)) 
                                    return; // Handled as a shortcut! Stop here.
                            }

                            // 2. Standard UI Navigation (works on whichever window is currently active)
                            targetWindow.Activate();
                            
                            var focused = System.Windows.Input.Keyboard.FocusedElement as UIElement;
                            
                            if (focused == null)
                            {
                                var request = new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First);
                                ((FrameworkElement)targetWindow).MoveFocus(request);
                                focused = System.Windows.Input.Keyboard.FocusedElement as UIElement;
                            }

                            if (focused != null)
                            {
                                if (direction == "up") focused.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Up));
                                else if (direction == "down") focused.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Down));
                                else if (direction == "left") focused.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Left));
                                else if (direction == "right") focused.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Right));
                                else if (direction == "enter")
                                {
                                    if (focused is System.Windows.Controls.Button btn) btn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                }
                                
                                var newlyFocused = System.Windows.Input.Keyboard.FocusedElement as FrameworkElement;
                                newlyFocused?.BringIntoView();
                            }
                        });
                        return Results.Ok();
                    });
					
					// --- NEW: MOBILE MOVIE LIBRARY ---

                    _webHost.MapGet("/api/remote/movies", async () => 
                    {
                        var settings = SettingsManager.Load();
                        if (string.IsNullOrWhiteSpace(settings.LastServerAddress)) return Results.Json(new List<Movie>());

                        var api = new ChannelsApi();
                        var movies = await api.GetMoviesAsync(settings.LastServerAddress);
                        return Results.Json(movies);
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

                                // Launch the movie on the TV directly from the phone!
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

                    // --- NEW: REMOTE SCRUBBING ROUTES ---
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
					
					// --- NEW: SYSTEM MOUSE TRACKPAD ROUTES ---
                    _webHost.MapPost("/api/system/mouse", async (HttpContext context) =>
                    {
                        var delta = await context.Request.ReadFromJsonAsync<MouseDelta>();
                        if (delta != null && GetCursorPos(out POINT p))
                        {
                            // Move the physical Windows cursor!
                            SetCursorPos(p.X + delta.dx, p.Y + delta.dy);
                        }
                        return Results.Ok();
                    });

                    _webHost.MapPost("/api/system/click/{type}", (string type) =>
                    {
                        // Simulate physical hardware clicks
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
					
					// --- NEW: TV SHOWS LIBRARY ---
                    _webHost.MapGet("/api/remote/shows", async () => 
                    {
                        var settings = SettingsManager.Load();
                        if (string.IsNullOrWhiteSpace(settings.LastServerAddress)) return Results.Json(new List<TvShow>());
                        var api = new ChannelsApi();
                        return Results.Json(await api.GetShowsAsync(settings.LastServerAddress));
                    });

                    _webHost.MapGet("/api/remote/episodes/{showId}", async (string showId) => 
                    {
                        var settings = SettingsManager.Load();
                        if (string.IsNullOrWhiteSpace(settings.LastServerAddress)) return Results.Json(new List<Episode>());
                        var api = new ChannelsApi();
                        var allEpisodes = await api.GetEpisodesAsync(settings.LastServerAddress);
                        
                        // Filter to the clicked show, and sort so the newest episodes are at the top!
                        var showEpisodes = allEpisodes.Where(e => e.ShowId == showId)
                                                      .OrderByDescending(e => e.SeasonNumber)
                                                      .ThenByDescending(e => e.EpisodeNumber).ToList();
                        return Results.Json(showEpisodes);
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

                                // Launch using the exact same constructor we used for Movies!
                                ActivePlayerWindow = new PlayerWindow(streamUrl, displayTitle, ep.ImageUrl, ep.Commercials);
                                ActivePlayerWindow.Closed += (s, args) => ActivePlayerWindow = null; 
                                ActivePlayerWindow.Show();
                            });
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
}