using System;
using System.Threading.Tasks;
using System.Windows;

namespace FeralCode
{
    public partial class App : Application
    {
        // We added "async" here so we can use the Task.Delay!
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Instantly show the custom Splash Screen
            var splash = new SplashWindow();
            splash.Show();

            // 2. Do the heavy lifting: Load settings and apply the theme!
            var settings = SettingsManager.Load();
            string themeName = settings.IsLightTheme ? "LightTheme.xaml" : "DarkTheme.xaml";

            try 
            {
                var themeDict = new ResourceDictionary { Source = new Uri($"Themes/{themeName}", UriKind.Relative) };
                this.Resources.MergedDictionaries.Clear();
                this.Resources.MergedDictionaries.Add(themeDict);
            }
            catch 
            {
                // Failsafe: If the theme files are missing, WPF will just use its default grays
            }

            // 3. Keep the splash screen up just a little longer so it looks smooth and deliberate
            await Task.Delay(1500); 

            // 4. Boot up the Main Window
            var mainWindow = new MainWindow();
            mainWindow.Show();

            // 5. Close the Splash Screen seamlessly
            splash.Close();
        }
    }
}