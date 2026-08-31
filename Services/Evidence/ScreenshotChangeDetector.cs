using System.Drawing;
using System.Security.Cryptography;
using System.Text;

namespace Timenotifier.Services.Evidence;

public sealed class ScreenshotChangeDetector
{
    public string CreateFingerprint(Bitmap bitmap)
    {
        using var scaled = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.DrawImage(bitmap, 0, 0, 16, 16);
        }

        var builder = new StringBuilder(16 * 16 * 2);
        for (var y = 0; y < scaled.Height; y++)
        {
            for (var x = 0; x < scaled.Width; x++)
            {
                var pixel = scaled.GetPixel(x, y);
                builder.Append((pixel.R / 32).ToString("x"));
                builder.Append((pixel.G / 32).ToString("x"));
                builder.Append((pixel.B / 32).ToString("x"));
            }
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public bool HasMeaningfulChange(string? previousFingerprint, string currentFingerprint)
    {
        return string.IsNullOrWhiteSpace(previousFingerprint)
            || !string.Equals(previousFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);
    }
}
