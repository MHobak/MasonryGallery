namespace MasonryGallery.Application.Ports;

using MasonryGallery.Application.Settings;
using MasonryGallery.Domain;

public interface ISettingsStore
{
    /// <summary>The settings in effect; read it at the moment of use so changes apply without a restart.</summary>
    AppSettings Current { get; }

    /// <summary>What a fresh install uses; the UI offers it as "restore defaults".</summary>
    AppSettings Defaults { get; }

    /// <summary>Raised after a successful save with the previous and the new settings (on the saving thread).</summary>
    event Action<AppSettings, AppSettings>? Changed;

    /// <summary>Validates, persists and applies <paramref name="settings"/>.</summary>
    Task<Result> SaveAsync(AppSettings settings, CancellationToken ct = default);
}
