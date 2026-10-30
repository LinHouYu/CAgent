using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CAgent.Core.Agent;

namespace CAgent.Desktop.ViewModels;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            execute();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class AccountItemViewModel(string id, string displayName, string badgeText, int debugPort)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public string BadgeText { get; } = badgeText;
    public int DebugPort { get; } = debugPort;
    public string FullLabel => $"{DisplayName} ({BadgeText} · :{DebugPort})";
}

public sealed class TimelineItemViewModel : ViewModelBase
{
    private string _content;
    private ToolApprovalStatus _approvalStatus;

    public string Id { get; }
    public TimelineEntryKind Kind { get; }
    public string Title { get; }
    public string TimestampText { get; }

    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }

    public TimelineItemViewModel(
        TimelineEntry entry,
        Action onApprove,
        Action onReject)
    {
        Id = entry.Id;
        Kind = entry.Kind;
        Title = entry.Title;
        TimestampText = entry.Timestamp.ToString("HH:mm:ss");
        _content = entry.Content;
        _approvalStatus = entry.ApprovalStatus;

        ApproveCommand = new RelayCommand(onApprove, () => IsPendingApproval);
        RejectCommand = new RelayCommand(onReject, () => IsPendingApproval);
    }

    public string Content
    {
        get => _content;
        set => SetField(ref _content, value);
    }

    public ToolApprovalStatus ApprovalStatus
    {
        get => _approvalStatus;
        set
        {
            if (SetField(ref _approvalStatus, value))
            {
                OnPropertyChanged(nameof(IsPendingApproval));
                OnPropertyChanged(nameof(StatusBadgeText));
                ((RelayCommand)ApproveCommand).NotifyCanExecuteChanged();
                ((RelayCommand)RejectCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsToolCall => Kind == TimelineEntryKind.ToolCall;
    public bool IsUserMessage => Kind == TimelineEntryKind.UserMessage;
    public bool IsPendingApproval => Kind == TimelineEntryKind.ToolCall && ApprovalStatus == ToolApprovalStatus.PendingApproval;

    public string KindLabel => Kind switch
    {
        TimelineEntryKind.UserMessage => "USER",
        TimelineEntryKind.AssistantMessage => "COPILOT",
        TimelineEntryKind.ToolCall => "TOOL",
        TimelineEntryKind.Error => "ERROR",
        _ => "SYSTEM"
    };

    public string StatusBadgeText => ApprovalStatus switch
    {
        ToolApprovalStatus.PendingApproval => "⏳ 等待人工确认",
        ToolApprovalStatus.ApprovedExecuting => "⚙️ 正在执行...",
        ToolApprovalStatus.ExecutedSuccess => "✓ 执行成功",
        ToolApprovalStatus.ExecutedFailed => "✕ 执行失败",
        ToolApprovalStatus.Rejected => "⊘ 已拒绝",
        _ => string.Empty
    };

    public void SyncFrom(TimelineEntry updated)
    {
        Content = updated.Content;
        ApprovalStatus = updated.ApprovalStatus;
    }
}
