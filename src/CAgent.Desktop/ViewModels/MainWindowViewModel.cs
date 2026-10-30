using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using CAgent.Core.Abstractions;
using CAgent.Core.Agent;
using CAgent.Core.Configuration;

namespace CAgent.Desktop.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ConfigService _configService;
    private readonly IBrowserBridge _bridge;
    private readonly AgentOrchestrator _orchestrator;
    private readonly Action<bool>? _applyThemeCallback;

    private AccountItemViewModel? _selectedAccount;
    private bool _suppressAccountSelectionCallback;

    private bool _isAddAccountPanelOpen;
    private string _newAccountName = string.Empty;
    private bool _newAccountIsWork;

    private string _connectionStatusText = "未连接浏览器";
    private BridgeConnectionState _connectionState = BridgeConnectionState.Disconnected;
    private string _agentStatusText = "就绪 — 请先选择工作目录并连接 Copilot";
    private string _promptInputText = string.Empty;
    private int _totalToolCallsCount;

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];
    public ObservableCollection<TimelineItemViewModel> TimelineItems { get; } = [];

    // Top Bar Commands
    public ICommand SwitchToPersonalCommand { get; }
    public ICommand SwitchToWorkCommand { get; }
    public ICommand ToggleAddAccountPanelCommand { get; }
    public ICommand ConfirmAddAccountCommand { get; }
    public ICommand CancelAddAccountCommand { get; }
    public ICommand RemoveCurrentAccountCommand { get; }
    public ICommand ConnectBrowserCommand { get; }
    public ICommand CalibrateInputCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ToggleSettingsCommand { get; }

    // Workspace & Chat Commands
    public ICommand BootstrapWorkspaceCommand { get; }
    public ICommand SendPromptCommand { get; }
    public ICommand EmergencyStopCommand { get; }
    public ICommand ClearTimelineCommand { get; }

    public event Action? TimelineChangedScrollRequested;

    public MainWindowViewModel(
        ConfigService configService,
        IBrowserBridge bridge,
        AgentOrchestrator orchestrator,
        Action<bool>? applyThemeCallback = null)
    {
        _configService = configService;
        _bridge = bridge;
        _orchestrator = orchestrator;
        _applyThemeCallback = applyThemeCallback;

        SwitchToPersonalCommand = new RelayCommand(() => SwitchEdition(CopilotEdition.Personal));
        SwitchToWorkCommand = new RelayCommand(() => SwitchEdition(CopilotEdition.Work));
        ToggleAddAccountPanelCommand = new RelayCommand(() =>
        {
            NewAccountIsWork = IsWorkEditionActive;
            NewAccountName = string.Empty;
            IsAddAccountPanelOpen = !IsAddAccountPanelOpen;
        });
        ConfirmAddAccountCommand = new RelayCommand(AddNewAccount);
        CancelAddAccountCommand = new RelayCommand(() => IsAddAccountPanelOpen = false);
        RemoveCurrentAccountCommand = new RelayCommand(RemoveCurrentAccount, () => CanRemoveCurrentAccount);

        ConnectBrowserCommand = new RelayCommand(() => _ = ConnectActiveAccountAsync());
        CalibrateInputCommand = new RelayCommand(() => _ = CalibrateActiveAccountAsync());
        ToggleThemeCommand = new RelayCommand(() => IsDarkMode = !IsDarkMode);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsExpanded = !IsSettingsExpanded);

        BootstrapWorkspaceCommand = new RelayCommand(() => _ = BootstrapWorkspaceAsync());
        SendPromptCommand = new RelayCommand(() => _ = SendPromptAsync());
        EmergencyStopCommand = new RelayCommand(() => _orchestrator.EmergencyStop(ActiveAccount.Id));
        ClearTimelineCommand = new RelayCommand(() =>
        {
            _orchestrator.ClearTimeline(ActiveAccount.Id);
            TimelineItems.Clear();
        });

        _bridge.StatusChanged += OnBridgeStatusChanged;
        _orchestrator.EntryAdded += OnOrchestratorEntryAdded;
        _orchestrator.EntryUpdated += OnOrchestratorEntryUpdated;
        _orchestrator.StateChanged += OnOrchestratorStateChanged;

        RefreshAccountsList();
        ReloadTimelineForActiveAccount();
    }

    public AccountProfile ActiveAccount => _configService.Current.GetActiveAccount();

    public bool IsPersonalEditionActive => ActiveAccount.Edition == CopilotEdition.Personal;
    public bool IsWorkEditionActive => ActiveAccount.Edition == CopilotEdition.Work;

    public string ActiveEditionLabel => IsPersonalEditionActive ? "个人版 Copilot" : "工作版 M365 Copilot";

    public bool CanRemoveCurrentAccount =>
        _configService.Current.Accounts.Count(a => a.Edition == ActiveAccount.Edition) > 1;

    public AccountItemViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetField(ref _selectedAccount, value) && value is not null && !_suppressAccountSelectionCallback)
            {
                _configService.Update(cfg => cfg.SwitchAccount(value.Id));
                NotifyAccountChanged();
            }
        }
    }

    public bool IsAddAccountPanelOpen
    {
        get => _isAddAccountPanelOpen;
        set => SetField(ref _isAddAccountPanelOpen, value);
    }

    public string NewAccountName
    {
        get => _newAccountName;
        set => SetField(ref _newAccountName, value);
    }

    public bool NewAccountIsWork
    {
        get => _newAccountIsWork;
        set => SetField(ref _newAccountIsWork, value);
    }

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set => SetField(ref _connectionStatusText, value);
    }

    public BridgeConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetField(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(IsConnectionReady));
            }
        }
    }

    public bool IsConnectionReady => ConnectionState == BridgeConnectionState.Ready;

    public string AgentStatusText
    {
        get => _agentStatusText;
        private set => SetField(ref _agentStatusText, value);
    }

    public int TotalToolCallsCount
    {
        get => _totalToolCallsCount;
        private set => SetField(ref _totalToolCallsCount, value);
    }

    public string PromptInputText
    {
        get => _promptInputText;
        set => SetField(ref _promptInputText, value);
    }

    // Persisted Settings Properties (auto-saved to config.json)
    public string WorkspacePath
    {
        get => _configService.Current.LastWorkspacePath;
        set
        {
            if (_configService.Current.LastWorkspacePath != value)
            {
                _configService.Update(cfg => cfg.LastWorkspacePath = value ?? string.Empty, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public bool AutoApproveTools
    {
        get => _configService.Current.AutoApproveTools;
        set
        {
            if (_configService.Current.AutoApproveTools != value)
            {
                _configService.Update(cfg => cfg.AutoApproveTools = value, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public decimal MaxAutoSteps
    {
        get => _configService.Current.MaxAutoSteps;
        set
        {
            var intVal = Math.Clamp((int)value, 1, 100);
            if (_configService.Current.MaxAutoSteps != intVal)
            {
                _configService.Update(cfg => cfg.MaxAutoSteps = intVal, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public decimal DelayMinMs
    {
        get => _configService.Current.DelayMinMs;
        set
        {
            var intVal = Math.Clamp((int)value, 0, 60_000);
            if (_configService.Current.DelayMinMs != intVal)
            {
                _configService.Update(cfg => cfg.DelayMinMs = intVal, saveImmediately: false);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DelayMaxMs));
            }
        }
    }

    public decimal DelayMaxMs
    {
        get => _configService.Current.DelayMaxMs;
        set
        {
            var intVal = Math.Clamp((int)value, 0, 120_000);
            if (_configService.Current.DelayMaxMs != intVal)
            {
                _configService.Update(cfg => cfg.DelayMaxMs = intVal, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public decimal MaxPromptChars
    {
        get => _configService.Current.MaxPromptChars;
        set
        {
            var intVal = Math.Clamp((int)value, 800, 32_000);
            if (_configService.Current.MaxPromptChars != intVal)
            {
                _configService.Update(cfg => cfg.MaxPromptChars = intVal, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public decimal CommandTimeoutSec
    {
        get => _configService.Current.CommandTimeoutSec;
        set
        {
            var intVal = Math.Clamp((int)value, 5, 600);
            if (_configService.Current.CommandTimeoutSec != intVal)
            {
                _configService.Update(cfg => cfg.CommandTimeoutSec = intVal, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public bool EnterToSend
    {
        get => _configService.Current.EnterToSend;
        set
        {
            if (_configService.Current.EnterToSend != value)
            {
                _configService.Update(cfg => cfg.EnterToSend = value, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public bool IsDarkMode
    {
        get => _configService.Current.IsDarkMode;
        set
        {
            if (_configService.Current.IsDarkMode != value)
            {
                _configService.Update(cfg => cfg.IsDarkMode = value, saveImmediately: false);
                _applyThemeCallback?.Invoke(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThemeButtonText));
            }
        }
    }

    public string ThemeButtonText => IsDarkMode ? "🌙 深色" : "☀️ 浅色";

    public bool IsSettingsExpanded
    {
        get => _configService.Current.IsSettingsExpanded;
        set
        {
            if (_configService.Current.IsSettingsExpanded != value)
            {
                _configService.Update(cfg => cfg.IsSettingsExpanded = value, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public string CustomBrowserExecutablePath
    {
        get => _configService.Current.CustomBrowserExecutablePath;
        set
        {
            if (_configService.Current.CustomBrowserExecutablePath != value)
            {
                _configService.Update(cfg => cfg.CustomBrowserExecutablePath = value ?? string.Empty, saveImmediately: false);
                OnPropertyChanged();
            }
        }
    }

    public void SwitchEdition(CopilotEdition edition)
    {
        _configService.Update(cfg => cfg.SwitchEdition(edition));
        RefreshAccountsList();
        NotifyAccountChanged();
    }

    public void AddNewAccount()
    {
        var edition = NewAccountIsWork ? CopilotEdition.Work : CopilotEdition.Personal;
        _configService.Update(cfg => cfg.AddAccount(NewAccountName, edition));
        NewAccountName = string.Empty;
        IsAddAccountPanelOpen = false;
        RefreshAccountsList();
        NotifyAccountChanged();
    }

    public void RemoveCurrentAccount()
    {
        var currentId = ActiveAccount.Id;
        var removed = false;
        _configService.Update(cfg => removed = cfg.RemoveAccount(currentId));
        if (removed)
        {
            _ = _bridge.DisconnectAccountAsync(currentId);
            RefreshAccountsList();
            NotifyAccountChanged();
        }
    }

    public async Task ConnectActiveAccountAsync()
    {
        var account = ActiveAccount;
        var snap = await _bridge.EnsureConnectedAsync(account);
        UpdateStatusFromBridge(account.Id, snap);
    }

    public async Task CalibrateActiveAccountAsync()
    {
        var account = ActiveAccount;
        try
        {
            AgentStatusText = "请在浏览器页面中点击 Copilot 聊天输入框（30秒内有效）...";
            var sel = await _bridge.CalibrateInputSelectorAsync(account, TimeSpan.FromSeconds(30));
            AgentStatusText = $"输入框自愈校准完成：{sel.CssSelector}";
        }
        catch (Exception ex)
        {
            AgentStatusText = $"校准未完成：{ex.Message}";
        }
    }

    public async Task BootstrapWorkspaceAsync()
    {
        var account = ActiveAccount;
        await _orchestrator.BootstrapWorkspaceAsync(account);
    }

    public async Task SendPromptAsync()
    {
        var text = PromptInputText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        PromptInputText = string.Empty;
        await _orchestrator.SendUserTaskAsync(ActiveAccount, text);
    }

    private void RefreshAccountsList()
    {
        _suppressAccountSelectionCallback = true;
        try
        {
            Accounts.Clear();
            foreach (var acc in _configService.Current.Accounts)
            {
                var badge = acc.Edition == CopilotEdition.Personal ? "个人版" : "工作版";
                Accounts.Add(new AccountItemViewModel(acc.Id, acc.DisplayName, badge, acc.DebugPort));
            }

            _selectedAccount = Accounts.FirstOrDefault(a => a.Id == _configService.Current.ActiveAccountId);
            OnPropertyChanged(nameof(SelectedAccount));
        }
        finally
        {
            _suppressAccountSelectionCallback = false;
        }
    }

    private void NotifyAccountChanged()
    {
        OnPropertyChanged(nameof(ActiveAccount));
        OnPropertyChanged(nameof(IsPersonalEditionActive));
        OnPropertyChanged(nameof(IsWorkEditionActive));
        OnPropertyChanged(nameof(ActiveEditionLabel));
        OnPropertyChanged(nameof(CanRemoveCurrentAccount));
        ((RelayCommand)RemoveCurrentAccountCommand).NotifyCanExecuteChanged();

        ReloadTimelineForActiveAccount();
        _ = RefreshBridgeStatusForActiveAccountAsync();
    }

    private async Task RefreshBridgeStatusForActiveAccountAsync()
    {
        var acc = ActiveAccount;
        var snap = await _bridge.CheckStatusAsync(acc);
        UpdateStatusFromBridge(acc.Id, snap);
    }

    private void ReloadTimelineForActiveAccount()
    {
        TimelineItems.Clear();
        var accId = ActiveAccount.Id;
        foreach (var entry in _orchestrator.GetTimeline(accId))
        {
            TimelineItems.Add(CreateItemVm(entry));
        }
        TimelineChangedScrollRequested?.Invoke();
    }

    private TimelineItemViewModel CreateItemVm(TimelineEntry entry) =>
        new(
            entry,
            onApprove: () => _orchestrator.ApprovePendingTool(entry.AccountId),
            onReject: () => _orchestrator.RejectPendingTool(entry.AccountId));

    private void OnBridgeStatusChanged(string accountId, BridgeStatusSnapshot snap) =>
        RunOnUi(() => UpdateStatusFromBridge(accountId, snap));

    private void UpdateStatusFromBridge(string accountId, BridgeStatusSnapshot snap)
    {
        if (accountId != ActiveAccount.Id)
        {
            return;
        }
        ConnectionState = snap.State;
        ConnectionStatusText = snap.Message;
    }

    private void OnOrchestratorEntryAdded(TimelineEntry entry) =>
        RunOnUi(() =>
        {
            if (entry.AccountId != ActiveAccount.Id)
            {
                return;
            }
            TimelineItems.Add(CreateItemVm(entry));
            TimelineChangedScrollRequested?.Invoke();
        });

    private void OnOrchestratorEntryUpdated(TimelineEntry entry) =>
        RunOnUi(() =>
        {
            if (entry.AccountId != ActiveAccount.Id)
            {
                return;
            }
            var target = TimelineItems.FirstOrDefault(i => i.Id == entry.Id);
            target?.SyncFrom(entry);
            TimelineChangedScrollRequested?.Invoke();
        });

    private void OnOrchestratorStateChanged(AgentStateSnapshot snap) =>
        RunOnUi(() =>
        {
            if (snap.AccountId != ActiveAccount.Id)
            {
                return;
            }
            AgentStatusText = snap.StatusText;
            TotalToolCallsCount = snap.TotalToolCalls;
        });

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
