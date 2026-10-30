using System.Text.Json.Serialization;

namespace CAgent.Core.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CopilotEdition
{
    Personal,
    Work
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentInteractionMode
{
    Plan,
    Execute
}

public sealed class CalibratedSelector
{
    public string CssSelector { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string AriaLabel { get; set; } = string.Empty;
    public string Placeholder { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string FrameUrlHint { get; set; } = string.Empty;
    public DateTimeOffset CalibratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DisplayName { get; set; } = "默认个人账号";
    public CopilotEdition Edition { get; set; } = CopilotEdition.Personal;
    public int DebugPort { get; set; } = 9222;
    public string ProfileDirName { get; set; } = "personal_default";
    public string CustomUrl { get; set; } = string.Empty;
    public CalibratedSelector? CustomInputSelector { get; set; }
}

public sealed class AppConfig
{
    public int ConfigSchemaVersion { get; set; } = 0;

    public string ActiveAccountId { get; set; } = "personal_default";
    public string LastPersonalAccountId { get; set; } = "personal_default";
    public string LastWorkAccountId { get; set; } = "work_default";
    public List<AccountProfile> Accounts { get; set; } = [];

    // Workspace, Interaction Mode & Safety Guardrails
    public string LastWorkspacePath { get; set; } = string.Empty;
    public AgentInteractionMode InteractionMode { get; set; } = AgentInteractionMode.Execute;
    public bool AutoApproveTools { get; set; } = true;
    public int MaxAutoSteps { get; set; } = 25;
    public int DelayMinMs { get; set; } = 1200;
    public int DelayMaxMs { get; set; } = 3200;
    public int MaxPromptChars { get; set; } = 4200;
    public int CommandTimeoutSec { get; set; } = 60;
    public bool EnterToSend { get; set; } = true;

    // UI Appearance & Window State Persistence
    public bool IsDarkMode { get; set; } = true;
    public bool IsSettingsExpanded { get; set; } = false;
    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 740;
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public string CustomBrowserExecutablePath { get; set; } = string.Empty;

    public void EnsureDefaults()
    {
        // Smoothly migrate v0.1 configs (where ConfigSchemaVersion was absent/0 and AutoApproveTools defaulted to false)
        if (ConfigSchemaVersion < 2)
        {
            AutoApproveTools = true;
            InteractionMode = AgentInteractionMode.Execute;
            if (MaxAutoSteps <= 10)
            {
                MaxAutoSteps = 25;
            }
            ConfigSchemaVersion = 2;
        }

        Accounts ??= [];

        if (!Accounts.Any(a => a.Edition == CopilotEdition.Personal))
        {
            Accounts.Add(new AccountProfile
            {
                Id = "personal_default",
                DisplayName = "个人版账号 1",
                Edition = CopilotEdition.Personal,
                DebugPort = AllocatePort(9222),
                ProfileDirName = "personal_default"
            });
        }

        if (!Accounts.Any(a => a.Edition == CopilotEdition.Work))
        {
            Accounts.Add(new AccountProfile
            {
                Id = "work_default",
                DisplayName = "工作版账号 1 (M365)",
                Edition = CopilotEdition.Work,
                DebugPort = AllocatePort(9223),
                ProfileDirName = "work_default"
            });
        }

        // Ensure unique valid ports
        var usedPorts = new HashSet<int>();
        foreach (var account in Accounts)
        {
            if (account.DebugPort < 1024 || account.DebugPort > 65535 || !usedPorts.Add(account.DebugPort))
            {
                account.DebugPort = AllocatePort(9222, usedPorts);
                usedPorts.Add(account.DebugPort);
            }

            if (string.IsNullOrWhiteSpace(account.ProfileDirName))
            {
                account.ProfileDirName = $"{account.Edition.ToString().ToLowerInvariant()}_{account.Id}";
            }
        }

        var personalFallback = Accounts.First(a => a.Edition == CopilotEdition.Personal).Id;
        var workFallback = Accounts.First(a => a.Edition == CopilotEdition.Work).Id;

        if (Accounts.All(a => a.Id != LastPersonalAccountId))
        {
            LastPersonalAccountId = personalFallback;
        }

        if (Accounts.All(a => a.Id != LastWorkAccountId))
        {
            LastWorkAccountId = workFallback;
        }

        if (Accounts.All(a => a.Id != ActiveAccountId))
        {
            ActiveAccountId = personalFallback;
        }

        MaxAutoSteps = Math.Clamp(MaxAutoSteps, 1, 200);
        DelayMinMs = Math.Clamp(DelayMinMs, 0, 60_000);
        DelayMaxMs = Math.Clamp(Math.Max(DelayMinMs, DelayMaxMs), 0, 120_000);
        MaxPromptChars = Math.Clamp(MaxPromptChars, 800, 32_000);
        CommandTimeoutSec = Math.Clamp(CommandTimeoutSec, 5, 600);
        WindowWidth = Math.Clamp(WindowWidth, 640, 4096);
        WindowHeight = Math.Clamp(WindowHeight, 480, 3000);
    }

    public AccountProfile GetActiveAccount()
    {
        EnsureDefaults();
        return Accounts.First(a => a.Id == ActiveAccountId);
    }

    public AccountProfile SwitchAccount(string accountId)
    {
        EnsureDefaults();
        var target = Accounts.FirstOrDefault(a => a.Id == accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' does not exist.");

        ActiveAccountId = target.Id;
        if (target.Edition == CopilotEdition.Personal)
        {
            LastPersonalAccountId = target.Id;
        }
        else
        {
            LastWorkAccountId = target.Id;
        }

        return target;
    }

    public AccountProfile SwitchEdition(CopilotEdition edition)
    {
        EnsureDefaults();
        var preferredId = edition == CopilotEdition.Personal ? LastPersonalAccountId : LastWorkAccountId;
        var target = Accounts.FirstOrDefault(a => a.Id == preferredId && a.Edition == edition)
            ?? Accounts.First(a => a.Edition == edition);

        return SwitchAccount(target.Id);
    }

    public AccountProfile AddAccount(string displayName, CopilotEdition edition)
    {
        EnsureDefaults();
        var id = $"{edition.ToString().ToLowerInvariant()}_{Guid.NewGuid().ToString("N")[..6]}";
        var countForEdition = Accounts.Count(a => a.Edition == edition) + 1;
        var cleanName = string.IsNullOrWhiteSpace(displayName)
            ? (edition == CopilotEdition.Personal ? $"个人版账号 {countForEdition}" : $"工作版账号 {countForEdition} (M365)")
            : displayName.Trim();

        var profile = new AccountProfile
        {
            Id = id,
            DisplayName = cleanName,
            Edition = edition,
            DebugPort = AllocatePort(9222),
            ProfileDirName = id
        };

        Accounts.Add(profile);
        return SwitchAccount(profile.Id);
    }

    public bool RemoveAccount(string accountId)
    {
        EnsureDefaults();
        var target = Accounts.FirstOrDefault(a => a.Id == accountId);
        if (target is null)
        {
            return false;
        }

        // Always keep at least one account per edition so top-bar switching remains instant
        if (Accounts.Count(a => a.Edition == target.Edition) <= 1)
        {
            return false;
        }

        Accounts.Remove(target);
        EnsureDefaults();
        return true;
    }

    private int AllocatePort(int startPort, HashSet<int>? additionalReserved = null)
    {
        var port = startPort;
        while (Accounts.Any(a => a.DebugPort == port) || (additionalReserved?.Contains(port) ?? false))
        {
            port++;
        }
        return port;
    }
}
