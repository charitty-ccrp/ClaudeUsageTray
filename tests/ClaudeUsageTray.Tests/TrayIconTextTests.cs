using ClaudeUsageTray.UI;

namespace ClaudeUsageTray.Tests;

public class TrayIconTextTests
{
    [Theory]
    [InlineData(null, "--")]
    [InlineData(0, "0")]
    [InlineData(35, "35")]
    [InlineData(100, "100")]
    public void Text_is_the_plain_number_or_dashes(int? percent, string expected) =>
        Assert.Equal(expected, TrayIconRenderer.TextFor(percent));

    /// <summary>Regression: "100" used to wrap so the last "0" landed on a second line.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void Every_label_fits_on_one_line_and_is_not_wider_than_the_tile(int size)
    {
        foreach (var text in new[] { "--", "0", "7", "35", "99", "100" })
        {
            using var bitmap = new Bitmap(size, size);
            using var g = Graphics.FromImage(bitmap);
            using var font = new Font("Segoe UI", TrayIconRenderer.FontPixels(size, text), FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = TrayIconRenderer.CreateTextFormat();

            // the box is exactly as wide as the tile, so a wrapping layout would report more than one line
            var measured = g.MeasureString(text, font, new SizeF(size, size * 10), format, out _, out var lines);

            Assert.Equal(1, lines);
            Assert.True(measured.Width <= size, $"'{text}' is {measured.Width}px wide on a {size}px tile");
        }
    }

    [Fact]
    public void Three_digit_label_does_not_wrap_with_the_format_the_renderer_uses()
    {
        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI", TrayIconRenderer.FontPixels(16, "100"), FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = TrayIconRenderer.CreateTextFormat();

        // with a box exactly as wide as the tile, a wrapping format would report two lines
        var lines = 0;
        g.MeasureString("100", font, new SizeF(16, 100), format, out _, out lines);

        Assert.Equal(1, lines);
    }
}
