using System;
using System.Windows;

namespace FeralCode
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Read the user_settings.json file the second the app boots
            var settings = SettingsManager.Load();

            // 2. Figure out which theme they prefer
            string themeName = settings.IsLightTheme ? "LightTheme.xaml" : "DarkTheme.xaml";

            // 3. Inject that theme into the master application resources!
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
        }
    }
}