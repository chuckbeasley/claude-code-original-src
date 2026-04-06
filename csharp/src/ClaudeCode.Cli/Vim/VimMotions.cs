namespace ClaudeCode.Cli.Vim;

public static class VimMotions
{
    /// <summary>Returns the new cursor position after applying motion to buffer.</summary>
    public static int Apply(string motion, string buffer, int pos, int count = 1)
    {
        var result = pos;
        for (int i = 0; i < count; i++)
            result = motion switch
            {
                "h" => Math.Max(0, result - 1),
                "l" => Math.Min(buffer.Length, result + 1),
                "0" => 0,
                "$" => buffer.Length,
                "^" => FirstNonWhitespace(buffer),
                "w" => WordForward(buffer, result),
                "b" => WordBackward(buffer, result),
                "e" => WordEnd(buffer, result),
                "W" => WORDForward(buffer, result),
                "B" => WORDBackward(buffer, result),
                "E" => WORDEnd(buffer, result),
                _ => result
            };
        return result;
    }

    private static int FirstNonWhitespace(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static int WordForward(string s, int pos)
    {
        var i = pos;
        if (i < s.Length && IsWordChar(s[i]))
            while (i < s.Length && IsWordChar(s[i])) i++;
        else if (i < s.Length && !char.IsWhiteSpace(s[i]))
            while (i < s.Length && !IsWordChar(s[i]) && !char.IsWhiteSpace(s[i])) i++;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return Math.Min(i, s.Length);
    }

    private static int WordBackward(string s, int pos)
    {
        var i = pos;
        while (i > 0 && char.IsWhiteSpace(s[i - 1])) i--;
        if (i > 0 && IsWordChar(s[i - 1]))
            while (i > 0 && IsWordChar(s[i - 1])) i--;
        else
            while (i > 0 && !IsWordChar(s[i - 1]) && !char.IsWhiteSpace(s[i - 1])) i--;
        return i;
    }

    private static int WordEnd(string s, int pos)
    {
        var i = pos;
        if (i < s.Length) i++;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i < s.Length && IsWordChar(s[i]))
            while (i < s.Length && IsWordChar(s[i])) i++;
        else
            while (i < s.Length && !IsWordChar(s[i]) && !char.IsWhiteSpace(s[i])) i++;
        return Math.Max(0, Math.Min(i - 1, s.Length - 1));
    }

    private static int WORDForward(string s, int pos)
    {
        var i = pos;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static int WORDBackward(string s, int pos)
    {
        var i = pos;
        while (i > 0 && char.IsWhiteSpace(s[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(s[i - 1])) i--;
        return i;
    }

    private static int WORDEnd(string s, int pos)
    {
        var i = pos + 1;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return Math.Max(0, i - 1);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
