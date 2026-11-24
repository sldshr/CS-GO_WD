using System.Text.Json;

namespace CsgoWorkshop.Core;

public sealed class Settings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string MapsDir { get; set; } = "";

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.ConfigFile));
                if (loaded != null)
                {
                    loaded.MapsDir ??= "";
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // A broken config file must never stop the program from starting.
        }

        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        var temp = AppPaths.ConfigFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, AppPaths.ConfigFile, overwrite: true);
    }
}
