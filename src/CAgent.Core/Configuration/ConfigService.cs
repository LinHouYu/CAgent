using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CAgent.Core.Configuration;

public sealed class ConfigService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _sync = new();
    private Timer? _debounceTimer;
    private bool _hasPendingSave;
    private bool _disposed;

    public string ConfigFilePath { get; }
    public string DataDirectory { get; }
    public AppConfig Current { get; private set; }

    public event Action<AppConfig>? ConfigSaved;

    public ConfigService(string? customConfigFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customConfigFilePath))
        {
            ConfigFilePath = Path.GetFullPath(customConfigFilePath);
            DataDirectory = Path.GetDirectoryName(ConfigFilePath)
                ?? Path.Combine(Path.GetTempPath(), "CAgent");
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            DataDirectory = Path.Combine(appData, "CAgent");
            ConfigFilePath = Path.Combine(DataDirectory, "config.json");
        }

        Directory.CreateDirectory(DataDirectory);
        Current = LoadOrCreate();
    }

    public string GetProfilesRootDirectory()
    {
        var dir = Path.Combine(DataDirectory, "profiles");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetAccountProfileDirectory(AccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var safeDirName = string.Join("_", account.ProfileDirName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeDirName))
        {
            safeDirName = $"profile_{account.Id}";
        }
        var dir = Path.Combine(GetProfilesRootDirectory(), safeDirName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Update(Action<AppConfig> mutator, bool saveImmediately = true, int debounceMs = 200)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        lock (_sync)
        {
            mutator(Current);
            Current.EnsureDefaults();
            if (saveImmediately || debounceMs <= 0)
            {
                SaveInternal();
            }
            else
            {
                ScheduleSaveInternal(debounceMs);
            }
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            SaveInternal();
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (_hasPendingSave)
            {
                SaveInternal();
            }
        }
    }

    private AppConfig LoadOrCreate()
    {
        if (!File.Exists(ConfigFilePath))
        {
            var fresh = new AppConfig();
            fresh.EnsureDefaults();
            WriteAtomically(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            loaded.EnsureDefaults();
            return loaded;
        }
        catch
        {
            // Back up corrupt config file so user data isn't silently destroyed
            try
            {
                var backupPath = $"{ConfigFilePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(ConfigFilePath, backupPath, overwrite: true);
            }
            catch
            {
                // Ignore backup errors
            }

            var fallback = new AppConfig();
            fallback.EnsureDefaults();
            WriteAtomically(fallback);
            return fallback;
        }
    }

    private void ScheduleSaveInternal(int debounceMs)
    {
        _hasPendingSave = true;
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ =>
        {
            lock (_sync)
            {
                if (!_disposed && _hasPendingSave)
                {
                    SaveInternal();
                }
            }
        }, null, debounceMs, Timeout.Infinite);
    }

    private void SaveInternal()
    {
        _hasPendingSave = false;
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        Current.EnsureDefaults();
        WriteAtomically(Current);
        ConfigSaved?.Invoke(Current);
    }

    private void WriteAtomically(AppConfig config)
    {
        Directory.CreateDirectory(DataDirectory);
        var tempPath = $"{ConfigFilePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, ConfigFilePath, overwrite: true);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            if (_hasPendingSave)
            {
                SaveInternal();
            }
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _disposed = true;
        }
    }
}
