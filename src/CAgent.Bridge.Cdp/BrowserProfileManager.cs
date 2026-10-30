using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CAgent.Core.Adapters;
using CAgent.Core.Configuration;

namespace CAgent.Bridge.Cdp;

public sealed class CdpTargetInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("webSocketDebuggerUrl")]
    public string WebSocketDebuggerUrl { get; set; } = string.Empty;
}

public sealed class BrowserProfileManager
{
    private readonly ConfigService _configService;
    private readonly AdapterManager _adapterManager;
    private readonly HttpClient _httpClient;

    public BrowserProfileManager(
        ConfigService configService,
        AdapterManager adapterManager,
        HttpClient? httpClient = null)
    {
        _configService = configService;
        _adapterManager = adapterManager;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    }

    public async Task<CdpTargetInfo> EnsureBrowserAndPageTargetAsync(
        AccountProfile account,
        CancellationToken cancellationToken = default)
    {
        var port = account.DebugPort;
        var startUrl = _adapterManager.GetEffectiveStartUrl(account);

        if (!await IsDebugPortReadyAsync(port, cancellationToken).ConfigureAwait(false))
        {
            var browserExe = ResolveBrowserExecutable(_configService.Current.CustomBrowserExecutablePath)
                ?? throw new FileNotFoundException(
                    "未检测到 Microsoft Edge 或 Google Chrome 浏览器，请在设置中指定浏览器可执行文件路径。");

            var profileDir = _configService.GetAccountProfileDirectory(account);
            LaunchBrowserProcess(browserExe, port, profileDir, startUrl);

            var ready = false;
            for (var i = 0; i < 30; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                if (await IsDebugPortReadyAsync(port, cancellationToken).ConfigureAwait(false))
                {
                    ready = true;
                    break;
                }
            }

            if (!ready)
            {
                throw new TimeoutException($"启动浏览器调试端口 127.0.0.1:{port} 超时，请确认端口未被非调试进程占用。");
            }
        }

        var target = await FindOrCreateCopilotPageAsync(account, startUrl, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException("未能获取浏览器标签页的 CDP WebSocket 调试地址。");
        }

        return target;
    }

    public async Task<CdpTargetInfo?> TryGetExistingPageTargetAsync(
        AccountProfile account,
        CancellationToken cancellationToken = default)
    {
        if (!await IsDebugPortReadyAsync(account.DebugPort, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var targets = await GetPageTargetsAsync(account.DebugPort, cancellationToken).ConfigureAwait(false);
        return SelectBestPageTarget(targets, account);
    }

    public static string? ResolveBrowserExecutable(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return customPath;
        }

        if (OperatingSystem.IsWindows())
        {
            string[] candidates =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
            ];
            return candidates.FirstOrDefault(File.Exists);
        }

        if (OperatingSystem.IsMacOS())
        {
            string[] macCandidates =
            [
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
                "/Applications/Chromium.app/Contents/MacOS/Chromium"
            ];
            return macCandidates.FirstOrDefault(File.Exists);
        }

        // Linux
        string[] linuxNames =
        [
            "microsoft-edge",
            "microsoft-edge-stable",
            "google-chrome",
            "google-chrome-stable",
            "chromium",
            "chromium-browser"
        ];

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/usr/local/bin:/snap/bin")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var binName in linuxNames)
        {
            foreach (var dir in pathDirs)
            {
                var full = Path.Combine(dir.Trim(), binName);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private async Task<bool> IsDebugPortReadyAsync(int port, CancellationToken ct)
    {
        try
        {
            using var resp = await _httpClient.GetAsync($"http://127.0.0.1:{port}/json/version", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<CdpTargetInfo>> GetPageTargetsAsync(int port, CancellationToken ct)
    {
        try
        {
            var list = await _httpClient.GetFromJsonAsync<List<CdpTargetInfo>>(
                $"http://127.0.0.1:{port}/json/list",
                ct).ConfigureAwait(false);
            return list?.Where(t => string.Equals(t.Type, "page", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task<CdpTargetInfo> FindOrCreateCopilotPageAsync(
        AccountProfile account,
        string startUrl,
        CancellationToken ct)
    {
        var pages = await GetPageTargetsAsync(account.DebugPort, ct).ConfigureAwait(false);
        var best = SelectBestPageTarget(pages, account);
        if (best is not null)
        {
            return best;
        }

        // Open a new tab via PUT /json/new?{url} (modern Chromium requires PUT) with GET fallback
        var encodedUrl = Uri.EscapeDataString(startUrl);
        var endpoint = $"http://127.0.0.1:{account.DebugPort}/json/new?{encodedUrl}";
        try
        {
            using var putResp = await _httpClient.PutAsync(endpoint, content: null, ct).ConfigureAwait(false);
            if (putResp.IsSuccessStatusCode)
            {
                var created = await putResp.Content.ReadFromJsonAsync<CdpTargetInfo>(cancellationToken: ct).ConfigureAwait(false);
                if (created is not null)
                {
                    return created;
                }
            }
        }
        catch
        {
            // Fallback to GET for older Chromium builds
        }

        var getCreated = await _httpClient.GetFromJsonAsync<CdpTargetInfo>(endpoint, ct).ConfigureAwait(false);
        return getCreated ?? throw new InvalidOperationException("无法在调试浏览器中打开新标签页。");
    }

    private CdpTargetInfo? SelectBestPageTarget(List<CdpTargetInfo> pages, AccountProfile account)
    {
        if (pages.Count == 0)
        {
            return null;
        }

        var patterns = _adapterManager.GetRule(account.Edition).TargetPageUrlPatterns;
        var matched = pages.FirstOrDefault(p =>
            patterns.Any(pat => p.Url.Contains(pat, StringComparison.OrdinalIgnoreCase)));
        if (matched is not null)
        {
            return matched;
        }

        // Also accept Microsoft SSO / Entra ID login pages in progress
        var loginPage = pages.FirstOrDefault(p =>
            p.Url.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
            p.Url.Contains("login.live.com", StringComparison.OrdinalIgnoreCase) ||
            p.Url.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase));

        return loginPage ?? pages.FirstOrDefault(p => !p.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase));
    }

    private static void LaunchBrowserProcess(string exePath, int port, string profileDir, string startUrl)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false
        };

        psi.ArgumentList.Add($"--remote-debugging-port={port}");
        psi.ArgumentList.Add($"--user-data-dir={profileDir}");
        psi.ArgumentList.Add("--no-first-run");
        psi.ArgumentList.Add("--no-default-browser-check");
        psi.ArgumentList.Add("--disable-background-timer-throttling");
        psi.ArgumentList.Add("--disable-backgrounding-occluded-windows");
        psi.ArgumentList.Add("--disable-renderer-backgrounding");
        psi.ArgumentList.Add("--disable-blink-features=AutomationControlled");
        psi.ArgumentList.Add("--remote-allow-origins=*");
        psi.ArgumentList.Add(startUrl);

        Process.Start(psi);
    }
}
