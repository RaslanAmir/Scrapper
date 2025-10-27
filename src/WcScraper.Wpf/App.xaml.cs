using System.Windows;
using Microsoft.Extensions.Logging;
using WcScraper.Wpf.Services;
using WcScraper.Wpf.ViewModels;

namespace WcScraper.Wpf
{
    public partial class App : Application
    {
        private ILoggerFactory? _loggerFactory;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddDebug();
                builder.AddConsole();
            });

            var mainWindow = new MainWindow
            {
                DataContext = new MainViewModel(new DialogService(), _loggerFactory)
            };

            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _loggerFactory?.Dispose();
            base.OnExit(e);
        }
    }
}
