using AndroidManager.Core.Abstractions;
using AndroidManager.Core.Models;

namespace AndroidManager.Settings.Services;

public sealed class SettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = AppSettingsStore.Load();

    public event EventHandler<AppSettings>? SettingsChanged;

    public void Save()
    {
        AppSettingsStore.Save(Current);
        SettingsChanged?.Invoke(this, Current);
    }

    public void Reset()
    {
        AppSettingsStore.Reset(Current);
        SettingsChanged?.Invoke(this, Current);
    }
}
