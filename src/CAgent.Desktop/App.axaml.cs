using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CAgent.Bridge.Cdp;
using CAgent.Core.Adapters;
using CAgent.Core.Agent;
using CAgent.Core.Configuration;
using CAgent.Desktop.ViewModels;
using CAgent.Desktop.Views;
using CAgent.Tools;

namespace CAgent.Desktop;

public partial class App : Application
{
    private ConfigService? _configService;
    private CdpBrowserBridge? _bridge;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _configService = new ConfigService();
            ApplyTheme(_configService.Current.IsDarkMode);

            var adaptersPath = Path.Combine(_configService.DataDirectory, "adapters.json");
            var adapterManager = new AdapterManager(adaptersPath);
            _bridge = new CdpBrowserBridge(_configService, adapterManager);
            var tools = BuiltInTools.CreateDefaultRegistry();
            var orchestrator = new AgentOrchestrator(_configService, _bridge, tools);

            var vm = new MainWindowViewModel(
                _configService,
                _bridge,
                orchestrator,
                applyThemeCallback: ApplyTheme);

            desktop.MainWindow = new MainWindow(_configService)
            {
                DataContext = vm
            };

            desktop.Exit += (_, _) =>
            {
                _configService?.Dispose();
                if (_bridge is not null)
                {
                    _bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyTheme(bool isDark)
    {
        RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}
