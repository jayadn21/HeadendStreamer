using HeadendStreamer.Web.Models.Entities;
using System.Text.Json;

namespace HeadendStreamer.Web.Services;

public class ConfigService
{
    private readonly ILogger<ConfigService> _logger;
    private readonly string _configDirectory;
    private Dictionary<string, StreamConfig> _configs = new();
    private readonly object _lock = new();
    
    public ConfigService(ILogger<ConfigService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _configDirectory = Path.Combine(env.ContentRootPath, "configs");
        Directory.CreateDirectory(_configDirectory);
        LoadConfigs();
    }
    
    public async Task<StreamConfig?> GetConfigAsync(string id)
    {
        lock (_lock)
        {
            return _configs.TryGetValue(id, out var config) ? config : null;
        }
    }
    
    public List<StreamConfig> GetAllConfigs()
    {
        lock (_lock)
        {
            return _configs.Values.ToList();
        }
    }
    
    public async Task<StreamConfig> CreateConfigAsync(StreamConfig config)
    {
        config.Id = Guid.NewGuid().ToString();
        config.CreatedAt = DateTime.UtcNow;
        config.UpdatedAt = DateTime.UtcNow;
        
        lock (_lock)
        {
            _configs[config.Id] = config;
        }
        await SaveConfigAsync(config);
        
        _logger.LogInformation($"Created new stream config: {config.Name} ({config.Id})");
        return config;
    }
    
    public async Task<StreamConfig?> UpdateConfigAsync(string id, StreamConfig updates)
    {
        StreamConfig? existingConfig;
        lock (_lock)
        {
            if (!_configs.TryGetValue(id, out existingConfig))
                return null;
        }
        
        // Update properties
        existingConfig.Name = updates.Name ?? existingConfig.Name;
        existingConfig.Description = updates.Description ?? existingConfig.Description;
        existingConfig.Enabled = updates.Enabled;
        existingConfig.InputDevice = updates.InputDevice ?? existingConfig.InputDevice;
        existingConfig.InputFormat = updates.InputFormat ?? existingConfig.InputFormat;
        existingConfig.PixelFormat = updates.PixelFormat ?? existingConfig.PixelFormat;
        existingConfig.VideoSize = updates.VideoSize ?? existingConfig.VideoSize;
        existingConfig.FrameRate = updates.FrameRate;
        existingConfig.VideoCodec = updates.VideoCodec ?? existingConfig.VideoCodec;
        existingConfig.Preset = updates.Preset ?? existingConfig.Preset;
        existingConfig.Tune = updates.Tune ?? existingConfig.Tune;
        existingConfig.Bitrate = updates.Bitrate ?? existingConfig.Bitrate;
        existingConfig.GopSize = updates.GopSize;
        existingConfig.EnableAudio = updates.EnableAudio;
        existingConfig.AudioDevice = updates.AudioDevice ?? existingConfig.AudioDevice;
        existingConfig.AudioCodec = updates.AudioCodec ?? existingConfig.AudioCodec;
        existingConfig.AudioBitrate = updates.AudioBitrate ?? existingConfig.AudioBitrate;
        existingConfig.AudioVolume = updates.AudioVolume;
        existingConfig.MulticastIp = updates.MulticastIp ?? existingConfig.MulticastIp;
        existingConfig.Port = updates.Port;
        existingConfig.Ttl = updates.Ttl;
        existingConfig.OutputFormat = updates.OutputFormat ?? existingConfig.OutputFormat;
        existingConfig.AdvancedOptions = updates.AdvancedOptions ?? existingConfig.AdvancedOptions;
        existingConfig.UpdatedAt = DateTime.UtcNow;
        
        await SaveConfigAsync(existingConfig);
        
        _logger.LogInformation($"Updated stream config: {existingConfig.Name} ({id})");
        return existingConfig;
    }
    
    public async Task<bool> DeleteConfigAsync(string id)
    {
        lock (_lock)
        {
            if (!_configs.ContainsKey(id))
                return false;
            
            _configs.Remove(id);
        }
        
        _logger.LogInformation($"Deleted stream config: {id}");
        return true;
    }
    
