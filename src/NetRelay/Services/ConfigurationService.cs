using System.IO;
using System.Text;
using System.Text.Json;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class ConfigurationService
{
    public static ConfigurationService? Instance { get; private set; }

    private readonly string _directoryPath;
    private readonly string _filePath;
    private readonly string _tempFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public AppConfiguration Current { get; private set; }
    public bool IsAutomationEnabled { get; private set; } = true;
    public string? AutomationDisabledReason { get; private set; }

    public ConfigurationService()
    {
        Instance = this;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _directoryPath = Path.Combine(appData, "NetRelay");
        _filePath = Path.Combine(_directoryPath, "config.json");
        _tempFilePath = _filePath + ".tmp";

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        Current = Load();
    }

    public ConfigurationService(string customDirectoryPath)
    {
        Instance = this;
        _directoryPath = customDirectoryPath;
        _filePath = Path.Combine(_directoryPath, "config.json");
        _tempFilePath = _filePath + ".tmp";

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        Current = Load();
    }

    public AppConfiguration Load()
    {
        try
        {
            if (!Directory.Exists(_directoryPath))
            {
                Directory.CreateDirectory(_directoryPath);
            }

            if (!File.Exists(_filePath))
            {
                var defaultConfiguration = CreateDefaultConfiguration();
                SaveInternal(defaultConfiguration);
                UpdateValidationState(defaultConfiguration);
                return defaultConfiguration;
            }

            var jsonContent = File.ReadAllText(_filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                HandleCorruptedConfig("配置文件内容为空。");
                var defaultConfiguration = CreateDefaultConfiguration();
                UpdateValidationState(defaultConfiguration);
                return defaultConfiguration;
            }

            var config = JsonSerializer.Deserialize<AppConfiguration>(jsonContent, _jsonOptions);
            if (config is null)
            {
                HandleCorruptedConfig("反序列化配置结果为 null。");
                var defaultConfiguration = CreateDefaultConfiguration();
                UpdateValidationState(defaultConfiguration);
                return defaultConfiguration;
            }

            // 自动迁移与修正已知失效或高延迟的默认探测端点
            bool configUpdated = false;

            // 模式版本迁移：从 v1 迁移到 v2 时，将 manualDisableProtection 强制设为默认开启（true）
            if (config.SchemaVersion < 2)
            {
                config.ManualDisableProtection = true;
                config.SchemaVersion = 2;
                configUpdated = true;
            }

            if (config.SchemaVersion < 3)
            {
                config.SchemaVersion = 3;
                configUpdated = true;
            }

            if (config.SchemaVersion < 4)
            {
                config.AutoCheckUpdatesOnStartup = true;
                config.SchemaVersion = 4;
                configUpdated = true;
            }

            if (string.IsNullOrWhiteSpace(config.InstallationId))
            {
                config.InstallationId = Guid.NewGuid().ToString();
                configUpdated = true;
            }

            if (config.ProbePolicy?.Endpoints != null)
            {
                for (int i = 0; i < config.ProbePolicy.Endpoints.Count; i++)
                {
                    var ep = config.ProbePolicy.Endpoints[i];
                    if (string.Equals(ep.Url, "https://www.msftconnecttest.com/connecttest.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        ep.Url = "http://www.msftconnecttest.com/connecttest.txt";
                        configUpdated = true;
                    }
                    if (string.Equals(ep.Url, "https://www.cloudflare.com/cdn-cgi/trace", StringComparison.OrdinalIgnoreCase))
                    {
                        ep.Url = "https://www.baidu.com";
                        configUpdated = true;
                    }
                }
            }

            if (configUpdated)
            {
                SaveInternal(config);
            }

            UpdateValidationState(config);
            return config;
        }
        catch (Exception exception)
        {
            HandleCorruptedConfig($"读取或反序列化配置发生异常：{exception.Message}");
            var defaultConfiguration = CreateDefaultConfiguration();
            UpdateValidationState(defaultConfiguration);
            return defaultConfiguration;
        }
    }

    public void Save()
    {
        ValidateCurrent();
        SaveInternal(Current);
    }

    public string? ValidateCurrent()
    {
        UpdateValidationState(Current);
        return AutomationDisabledReason;
    }

    private void SaveInternal(AppConfiguration config)
    {
        try
        {
            if (!Directory.Exists(_directoryPath))
            {
                Directory.CreateDirectory(_directoryPath);
            }

            var jsonContent = JsonSerializer.Serialize(config, _jsonOptions);
            File.WriteAllText(_tempFilePath, jsonContent, Encoding.UTF8);

            if (File.Exists(_filePath))
            {
                try
                {
                    File.Replace(_tempFilePath, _filePath, null);
                }
                catch
                {
                    // Fallback replacement if File.Replace fails
                    File.Delete(_filePath);
                    File.Move(_tempFilePath, _filePath);
                }
            }
            else
            {
                File.Move(_tempFilePath, _filePath);
            }
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"无法保存配置文件：{exception.Message}", exception);
        }
    }

    private AppConfiguration CreateDefaultConfiguration()
    {
        return new AppConfiguration
        {
            SchemaVersion = 4,
            ProbePolicy = new ConnectivityProbePolicy(),
            Rules = [],
            AutoStart = false,
            AutoCheckUpdatesOnStartup = true,
            InstallationId = Guid.NewGuid().ToString()
        };
    }

    private void UpdateValidationState(AppConfiguration config)
    {
        AutomationDisabledReason = ConnectivityProbePolicyValidator.ValidateConfig(config);
        IsAutomationEnabled = AutomationDisabledReason is null;
    }

    private void HandleCorruptedConfig(string reason)
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var corruptedPath = Path.Combine(_directoryPath, $"config.json.corrupted-{timestamp}");
                File.Move(_filePath, corruptedPath);
            }
        }
        catch
        {
            // Suppress secondary errors during recovery fallback
        }

        Current = CreateDefaultConfiguration();
        try
        {
            SaveInternal(Current);
        }
        catch
        {
            // Suppress secondary errors
        }
    }
}
