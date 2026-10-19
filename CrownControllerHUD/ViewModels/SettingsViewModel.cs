using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CrownControllerHUD.Models;
using CrownControllerHUD.Services;
using Microsoft.Extensions.Logging;

namespace CrownControllerHUD.ViewModels
{
    public class SettingsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly ISettingsService _settingsService;
        private readonly IStartupService _startupService;
        private readonly ILogger<SettingsViewModel> _logger;

        private AppSettings _settings;
        private bool _hasChanges = false;

        public AppSettings Settings
        {
            get => _settings;
            set => SetProperty(ref _settings, value);
        }

        public bool HasChanges
        {
            get => _hasChanges;
            set => SetProperty(ref _hasChanges, value);
        }

        public ICommand SaveCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand CancelCommand { get; }

        public SettingsViewModel(
            ISettingsService settingsService,
            IStartupService startupService,
            ILogger<SettingsViewModel> logger)
        {
            _settingsService = settingsService;
            _startupService = startupService;
            _logger = logger;

            _settings = _settingsService.Settings;

            SaveCommand = new RelayCommand(async () => await SaveAsync());
            ResetCommand = new RelayCommand(() => Reset());
            CancelCommand = new RelayCommand(() => Cancel());

            _settingsService.SettingsChanged += OnSettingsChanged;
        }

        private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
        {
            // Settings were changed externally, reload
            _settings = _settingsService.Settings;
            OnPropertyChanged(nameof(Settings));
            HasChanges = false;
        }

        public async Task SaveAsync()
        {
            try
            {
                // Sync startup setting
                if (_settings.General.StartWithWindows != _startupService.IsEnabled)
                {
                    _startupService.SetEnabled(_settings.General.StartWithWindows);
                }

                await _settingsService.SaveAsync();
                HasChanges = false;
                _logger.LogInformation("Settings saved");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save settings");
            }
        }

        public void Reset()
        {
            _settingsService.ResetToDefaults();
            _settings = _settingsService.Settings;
            OnPropertyChanged(nameof(Settings));
            HasChanges = true;
        }

        public void Cancel()
        {
            _settings = _settingsService.Settings;
            OnPropertyChanged(nameof(Settings));
            HasChanges = false;
        }

        protected void OnSettingsPropertyChanged(string propertyName)
        {
            HasChanges = true;
            OnPropertyChanged(propertyName);
        }

        public void Dispose()
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}