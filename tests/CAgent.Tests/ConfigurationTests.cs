using System.Text;
using CAgent.Core.Adapters;
using CAgent.Core.Configuration;

namespace CAgent.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigurationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cagent_cfg_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void FreshConfig_SeedsPersonalAndWorkAccountsWithDistinctPortsAndProfiles()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        using var service = new ConfigService(configPath);

        Assert.True(File.Exists(configPath));
        Assert.Equal(2, service.Current.Accounts.Count);

        var personal = service.Current.Accounts.Single(a => a.Edition == CopilotEdition.Personal);
        var work = service.Current.Accounts.Single(a => a.Edition == CopilotEdition.Work);

        Assert.NotEqual(personal.DebugPort, work.DebugPort);
        Assert.NotEqual(personal.ProfileDirName, work.ProfileDirName);
        Assert.Equal(personal.Id, service.Current.ActiveAccountId);
    }

    [Fact]
    public void UiStateAndMultiAccountChanges_PersistAcrossRestart()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        string secondWorkId;

        using (var firstRun = new ConfigService(configPath))
        {
            firstRun.Update(cfg =>
            {
                var addedWork = cfg.AddAccount("公司研发账号", CopilotEdition.Work);
                secondWorkId = addedWork.Id;
                addedWork.CustomInputSelector = new CalibratedSelector
                {
                    CssSelector = "div[role='textbox'][aria-label='Ask Copilot']",
                    TagName = "div",
                    Role = "textbox"
                };

                cfg.IsDarkMode = false;
                cfg.IsSettingsExpanded = true;
                cfg.AutoApproveTools = true;
                cfg.MaxAutoSteps = 15;
                cfg.DelayMinMs = 2000;
                cfg.DelayMaxMs = 5500;
                cfg.WindowWidth = 1280;
                cfg.WindowHeight = 840;
                cfg.WindowX = 120;
                cfg.WindowY = 85;
                cfg.LastWorkspacePath = @"C:\Projects\Demo";
            });
            secondWorkId = firstRun.Current.ActiveAccountId;
        }

        using var secondRun = new ConfigService(configPath);
        var restored = secondRun.Current;

        Assert.Equal(3, restored.Accounts.Count);
        Assert.Equal(secondWorkId, restored.ActiveAccountId);
        Assert.Equal(CopilotEdition.Work, restored.GetActiveAccount().Edition);
        Assert.Equal("div[role='textbox'][aria-label='Ask Copilot']", restored.GetActiveAccount().CustomInputSelector?.CssSelector);

        Assert.False(restored.IsDarkMode);
        Assert.True(restored.IsSettingsExpanded);
        Assert.True(restored.AutoApproveTools);
        Assert.Equal(15, restored.MaxAutoSteps);
        Assert.Equal(2000, restored.DelayMinMs);
        Assert.Equal(5500, restored.DelayMaxMs);
        Assert.Equal(1280, restored.WindowWidth);
        Assert.Equal(840, restored.WindowHeight);
        Assert.Equal(120, restored.WindowX);
        Assert.Equal(85, restored.WindowY);
        Assert.Equal(@"C:\Projects\Demo", restored.LastWorkspacePath);
    }

    [Fact]
    public void SwitchEdition_RemembersLastUsedAccountPerEdition()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        using var service = new ConfigService(configPath);

        AccountProfile secondWork = null!;
        service.Update(cfg =>
        {
            secondWork = cfg.AddAccount("第二工作号", CopilotEdition.Work);
            cfg.SwitchEdition(CopilotEdition.Personal);
        });

        Assert.Equal(CopilotEdition.Personal, service.Current.GetActiveAccount().Edition);

        service.Update(cfg => cfg.SwitchEdition(CopilotEdition.Work));
        Assert.Equal(secondWork.Id, service.Current.ActiveAccountId);

        // Cannot delete the sole Personal account, can delete extra Work account
        var defaultPersonalId = service.Current.Accounts.First(a => a.Edition == CopilotEdition.Personal).Id;
        Assert.False(service.Current.RemoveAccount(defaultPersonalId));
        Assert.True(service.Current.RemoveAccount(secondWork.Id));
        Assert.Single(service.Current.Accounts, a => a.Edition == CopilotEdition.Work);
    }

    [Fact]
    public void DebouncedSave_FlushesOnDisposeAndRecoversFromCorruptJson()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        using (var service = new ConfigService(configPath))
        {
            service.Update(cfg => cfg.LastWorkspacePath = "/home/user/work", saveImmediately: false, debounceMs: 5000);
        } // Dispose must flush immediately even though 5000ms hasn't elapsed

        using (var verify = new ConfigService(configPath))
        {
            Assert.Equal("/home/user/work", verify.Current.LastWorkspacePath);
        }

        // Write corrupt JSON and ensure graceful recovery + backup creation
        File.WriteAllText(configPath, "{ invalid json !!!", Encoding.UTF8);
        using var recovered = new ConfigService(configPath);
        Assert.Equal(2, recovered.Current.Accounts.Count);
        Assert.NotEmpty(Directory.GetFiles(_tempDir, "*.corrupt.*.bak"));
    }

    [Fact]
    public void AdapterManager_GeneratesExternalFileAndSupportsHotReloadOverride()
    {
        var adaptersPath = Path.Combine(_tempDir, "adapters.json");
        var manager = new AdapterManager(adaptersPath);

        Assert.True(File.Exists(adaptersPath));
        Assert.Equal("https://copilot.microsoft.com", manager.GetRule(CopilotEdition.Personal).DefaultStartUrl);
        Assert.Equal("https://m365.cloud.microsoft/chat", manager.GetRule(CopilotEdition.Work).DefaultStartUrl);

        // Simulate hot-editing external adapters.json after a future webpage update
        var updatedJson = """
        {
          "schemaVersion": 1,
          "rules": [
            {
              "edition": "Work",
              "defaultStartUrl": "https://copilot.cloud.microsoft/enterprise",
              "targetPageUrlPatterns": ["copilot.cloud.microsoft"],
              "webSocketUrlHints": ["ChathubV2"],
              "inputSelectorHints": ["#new-m365-editor"],
              "sendButtonHints": ["#new-send-btn"],
              "fileInputHints": ["input[type='file']"]
            }
          ]
        }
        """;
        File.WriteAllText(adaptersPath, updatedJson, Encoding.UTF8);
        manager.Reload();

        var workRule = manager.GetRule(CopilotEdition.Work);
        Assert.Equal("https://copilot.cloud.microsoft/enterprise", workRule.DefaultStartUrl);
        Assert.Contains("#new-m365-editor", workRule.InputSelectorHints);
        // v1 adapters.json without newChatButtonHints must auto-backfill NewChatButtonHints!
        Assert.NotEmpty(workRule.NewChatButtonHints);
        // Personal rule remains intact
        Assert.Equal("https://copilot.microsoft.com", manager.GetRule(CopilotEdition.Personal).DefaultStartUrl);
    }

    [Fact]
    public void LegacyV1Config_MigratesToV2AutonomousExecuteDefaults_WhilePreservingExplicitV2Settings()
    {
        var configPath = Path.Combine(_tempDir, "legacy_v1_config.json");
        var legacyJson = """
        {
          "autoApproveTools": false,
          "maxAutoSteps": 10,
          "lastWorkspacePath": "C:\\Users\\Demo\\Work"
        }
        """;
        File.WriteAllText(configPath, legacyJson, Encoding.UTF8);

        // First load upgrades schema < 2 to AutoApproveTools=true, InteractionMode=Execute, MaxAutoSteps=25
        using (var migrated = new ConfigService(configPath))
        {
            Assert.Equal(2, migrated.Current.ConfigSchemaVersion);
            Assert.True(migrated.Current.AutoApproveTools);
            Assert.Equal(AgentInteractionMode.Execute, migrated.Current.InteractionMode);
            Assert.Equal(25, migrated.Current.MaxAutoSteps);

            // User explicitly chooses Plan mode and manual tool approval on v2
            migrated.Update(cfg =>
            {
                cfg.InteractionMode = AgentInteractionMode.Plan;
                cfg.AutoApproveTools = false;
            });
        }

        // Subsequent restart on v2 must preserve explicit user choices!
        using var reopened = new ConfigService(configPath);
        Assert.Equal(2, reopened.Current.ConfigSchemaVersion);
        Assert.False(reopened.Current.AutoApproveTools);
        Assert.Equal(AgentInteractionMode.Plan, reopened.Current.InteractionMode);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup locks
        }
    }
}