    public async Task<string> CreateBackupAsync()
    {
        var backupDir = Path.Combine(_configDirectory, "backups");
        Directory.CreateDirectory(backupDir);
        
        var backupFile = Path.Combine(backupDir, $"backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        
        object backupData;
        lock (_lock)
        {
            backupData = new
            {
                Timestamp = DateTime.UtcNow,
                Configs = _configs.Values.ToList()
            };
        }
        
        var json = JsonSerializer.Serialize(backupData, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        
        await File.WriteAllTextAsync(backupFile, json);
        
        // Keep only last 10 backups
        var backupFiles = Directory.GetFiles(backupDir, "backup_*.json")
            .OrderByDescending(f => f)
            .Skip(10);
        
        foreach (var file in backupFiles)
        {
            File.Delete(file);
        }
        
        return backupFile;
    }
    
    public async Task<bool> RestoreBackupAsync(string backupFilePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(backupFilePath);
            var backupData = JsonSerializer.Deserialize<BackupData>(json);
            
            if (backupData?.Configs == null)
                return false;
            
            lock (_lock)
            {
                // Clear existing configs
                _configs.Clear();
                
                // Restore configs
                foreach (var config in backupData.Configs)
                {
                    _configs[config.Id] = config;
                }
            }
            
            foreach (var config in backupData.Configs)
            {
                await SaveConfigAsync(config);
            }
            
            _logger.LogInformation($"Restored backup with {_configs.Count} configs");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore backup");
            return false;
        }
    }
    
    public async Task<StreamConfig> CreateConfigFromTemplateAsync(string templateName)
    {
        var template = GetTemplate(templateName);
        return await CreateConfigAsync(template);
    }
    
    private StreamConfig GetTemplate(string templateName)
    {
        return templateName.ToLower() switch
        {
            "hd_1080p" => new StreamConfig
            {
                Name = "HD 1080p Stream",
                Description = "High Definition 1080p stream",
                VideoSize = "1920x1080",
                FrameRate = 30,
                Bitrate = "5000k",
                VideoCodec = "libx264",
                Preset = "veryfast",
                Tune = "zerolatency",
                GopSize = 60
            },
            "hd_720p" => new StreamConfig
            {
                Name = "HD 720p Stream",
                Description = "High Definition 720p stream",
                VideoSize = "1280x720",
                FrameRate = 30,
                Bitrate = "2500k",
                VideoCodec = "libx264",
                Preset = "veryfast",
                Tune = "zerolatency",
                GopSize = 60
            },
            "sd_480p" => new StreamConfig
            {
                Name = "SD 480p Stream",
                Description = "Standard Definition 480p stream",
                VideoSize = "720x480",
                FrameRate = 30,
                Bitrate = "1500k",
                VideoCodec = "libx264",
                Preset = "veryfast",
                Tune = "zerolatency",
                GopSize = 60
            },
            _ => new StreamConfig
            {
                Name = "New Stream",
                Description = "New streaming configuration"
            }
        };
    }
    
    private async Task SaveConfigAsync(StreamConfig config)
    {
        var configPath = GetConfigFilePath(config.Id);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        
        await File.WriteAllTextAsync(configPath, json);
    }
    
    private void LoadConfigs()
    {
        try
        {
            var configFiles = Directory.GetFiles(_configDirectory, "*.json")
                .Where(f => !f.Contains("backup"))
                .ToArray();
            
            foreach (var file in configFiles)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var config = JsonSerializer.Deserialize<StreamConfig>(json);
                    
                    if (config != null && !string.IsNullOrEmpty(config.Id))
                    {
                        lock (_lock)
                        {
                            _configs[config.Id] = config;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to load config file: {file}");
                }
            }
            
            _logger.LogInformation($"Loaded {_configs.Count} stream configurations");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load configurations");
        }
    }
    
    private string GetConfigFilePath(string id)
    {
        return Path.Combine(_configDirectory, $"{id}.json");
    }
    
    private class BackupData
    {
        public DateTime Timestamp { get; set; }
        public List<StreamConfig>? Configs { get; set; }
    }
}