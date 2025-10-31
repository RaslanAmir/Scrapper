using System.Windows;
using System.Windows.Threading;
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

            var dialogs = new DialogService();
            var artifactIndexingService = new ArtifactIndexingService();
            var chatAssistantService = new ChatAssistantService(artifactIndexingService);
            var chatAssistant = MainViewModel.CreateDefaultChatAssistant(
                dialogs,
                artifactIndexingService,
                chatAssistantService);

            MainViewModel? mainViewModel = null;
            var dispatcher = Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            var assistantToggleBindings = MainViewModel.CreateChatAssistantToggleBindings(() => mainViewModel!);
            var exportPlanning = new ExportPlanningViewModel(
                dispatcher,
                assistantToggleBindings,
                () => mainViewModel!.HttpRetryAttempts,
                value => mainViewModel!.HttpRetryAttempts = value,
                () => mainViewModel!.HttpRetryBaseDelaySeconds,
                value => mainViewModel!.HttpRetryBaseDelaySeconds = value,
                () => mainViewModel!.HttpRetryMaxDelaySeconds,
                value => mainViewModel!.HttpRetryMaxDelaySeconds = value,
                value => mainViewModel!.ManualRunGoals = value,
                () => mainViewModel!.PrepareRunCancellationToken(),
                cancellationToken => mainViewModel!.OnRunAsync(cancellationToken),
                message => mainViewModel!.Append(message));
            var provisioning = new ProvisioningViewModel();

            mainViewModel = new MainViewModel(
                dialogs,
                _loggerFactory,
                artifactIndexingService,
                chatAssistantService,
                chatAssistant,
                exportPlanning,
                provisioning);

            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
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
