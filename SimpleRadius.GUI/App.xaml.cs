using System.Windows;
using System.Windows.Media.Imaging;

namespace SimpleRadius.GUI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Set the app icon — covers taskbar, Alt+Tab switcher, and title bar
        var icon = new BitmapImage(
            new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute));
        foreach (Window w in Windows) w.Icon = icon;

        // Global exception handler
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show($"Unhandled error:\n{ex.Exception.Message}",
                "Simple Radius — Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
