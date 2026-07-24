namespace SurvivalcraftGenius.UI;

public static class TextWrapper
{
    /// <summary>
    /// Wraps text to a width measured in half-glyph units: CJK glyphs count as
    /// two, everything else as one (the game font renders CJK roughly
    /// double-width).
    /// </summary>
    public static IEnumerable<string> Wrap(string text, int width)
    {
        text = text.Replace("\r", "").Replace("\n", " ");
        if (text.Length == 0)
        {
            yield return "";
            yield break;
        }

        var line = new System.Text.StringBuilder();
        var lineWeight = 0;
        foreach (var character in text)
        {
            var weight = character > 0x2E80 ? 2 : 1;
            if (lineWeight + weight > width * 2 && line.Length > 0)
            {
                yield return line.ToString();
                line.Clear();
                lineWeight = 0;
            }

            line.Append(character);
            lineWeight += weight;
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }
}
