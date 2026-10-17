namespace ClaudeUsageTray.UI;

internal static class WrapText
{
    /// <summary>
    /// WinForms check boxes never word-wrap when AutoSize is on, so long labels run off the window. This turns
    /// AutoSize off and sizes the box to fit its text wrapped at <paramref name="width"/>.
    /// </summary>
    public static T Fit<T>(T box, int width) where T : ButtonBase
    {
        box.AutoSize = false;
        box.TextAlign = ContentAlignment.TopLeft;
        box.CheckAlignIfAny(ContentAlignment.TopLeft);
        var text = TextRenderer.MeasureText(box.Text, box.Font, new Size(width - 24, 0), TextFormatFlags.WordBreak);
        box.Size = new Size(width, text.Height + 6);
        return box;
    }

    private static void CheckAlignIfAny(this ButtonBase box, ContentAlignment align)
    {
        if (box is CheckBox check) check.CheckAlign = align;
        else if (box is RadioButton radio) radio.CheckAlign = align;
    }
}
