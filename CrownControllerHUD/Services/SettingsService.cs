using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CrownControllerHUD.Models;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.Services
{
    public interface ISettingsService
    {
        AppSettings Settings { get; }
        Task LoadAsync();
        Task SaveAsync();
        void ResetToDefaults();
        event EventHandler<SettingsChangedEventArgs> SettingsChanged;
    }

    public class SettingsChangedEventArgs : EventArgs
    {
        public string Section { get; }
        public SettingsChangedEventArgs(string section)
        {
            Section = section;
        }
    }

    public class SettingsService : ISettingsService
    {
        private readonly ILogger<SettingsService> _logger;
        private readonly string _settingsPath;
        private AppSettings _settings = new();
        private readonly JsonSerializerOptions _jsonOptions;

        public AppSettings Settings => _settings;

        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;

        public SettingsService(ILogger<SettingsService> logger)
        {
            _logger = logger;
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appFolder = Path.Combine(appDataPath, "CrownControllerHUD");
            Directory.CreateDirectory(appFolder);
            _settingsPath = Path.Combine(appFolder, "settings.json");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        public async Task LoadAsync()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = await File.ReadAllTextAsync(_settingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
                    if (loaded != null)
                    {
                        _settings = MergeWithDefaults(loaded);
                        _logger.LogInformation("Settings loaded from {Path}", _settingsPath);
                    }
                }
                else
                {
                    _logger.LogInformation("No settings file found, using defaults");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load settings, using defaults");
                _settings = new AppSettings();
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                var json = JsonSerializer.Serialize(_settings, _jsonOptions);
                await File.WriteAllTextAsync(_settingsPath, json);
                _logger.LogInformation("Settings saved to {Path}", _settingsPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save settings");
            }
        }

        public void ResetToDefaults()
        {
            _settings = new AppSettings();
            _logger.LogInformation("Settings reset to defaults");
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs("All"));
        }

        private AppSettings MergeWithDefaults(AppSettings loaded)
        {
            var defaults = new AppSettings();
            // Simple merge - in production, use a proper deep merge
            return loaded;
        }

        public void NotifySectionChanged(string section)
        {
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(section));
        }
    }
}