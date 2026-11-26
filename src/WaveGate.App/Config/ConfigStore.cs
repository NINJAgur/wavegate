using System.IO;
using System.Text.Json;

namespace WaveGate.Overlay.Config;

/// <summary>%AppData%\WaveGate\board.json plus a clips\ folder holding copies of imported files.</summary>
public sealed class ConfigStore
{
    public static readonly string[] SupportedExtensions = [".mp3", ".wav", ".ogg", ".mp4", ".m4a"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveGate");

    public string ClipsDir => Path.Combine(Root, "clips");
    private string ConfigPath => Path.Combine(Root, "board.json");

    public BoardConfig Load()
    {
        Directory.CreateDirectory(ClipsDir);
        if (!File.Exists(ConfigPath)) return new BoardConfig();

        try
        {
            return JsonSerializer.Deserialize<BoardConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new BoardConfig();
        }
        catch (JsonException)
        {
            // Keep the broken file for the user instead of overwriting it on the next save.
            File.Move(ConfigPath, $"{ConfigPath}.bad-{DateTime.Now:yyyyMMddHHmmss}");
            return new BoardConfig();
        }
    }

    public void Save(BoardConfig config)
    {
        Directory.CreateDirectory(Root);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, ConfigPath, overwrite: true); // never leaves a half-written board.json
    }

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public string ClipPath(ClipEntry clip) => Path.Combine(ClipsDir, clip.FileName);

    /// <summary>
    /// Expands dropped paths into (file, category) pairs. Files inside a dropped folder take the
    /// name of the folder that directly contains them; loose files get <paramref name="defaultCategory"/>.
    /// </summary>
    public static IEnumerable<(string File, string? Category)> ExpandImports(IEnumerable<string> paths, string? defaultCategory)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(IsSupported).Order())
                    yield return (file, Path.GetFileName(Path.GetDirectoryName(file)));
            }
            else if (IsSupported(path))
            {
                yield return (path, defaultCategory);
            }
        }
    }

    /// <summary>Copies the file into the clips folder under a unique name.</summary>
    public ClipEntry Import(string sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();

        var fileName = name + ext;
        for (int n = 2; File.Exists(Path.Combine(ClipsDir, fileName)); n++)
            fileName = $"{name} ({n}){ext}";

        File.Copy(sourcePath, Path.Combine(ClipsDir, fileName));
        return new ClipEntry { Name = name, FileName = fileName };
    }
}
