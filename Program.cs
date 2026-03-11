using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using ColorlightDemo;

// --- List NICs mode ---
if (args.Contains("--list-nics"))
{
    ColorlightSender.ListInterfaces();
    return;
}

// --- Test pattern mode ---
if (args.Contains("--test"))
{
    var testConfig = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();
    var testSettings = testConfig.GetSection("Banner").Get<BannerSettings>()!;
    int w = testSettings.ScreenWidth, h = testSettings.ScreenHeight;

    using var testSender = new ColorlightSender(testSettings.NetworkInterface);
    testSender.SetBrightness(testSettings.Brightness);

    var cts2 = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts2.Cancel(); };

    int pattern = 0;
    string[] patternNames = ["SOLID RED", "HORIZ STRIPES (4px)", "VERT STRIPES (4px)", "DIAGONAL", "GRADIENT"];
    Console.WriteLine($"Test mode: {w}x{h} - Press ENTER to cycle patterns, Ctrl+C to exit.");

    while (!cts2.Token.IsCancellationRequested)
    {
        var px = new byte[w * h * 3];
        string name = patternNames[pattern % patternNames.Length];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 3;
                bool lit = (pattern % patternNames.Length) switch
                {
                    0 => true,                          // Solid red
                    1 => (y / 4) % 2 == 0,              // Horizontal stripes
                    2 => (x / 4) % 2 == 0,              // Vertical stripes
                    3 => (x + y) % 8 < 4,               // Diagonal
                    _ => false
                };
                if (pattern % patternNames.Length == 4)
                {
                    // Red gradient left-to-right
                    byte v = (byte)(x * 255 / w);
                    px[idx] = 0;       // B
                    px[idx + 1] = 0;   // G
                    px[idx + 2] = v;   // R
                }
                else if (lit)
                {
                    px[idx] = 0;       // B
                    px[idx + 1] = 0;   // G
                    px[idx + 2] = 0xFF;// R
                }
            }
        }

        Console.WriteLine($"  Pattern: {name}");
        // Send pattern continuously until Enter or Ctrl+C
        var sw = Stopwatch.StartNew();
        while (!cts2.Token.IsCancellationRequested && !Console.KeyAvailable)
        {
            testSender.SendFrame(px, w, h, sync: true);
            Thread.Sleep(33);
        }
        if (Console.KeyAvailable) Console.ReadKey(true);
        pattern++;
    }
    return;
}

// --- Load configuration ---
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var settings = config.GetSection("Banner").Get<BannerSettings>()
    ?? throw new InvalidOperationException("Missing 'Banner' section in appsettings.json.");

var fontColor = SKColor.Parse(settings.FontColor);
var bgColor = SKColor.Parse(settings.BackgroundColor);

Console.WriteLine($"Colorlight 5A-75B Scrolling Banner");
Console.WriteLine($"  Message : {settings.Message}");
Console.WriteLine($"  Screen  : {settings.ScreenWidth} x {settings.ScreenHeight} px");
Console.WriteLine($"  NIC     : {settings.NetworkInterface}");
Console.WriteLine($"  Speed   : {settings.ScrollSpeedPixelsPerFrame} px/frame @ {settings.TargetFps} fps");
Console.WriteLine($"  Bright  : {settings.Brightness}");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

// --- Initialize ---
using var sender = new ColorlightSender(settings.NetworkInterface);
using var renderer = new BannerRenderer(
    settings.Message,
    settings.ScreenWidth,
    settings.ScreenHeight,
    settings.FontSize,
    fontColor,
    bgColor);

sender.SetBrightness(settings.Brightness);

// Send one blank frame immediately so the panel shows something
var blankPixels = new byte[settings.ScreenWidth * settings.ScreenHeight * 3];
sender.SendFrame(blankPixels, settings.ScreenWidth, settings.ScreenHeight, sync: true);

// --- Main loop ---
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

int scrollPosition = 0;
int totalScroll = renderer.TotalScrollWidth;
double scrollInterval = 1.0 / settings.TargetFps;
var stopwatch = Stopwatch.StartNew();
double lastScrollTime = 0;
int frameCount = 0;
double lastFpsTime = 0;

// Render initial frame
var pixels = renderer.GetFramePixels(scrollPosition);
// Send twice with sync so both card buffers have the same data
sender.SendFrame(pixels, settings.ScreenWidth, settings.ScreenHeight, sync: true);
sender.SendFrame(pixels, settings.ScreenWidth, settings.ScreenHeight, sync: true);

while (!cts.Token.IsCancellationRequested)
{
    double now = stopwatch.Elapsed.TotalSeconds;

    // Advance scroll at the target rate
    if (now - lastScrollTime >= scrollInterval)
    {
        scrollPosition += settings.ScrollSpeedPixelsPerFrame;
        if (scrollPosition >= totalScroll)
            scrollPosition -= totalScroll;
        pixels = renderer.GetFramePixels(scrollPosition);
        lastScrollTime = now;

        // Send new data twice with sync so BOTH buffers get updated
        sender.SendFrame(pixels, settings.ScreenWidth, settings.ScreenHeight, sync: true);
        sender.SendFrame(pixels, settings.ScreenWidth, settings.ScreenHeight, sync: true);
    }
    else
    {
        // Keep refreshing same data without sync (no buffer swap = no tearing)
        sender.SendFrame(pixels, settings.ScreenWidth, settings.ScreenHeight, sync: false);
        sender.SendFrame(pixels, settings.ScreenWidth, settings.ScreenHeight, sync: false);
    }

    // FPS counter
    frameCount++;
    double fpsElapsed = now - lastFpsTime;
    if (fpsElapsed >= 2.0)
    {
        Console.Write($"\r  Panel refresh: {frameCount / fpsElapsed:F1} fps | Scroll: {settings.TargetFps} fps  ");
        frameCount = 0;
        lastFpsTime = now;
    }
}

Console.WriteLine();
Console.WriteLine("Stopped.");
