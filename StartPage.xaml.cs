using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Xabe.FFmpeg.Downloader; // <-- Added for the downloader

namespace FeralCode
{
    public partial class StartPage : Page
    {
        // This static variable survives even when the page is destroyed and recreated!
        private static string _lastFocusedButtonName = "";

        public StartPage()
        {
            InitializeComponent();
            this.Loaded += Page_Loaded;
        }

        // FIX: Made this async so we can safely await the FFmpeg download
        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Verify/Download FFmpeg quietly in the background
            await EnsureFfmpegIsInstalledAsync();

            // 2. Restore button focus
            if (!string.IsNullOrEmpty(_lastFocusedButtonName))
            {
                if (this.FindName(_lastFocusedButtonName) as Button is Button targetButton)
                {
                    targetButton.Focus();
                    return; // Stop here so we don't accidentally focus the default button
                }
            }

            // Fallback: If memory is empty (first time launching app), focus Live TV
            BtnLiveTV.Focus(); 
        }

        // --- UPDATED: FFmpeg Auto-Download Logic ---
        private async Task EnsureFfmpegIsInstalledAsync()
        {
            // Windows Standard: Save dynamic binaries to AppData\Local so we don't need Admin rights
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ffmpegFolder = Path.Combine(appData, "FeralHTPC", "ffmpeg");
            
            if (!Directory.Exists(ffmpegFolder))
            {
                Directory.CreateDirectory(ffmpegFolder);
            }

            string ffmpegPath = Path.Combine(ffmpegFolder, "ffmpeg.exe");
            
            if (!File.Exists(ffmpegPath))
            {
                try
                {
                    // Pass the ffmpegFolder as the second argument to tell Xabe where to save it
                    await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, ffmpegFolder);
                    System.Diagnostics.Debug.WriteLine("FFmpeg downloaded successfully to AppData.");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to download FFmpeg: {ex.Message}");
                }
            }
        }

        private void LiveTv_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) _lastFocusedButtonName = btn.Name;
            NavigationService.Navigate(new GuidePage());
        }

        private void Movies_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) _lastFocusedButtonName = btn.Name;
            NavigationService.Navigate(new MoviesPage());
        }

        private void Shows_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) _lastFocusedButtonName = btn.Name;
            NavigationService.Navigate(new ShowsPage());
        }
        
        private void Apps_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) _lastFocusedButtonName = btn.Name;
            NavigationService.Navigate(new ExternalStreamsPage());
        }
        
        private void Multiview_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) _lastFocusedButtonName = btn.Name;
            NavigationService.Navigate(new MultiviewSetupPage());
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn) _lastFocusedButtonName = btn.Name;
            NavigationService.Navigate(new SettingsPage());
        }
    }
}