using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CAgent.Core.Configuration;
using CAgent.Desktop.ViewModels;

namespace CAgent.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly ConfigService? _configService;
    private bool _isRestoringGeometry;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(ConfigService configService) : this()
    {
        _configService = configService;
        RestoreWindowGeometry(configService.Current);

        PositionChanged += OnWindowPositionChanged;
        Closing += OnWindowClosing;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.TimelineChangedScrollRequested += ScrollTimelineToBottom;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_configService is null || _isRestoringGeometry || WindowState != WindowState.Normal)
        {
            return;
        }

        if (change.Property == WidthProperty || change.Property == HeightProperty)
        {
            var w = Width;
            var h = Height;
            if (!double.IsNaN(w) && !double.IsNaN(h) && w >= 640 && h >= 480)
            {
                _configService.Update(cfg =>
                {
                    cfg.WindowWidth = w;
                    cfg.WindowHeight = h;
                }, saveImmediately: false, debounceMs: 300);
            }
        }
    }

    private void RestoreWindowGeometry(AppConfig cfg)
    {
        var targetWidth = cfg.WindowWidth;
        var targetHeight = cfg.WindowHeight;
        var targetX = cfg.WindowX;
        var targetY = cfg.WindowY;

        _isRestoringGeometry = true;
        try
        {
            Width = targetWidth;
            Height = targetHeight;
            if (targetX.HasValue && targetY.HasValue)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint((int)targetX.Value, (int)targetY.Value);
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        finally
        {
            _isRestoringGeometry = false;
        }
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_configService is null || WindowState != WindowState.Normal)
        {
            return;
        }

        _configService.Update(cfg =>
        {
            cfg.WindowX = e.Point.X;
            cfg.WindowY = e.Point.Y;
        }, saveImmediately: false, debounceMs: 400);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        _configService?.Flush();
    }

    private async void OnBrowseWorkspaceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || StorageProvider is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择允许 Copilot 操控的本地工作目录",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var localPath = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath))
            {
                vm.WorkspacePath = localPath;
            }
        }
    }

    private void OnPromptInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && vm.EnterToSend && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (vm.SendPromptCommand.CanExecute(null))
            {
                vm.SendPromptCommand.Execute(null);
            }
        }
    }

    private void ScrollTimelineToBottom()
    {
        TimelineScrollViewer?.ScrollToEnd();
    }
}
