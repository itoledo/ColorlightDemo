namespace ColorlightDemo;

public sealed class BannerSettings
{
    public string Message { get; set; } = "Hello, LED World!";
    public string NetworkInterface { get; set; } = "Ethernet";
    public int ScreenWidth { get; set; } = 128;
    public int ScreenHeight { get; set; } = 32;
    public byte Brightness { get; set; } = 40;
    public int ScrollSpeedPixelsPerFrame { get; set; } = 3;
    public int TargetFps { get; set; } = 30;
    public float FontSize { get; set; } = 28;
    public string FontColor { get; set; } = "#FF0000";
    public string BackgroundColor { get; set; } = "#000000";
}
