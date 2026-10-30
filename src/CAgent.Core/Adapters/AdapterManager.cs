using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CAgent.Core.Configuration;

namespace CAgent.Core.Adapters;

public sealed class EditionAdapterRule
{
    public CopilotEdition Edition { get; set; }
    public string DefaultStartUrl { get; set; } = string.Empty;
    public List<string> TargetPageUrlPatterns { get; set; } = [];
    public List<string> WebSocketUrlHints { get; set; } = [];
    public List<string> InputSelectorHints { get; set; } = [];
    public List<string> SendButtonHints { get; set; } = [];
    public List<string> FileInputHints { get; set; } = [];
}

public sealed class AdaptersDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<EditionAdapterRule> Rules { get; set; } = [];
}

public sealed class AdapterManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string? _externalFilePath;
    private Dictionary<CopilotEdition, EditionAdapterRule> _rules = new();

    public AdapterManager(string? externalFilePath = null)
    {
        _externalFilePath = externalFilePath;
        Reload();
    }

    public EditionAdapterRule GetRule(CopilotEdition edition)
    {
        if (_rules.TryGetValue(edition, out var rule))
        {
            return rule;
        }
        return CreateBuiltInDefaults().Rules.First(r => r.Edition == edition);
    }

    public string GetEffectiveStartUrl(AccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!string.IsNullOrWhiteSpace(account.CustomUrl))
        {
            return account.CustomUrl.Trim();
        }
        return GetRule(account.Edition).DefaultStartUrl;
    }

    public void Reload()
    {
        var defaults = CreateBuiltInDefaults();
        var map = defaults.Rules.ToDictionary(r => r.Edition, r => r);

        if (!string.IsNullOrWhiteSpace(_externalFilePath))
        {
            if (!File.Exists(_externalFilePath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(_externalFilePath);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(
                        _externalFilePath,
                        JsonSerializer.Serialize(defaults, JsonOptions),
                        new UTF8Encoding(false));
                }
                catch
                {
                    // Ignore write failures in read-only directories
                }
            }
            else
            {
                try
                {
                    var raw = File.ReadAllText(_externalFilePath, Encoding.UTF8);
                    var doc = JsonSerializer.Deserialize<AdaptersDocument>(raw, JsonOptions);
                    if (doc?.Rules is { Count: > 0 })
                    {
                        foreach (var custom in doc.Rules)
                        {
                            map[custom.Edition] = custom;
                        }
                    }
                }
                catch
                {
                    // Fall back to built-in rules if external JSON has syntax errors
                }
            }
        }

        _rules = map;
    }

    public static AdaptersDocument CreateBuiltInDefaults() => new()
    {
        SchemaVersion = 1,
        Rules =
        [
            new EditionAdapterRule
            {
                Edition = CopilotEdition.Personal,
                DefaultStartUrl = "https://copilot.microsoft.com",
                TargetPageUrlPatterns =
                [
                    "copilot.microsoft.com",
                    "bing.com/chat"
                ],
                WebSocketUrlHints =
                [
                    "copilot.microsoft.com/c/api/chat",
                    "/api/chat",
                    "sydney.bing.com"
                ],
                InputSelectorHints =
                [
                    "#userInput",
                    "textarea[placeholder]",
                    "[contenteditable='true'][role='textbox']",
                    "textarea"
                ],
                SendButtonHints =
                [
                    "button[data-testid='submit-button']",
                    "button[aria-label*='Submit']",
                    "button[aria-label*='Send']",
                    "button[aria-label*='发送']",
                    "button[title*='Submit']",
                    "button[title*='发送']"
                ],
                FileInputHints =
                [
                    "input[type='file']"
                ]
            },
            new EditionAdapterRule
            {
                Edition = CopilotEdition.Work,
                DefaultStartUrl = "https://m365.cloud.microsoft/chat",
                TargetPageUrlPatterns =
                [
                    "m365.cloud.microsoft",
                    "copilot.cloud.microsoft",
                    "office.com/chat",
                    "substrate.office.com"
                ],
                WebSocketUrlHints =
                [
                    "substrate.office.com",
                    "Chathub",
                    "m365Copilot",
                    "/chat"
                ],
                InputSelectorHints =
                [
                    "[contenteditable='true'][role='textbox']",
                    "#m365-chat-input-shared-id",
                    "div[role='textbox'][aria-label]",
                    "textarea[aria-label]",
                    "textarea"
                ],
                SendButtonHints =
                [
                    "button[aria-label*='Send']",
                    "button[aria-label*='Submit']",
                    "button[aria-label*='发送']",
                    "button[data-testid*='send']",
                    "button[title*='Send']",
                    "button[title*='发送']"
                ],
                FileInputHints =
                [
                    "input[type='file']"
                ]
            }
        ]
    };
}
