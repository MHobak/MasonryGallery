namespace MasonryGallery.Infrastructure.Settings;

using System.Text.Json;
using System.Text.Json.Nodes;
using MasonryGallery.Application.Ports;
using MasonryGallery.Application.Settings;
using MasonryGallery.Domain;
using Microsoft.Extensions.Logging;

/// <summary>
/// Defaults from the "Gallery" section of appsettings.json (shipped next to the executable), overlaid with the
/// user's settings.json. Only values that differ from the defaults are written, so a new release's defaults
/// still reach users who changed something else.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private const string Section = "Gallery";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _userPath;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private volatile AppSettings _current;

    public JsonSettingsStore(string defaultsPath, string userPath, ILogger<JsonSettingsStore> logger)
    {
        _userPath = userPath;
        _logger = logger;
        Defaults = Load(defaultsPath, new JsonObject(), section: Section, "defaults") ?? new AppSettings();
        _current = Load(userPath, ToJson(Defaults), section: null, "user settings") ?? Defaults;
    }

    public AppSettings Defaults { get; }
    public AppSettings Current => _current;
    public event Action<AppSettings, AppSettings>? Changed;

    public async Task<Result> SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var valid = settings.Validate();
        if (!valid.IsSuccess)
            return valid;

        AppSettings previous;
        await _saveLock.WaitAsync(ct);
        try
        {
            var changes = new JsonObject();
            var defaults = ToJson(Defaults);
            foreach (var (key, value) in ToJson(settings))
            {
                if (!JsonNode.DeepEquals(value, defaults[key]))
                    changes[key] = value?.DeepClone();
            }

            var temp = _userPath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, changes.ToJsonString(Json), ct);
                File.Move(temp, _userPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save settings to {Path}", _userPath);
                return Result.Failure($"Could not save settings: {ex.Message}");
            }

            previous = _current;
            _current = settings;
        }
        finally
        {
            _saveLock.Release();
        }

        _logger.LogInformation("Settings saved");
        if (!previous.Equals(settings))
            Changed?.Invoke(previous, settings);
        return Result.Success();
    }

    // A missing file means "nothing to change". A broken or invalid one is logged and ignored, never overwritten
    // until the user saves, so a hand-edit with a typo isn't silently thrown away.
    private AppSettings? Load(string path, JsonObject baseline, string? section, string what)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (section is not null)
                node = node?[section];
            if (node is not JsonObject overrides)
                return null;

            var merged = (JsonObject)baseline.DeepClone();
            foreach (var (key, value) in overrides)
            {
                // Names match case-insensitively; drop the baseline's spelling so the override wins.
                var existing = merged.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    merged.Remove(existing);
                merged[key] = value?.DeepClone();
            }
            var settings = merged.Deserialize<AppSettings>(Json) ?? new AppSettings();

            var valid = settings.Validate();
            if (valid.IsSuccess)
                return settings;
            _logger.LogWarning("Ignoring {What} in {Path}: {Error}", what, path, valid.Error);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable {What} in {Path}", what, path);
        }
        return null;
    }

    private static JsonObject ToJson(AppSettings settings) => JsonSerializer.SerializeToNode(settings, Json)!.AsObject();
}
