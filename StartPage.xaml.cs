using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace FeralCode
{
    public partial class StartPage : Page
    {
        public StartPage()
        {
            InitializeComponent();
            
            // Instantly highlight the first button when the page loads so the D-Pad works!
            this.Loaded += (s, e) => {
                var request = new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First);
                this.MoveFocus(request);
            };
        }

        private void LiveTv_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new GuidePage());
        }

        private void Movies_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new MoviesPage());
        }

        private void Shows_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new ShowsPage());
        }
		
		private void Apps_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new ExternalStreamsPage());
        }
		
		private void Multiview_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new MultiviewSetupPage());
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new SettingsPage());
        }
    }
}