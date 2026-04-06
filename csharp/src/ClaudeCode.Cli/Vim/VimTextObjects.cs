namespace ClaudeCode.Cli.Vim;

public static class VimTextObjects
{
    /// <summary>Returns (start, end) for text object. end is exclusive.</summary>
    public static (int start, int end) Resolve(string obj, string buffer, int pos)
    {
        return obj switch
        {
            "iw" => InnerWord(buffer, pos),
            "aw" => AWord(buffer, pos),
            "i\"" => Inner(buffer, pos, '"'),
            "a\"" => Around(buffer, pos, '"'),
            "i'" => Inner(buffer, pos, '\''),
            "a'" => Around(buffer, pos, '\''),
            "i(" or "ib" => Inner(buffer, pos, '(', ')'),
            "a(" or "ab" => Around(buffer, pos, '(', ')'),
            "i[" => Inner(buffer, pos, '[', ']'),
            "a[" => Around(buffer, pos, '[', ']'),
            "i{" or "iB" => Inner(buffer, pos, '{', '}'),
            "a{" or "aB" => Around(buffer, pos, '{', '}'),
            _ => (pos, pos)
        };
    }

    private static (int, int) InnerWord(string s, int pos)
    {
        var start = pos;
        var end = pos;
        bool isWord = pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_');
        while (start > 0 && InClass(s[start - 1], isWord)) start--;
        while (end < s.Length && InClass(s[end], isWord)) end++;
        return (start, end);
    }

    private static (int, int) AWord(string s, int pos)
    {
        var (start, end) = InnerWord(s, pos);
        while (end < s.Length && char.IsWhiteSpace(s[end])) end++;
        return (start, end);
    }

    private static (int, int) Inner(string s, int pos, char open, char close = '\0')
    {
        if (close == '\0') close = open;
        var i = pos;
        int openIdx = -1, closeIdx = -1;
        if (open == close)
        {
            // Find enclosing pair by scanning left then right
            for (int j = pos - 1; j >= 0; j--) if (s[j] == open) { openIdx = j; break; }
            for (int j = openIdx + 1; j < s.Length; j++) if (s[j] == close) { closeIdx = j; break; }
        }
        else
        {
            int depth = 0;
            for (int j = pos; j >= 0; j--) { if (s[j] == close) depth++; if (s[j] == open) { if (depth == 0) { openIdx = j; break; } depth--; } }
            depth = 0;
            for (int j = pos; j < s.Length; j++) { if (s[j] == open) depth++; if (s[j] == close) { if (depth == 0) { closeIdx = j; break; } depth--; } }
        }
        if (openIdx < 0 || closeIdx < 0) return (pos, pos);
        return (openIdx + 1, closeIdx);
    }

    private static (int, int) Around(string s, int pos, char open, char close = '\0')
    {
        if (close == '\0') close = open;
        var (start, end) = Inner(s, pos, open, close);
        return (Math.Max(0, start - 1), Math.Min(s.Length, end + 1));
    }

    private static bool InClass(char c, bool wordClass) =>
        wordClass ? (char.IsLetterOrDigit(c) || c == '_') : (!char.IsLetterOrDigit(c) && c != '_' && !char.IsWhiteSpace(c));
}
