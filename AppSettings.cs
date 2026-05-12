using System.Text.Json;

namespace Babelive;

/// <summary>
/// User-facing app settings persisted to
/// <c>%APPDATA%\Babelive\settings.json</c>. Loaded once at startup,
/// re-saved by the API settings dialog. Empty / null fields mean "use
/// built-in default":
/// <list type="bullet">
///   <item><see cref="ApiKey"/> empty → app refuses to Start until set via API… dialog</item>
///   <item><see cref="BaseUrl"/> empty → official <c>wss://api.openai.com</c></item>
/// </list>
/// </summary>
public sealed class AppSettings
{
    public string? ApiKey  { get; set; }
    public string? BaseUrl { get; set; }

    private static readonly string Dir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Babelive");

    public static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* corrupt / unreadable — start fresh rather than crash */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
