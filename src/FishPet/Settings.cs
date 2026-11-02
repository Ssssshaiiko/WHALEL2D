using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FishPet;

// What is kept between starts, in settings.json beside the plugin. Only the agent changes it (the tools
// in Control.cs); there is no settings window.
internal sealed class Settings
{
    public string Shape { get; set; } = "small";
    public string[] Outfit { get; set; } = [];
    public int Fps { get; set; } = 60;
    public double Scale { get; set; } = 1;
    public bool FollowMouse { get; set; } = true;
    // Seconds between the small things she does by herself when nobody talks to her.
    public int IdleSeconds { get; set; } = 40;
    public double? Left { get; set; }
    public double? Top { get; set; }

    internal static readonly int[] FpsChoices = [30, 60, 90, 120];
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    internal static Settings Load()
    {
        try
        {
            if (File.Exists(Places.SettingsFile) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(Places.SettingsFile)) is { } loaded)
                return loaded.Checked();
        }
        catch (Exception error) { Places.Log("设置读不出来，用默认的：" + error.Message); }
        return new Settings();
    }

    internal Settings Checked()
    {
        Shape = FishPet.Shape.Find(Shape).Id;
        Outfit = (Outfit ?? []).Where(item => FishPet.Outfit.Expression(item) != null).Select(item => FishPet.Outfit.Expression(item)!).Distinct().ToArray();
        if (!FpsChoices.Contains(Fps)) Fps = 60;
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1, .5, 2);
        IdleSeconds = Math.Clamp(IdleSeconds, 15, 600);
        return this;
    }

    internal void Save()
    {
        try { File.WriteAllText(Places.SettingsFile, JsonSerializer.Serialize(this, Format)); }
        catch (Exception error) { Places.Log("设置存不下来：" + error.Message); }
    }
}
