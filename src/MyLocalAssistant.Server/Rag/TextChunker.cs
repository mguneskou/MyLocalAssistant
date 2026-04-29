using System.Text;

namespace MyLocalAssistant.Server.Rag;

/// <summary>
/// Splits long text into overlapping, sentence-aligned chunks for embedding.
/// </summary>
public static class TextChunker
{
    public static IEnumerable<string> Chunk(string text, int maxChars = 800, int overlapChars = 100)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
        if (overlapChars < 0 || overlapChars >= maxChars) overlapChars = Math.Min(100, maxChars / 8);

        // Normalize line endings, collapse runs of whitespace within paragraphs but keep paragraph breaks.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var i = 0;
        var n = normalized.Length;
        while (i < n)
        {
            // Skip leading whitespace.
            while (i < n && char.IsWhiteSpace(normalized[i])) i++;
            if (i >= n) yield break;

            var end = Math.Min(i + maxChars, n);
            // Try to break at the last sentence end / newline / space within the window.
            if (end < n)
            {
                var breakAt = LastBreak(normalized, i, end);
                if (breakAt > i + (maxChars / 2)) end = breakAt;
            }

            var sb = new StringBuilder(end - i);
            for (var k = i; k < end; k++)
            {
                var c = normalized[k];
                if (c == '\n')
                {
                    sb.Append('\n');
                    // Collapse runs of \n into max two.
                    while (k + 1 < end && normalized[k + 1] == '\n') { sb.Append('\n'); k++; }
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ' && sb[sb.Length - 1] != '\n')
                        sb.Append(' ');
                }
                else
                {
                    sb.Append(c);
                }
            }
            var chunk = sb.ToString().Trim();
            if (chunk.Length > 0) yield return chunk;

            if (end >= n) yield break;
            // Slide forward with overlap.
            i = Math.Max(end - overlapChars, i + 1);
        }
    }

    private static int LastBreak(string s, int start, int end)
    {
        // Prefer paragraph break, then sentence end, then any whitespace.
        for (var i = end - 1; i > start; i--)
        {
            if (s[i] == '\n' && i > start && s[i - 1] == '\n') return i + 1;
        }
        for (var i = end - 1; i > start; i--)
        {
            var c = s[i];
            if (c == '.' || c == '!' || c == '?' || c == '\n') return i + 1;
        }
        for (var i = end - 1; i > start; i--)
        {
            if (char.IsWhiteSpace(s[i])) return i + 1;
        }
        return end;
    }
}
