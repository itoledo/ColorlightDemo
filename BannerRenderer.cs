using SkiaSharp;

namespace ColorlightDemo;

/// <summary>
/// Renders a scrolling text banner into an RGB pixel buffer suitable
/// for sending to an LED panel via <see cref="ColorlightSender"/>.
/// </summary>
public sealed class BannerRenderer : IDisposable
{
    private readonly SKBitmap _bitmap;
    private readonly int _screenWidth;
    private readonly int _screenHeight;
    private readonly int _textPixelWidth;

    /// <summary>
    /// Total scroll distance in pixels before the cycle repeats.
    /// The text enters from the right and exits completely to the left.
    /// </summary>
    public int TotalScrollWidth => _screenWidth + _textPixelWidth;

    public BannerRenderer(
        string message,
        int screenWidth,
        int screenHeight,
        float fontSize,
        SKColor fontColor,
        SKColor backgroundColor)
    {
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;

        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
        using var font = new SKFont(typeface, fontSize);
        using var paint = new SKPaint { IsAntialias = false, Color = fontColor };

        // Measure the text
        var textBounds = new SKRect();
        font.MeasureText(message, out textBounds, paint);
        _textPixelWidth = (int)Math.Ceiling(textBounds.Width);

        // Create a bitmap wide enough for: blank screen + text + blank screen
        int bitmapWidth = _screenWidth + _textPixelWidth + _screenWidth;
        _bitmap = new SKBitmap(bitmapWidth, screenHeight);

        using var canvas = new SKCanvas(_bitmap);
        canvas.Clear(backgroundColor);

        // Vertically center the text
        float y = (screenHeight + textBounds.Height) / 2f - textBounds.Bottom;

        // Draw text starting at x = screenWidth so it enters from the right
        canvas.DrawText(message, _screenWidth, y, SKTextAlign.Left, font, paint);
    }

    /// <summary>
    /// Extracts an RGB byte array (R,G,B per pixel, row-major) for
    /// the current scroll position.
    /// </summary>
    public byte[] GetFramePixels(int scrollOffset)
    {
        var rgb = new byte[_screenWidth * _screenHeight * 3];

        for (int y = 0; y < _screenHeight; y++)
        {
            for (int x = 0; x < _screenWidth; x++)
            {
                int srcX = x + scrollOffset;
                SKColor color = (srcX >= 0 && srcX < _bitmap.Width)
                    ? _bitmap.GetPixel(srcX, y)
                    : SKColors.Black;

                // Colorlight 5A-75B expects BGR byte order
                int idx = (y * _screenWidth + x) * 3;
                rgb[idx] = color.Blue;
                rgb[idx + 1] = color.Green;
                rgb[idx + 2] = color.Red;
            }
        }

        return rgb;
    }

    public void Dispose()
    {
        _bitmap.Dispose();
    }
}
