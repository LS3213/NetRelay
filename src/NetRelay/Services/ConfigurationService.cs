using System.IO;
using System.Text;
using System.Text.Json;
using NetRelay.Models;

namespace NetRelay.Services;

public sealed class ConfigurationService
{
    private readonly string _directoryPath;
    private readonly string _filePath;
    private readonly string _tempFilePath;
    private readonly JsonSerializerOptions _jsonOptions;

    public AppConfiguration Current { get; private set; }

    public ConfigurationService()
    {
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

    // Constructor for custom path (useful for testing)
    public ConfigurationService(string customDirectoryPath)
    {
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
                return defaultConfiguration;
            }

            var jsonContent = File.ReadAllText(_filePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(jsonContent))
            {
                HandleCorruptedConfig("配置文件内容为空。");
                return CreateDefaultConfiguration();
            }

            var config = JsonSerializer.Deserialize<AppConfiguration>(jsonContent, _jsonOptions);
            if (config is null)
            {
                HandleCorruptedConfig("反序列化配置结果为 null。");
                return CreateDefaultConfiguration();
            }

            return config;
        }
        catch (Exception exception)
        {
            HandleCorruptedConfig($"读取或反序列化配置发生异常：{exception.Message}");
            return CreateDefaultConfiguration();
        }
    }

    public void Save()
    {
        SaveInternal(Current);
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
            SchemaVersion = 1,
            ProbePolicy = new ConnectivityProbePolicy(),
            Rules = []
        };
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
