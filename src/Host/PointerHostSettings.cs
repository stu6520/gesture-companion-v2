using System.IO;
using System.Text.Json;

namespace GestureCompanionPointerHost;

public sealed class PointerHostSettings
{
    public string PinchOpenShortcut { get; set; } = "PageUp";
    public string PinchCloseShortcut { get; set; } = "PageDown";
    public string RotateClockwiseShortcut { get; set; } = "Shift+PageDown";
    public string RotateCounterClockwiseShortcut { get; set; } = "Shift+PageUp";
    public string PanDragShortcut { get; set; } = "Space";
    public string OneFingerSlideShortcut { get; set; } = string.Empty;
    public bool OneFingerSlideUsesPan { get; set; } = true;
    public string OneFingerHoldShortcut { get; set; } = string.Empty;
    public bool OneFingerHoldUsesRightClick { get; set; } = true;

    public string TwoFingerTapShortcut { get; set; } = "Ctrl+Z";
    public string ThreeFingerTapShortcut { get; set; } = "Ctrl+Y";
    public string FourFingerTapShortcut { get; set; } = "Home";

    public double ZoomStepPixels { get; set; } = 5;
    public double RotateStepDegrees { get; set; } = 2;
    public double PanStepPixels { get; set; } = 5;
    public double ZoomIntervalMilliseconds { get; set; } = 24;
    public double RotateIntervalMilliseconds { get; set; } = 29;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GestureCompanion",
        "pointer-host-settings.json");

    public static PointerHostSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<PointerHostSettings>(File.ReadAllText(SettingsPath)) ?? CreateDefaults();
                settings.Normalize();
                return settings;
            }
        }
        catch
        {
        }
        return CreateDefaults();
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public PointerHostSettings Clone() => new()
    {
        PinchOpenShortcut = PinchOpenShortcut,
        PinchCloseShortcut = PinchCloseShortcut,
        RotateClockwiseShortcut = RotateClockwiseShortcut,
        RotateCounterClockwiseShortcut = RotateCounterClockwiseShortcut,
        PanDragShortcut = PanDragShortcut,
        OneFingerSlideShortcut = OneFingerSlideShortcut,
        OneFingerSlideUsesPan = OneFingerSlideUsesPan,
        OneFingerHoldShortcut = OneFingerHoldShortcut,
        OneFingerHoldUsesRightClick = OneFingerHoldUsesRightClick,

        TwoFingerTapShortcut = TwoFingerTapShortcut,
        ThreeFingerTapShortcut = ThreeFingerTapShortcut,
        FourFingerTapShortcut = FourFingerTapShortcut,
        ZoomStepPixels = ZoomStepPixels,
        RotateStepDegrees = RotateStepDegrees,
        PanStepPixels = PanStepPixels,
        ZoomIntervalMilliseconds = ZoomIntervalMilliseconds,
        RotateIntervalMilliseconds = RotateIntervalMilliseconds

    };

    public static PointerHostSettings CreateDefaults() => new();

    public void Normalize()
    {
        ZoomStepPixels = Math.Clamp(ZoomStepPixels, 1, 60);
        RotateStepDegrees = Math.Clamp(RotateStepDegrees, 1, 15);
        PanStepPixels = Math.Clamp(PanStepPixels, 1, 60);
        ZoomIntervalMilliseconds = Math.Clamp(ZoomIntervalMilliseconds, 0, 100);
        RotateIntervalMilliseconds = Math.Clamp(RotateIntervalMilliseconds, 0, 100);

    }
}