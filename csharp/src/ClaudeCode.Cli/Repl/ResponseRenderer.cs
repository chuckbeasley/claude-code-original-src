namespace ClaudeCode.Cli.Repl;

using ClaudeCode.Services.Engine;
using Spectre.Console;
using System.Text;

/// <summary>
/// Renders QueryEngine events to the terminal with streaming markdown formatting.
/// Applies basic markdown-to-ANSI transforms for code blocks, bold, italic,
/// inline code, and headers. Handles streaming deltas by buffering partial spans.
/// </summary>
public sealed class ResponseRenderer
{
    private readonly StreamingMarkdownRenderer _md = new();

    /// <summary>
    /// Handles a single <see cref="QueryEvent"/> and renders it to the terminal.
    /// </summary>
    /// <param name="evt">The event to render. Must not be <see langword="null"/>.</param>
    public void HandleEvent(QueryEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        switch (evt)
        {
            case TextDeltaEvent text:
                foreach (var markup in _md.Feed(text.Text))
                    AnsiConsole.Markup(markup);
                break;

            case ThinkingDeltaEvent thinking:
                if (!string.IsNullOrEmpty(thinking.Text))
                {
                    AnsiConsole.Markup("[dim]");
                    Console.Write(thinking.Text);
                    AnsiConsole.Markup("[/]");
                }
                break;

            case ToolUseStartEvent toolUse:
                foreach (var markup in _md.Finish())
                    AnsiConsole.Markup(markup);
                Console.WriteLine();
                AnsiConsole.MarkupLine($"  [yellow]\u26a1[/] [grey]Tool:[/] [white]{toolUse.Name.EscapeMarkup()}[/]");
                break;

            case ToolResultEvent toolResult:
                var preview = toolResult.Result.Length > 120
                    ? toolResult.Result[..120] + "…"
                    : toolResult.Result;
                var statusColor = toolResult.IsError ? "red" : "grey";
                var statusIcon = toolResult.IsError ? "\u2717" : "\u2713";
                AnsiConsole.MarkupLine($"  [{statusColor}]{statusIcon}[/] [grey]{preview.EscapeMarkup()}[/]");
                break;

            case MessageCompleteEvent:
                foreach (var markup in _md.Finish())
                    AnsiConsole.Markup(markup);
                Console.WriteLine();
                break;

            case ErrorEvent error:
                foreach (var markup in _md.Finish())
                    AnsiConsole.Markup(markup);
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[red]Error:[/] {error.Message.EscapeMarkup()}");
                break;

            case CompactEvent compact:
                AnsiConsole.MarkupLine($"[yellow]\u27f3[/] [grey]{compact.Message.EscapeMarkup()}[/]");
                break;
        }
    }

    /// <summary>
    /// Flushes and resets per-turn renderer state. Call after each complete response
    /// turn, and also on cancellation or error paths where
    /// <see cref="MessageCompleteEvent"/> may not have been emitted.
    /// </summary>
    public void EndTurn()
    {
        foreach (var markup in _md.Finish())
            AnsiConsole.Markup(markup);
    }
}

/// <summary>
/// Stateful per-token markdown renderer. Converts common markdown conventions
/// (headers, bold, italic, inline code, fenced code blocks, bullet lists, links)
/// into Spectre.Console markup strings as tokens stream in.
///
/// Works by buffering within a logical "line" (or code block) and emitting
/// markup-annotated output at natural flush points (newlines, closing fences).
/// </summary>
internal sealed class StreamingMarkdownRenderer
{
    // -----------------------------------------------------------------------
    // State fields
    // -----------------------------------------------------------------------

    private readonly StringBuilder _lineBuf = new();      // Current line accumulator
    private readonly StringBuilder _codeBuf = new();      // Code block body accumulator
    private string? _codeLang;                             // Language of current code fence
    private bool _inCodeFence;                             // Inside a ``` ... ``` block?

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Feed one token (could be multiple characters) from the stream.
    /// Returns zero or more markup strings to write to the console.
    /// Each returned string may be written with AnsiConsole.Markup().
    /// </summary>
    public IEnumerable<string> Feed(string token)
    {
        var output = new List<string>();

        foreach (char ch in token)
        {
            _lineBuf.Append(ch);

            if (ch == '\n')
            {
                // Flush the accumulated line.
                var line = _lineBuf.ToString(); // includes the '\n'
                _lineBuf.Clear();
                output.AddRange(FlushLine(line));
            }
        }

        return output;
    }

    /// <summary>
    /// Flush any buffered content at the end of the stream.
    /// </summary>
    public IEnumerable<string> Finish()
    {
        var output = new List<string>();

        if (_inCodeFence && _codeBuf.Length > 0)
        {
            output.Add(FormatCodeBlock(_codeLang, _codeBuf.ToString()));
            _inCodeFence = false;
            _codeLang = null;
            _codeBuf.Clear();
        }
        else if (_lineBuf.Length > 0)
        {
            output.AddRange(FlushLine(_lineBuf + "\n"));
            _lineBuf.Clear();
        }

        return output;
    }

    // -----------------------------------------------------------------------
    // Private: line-level flushing
    // -----------------------------------------------------------------------

    private IEnumerable<string> FlushLine(string rawLine)
    {
        var line = rawLine.TrimEnd('\n', '\r');

        // --- Code fence detection ---
        if (line.StartsWith("```"))
        {
            if (_inCodeFence)
            {
                // Closing fence: emit the accumulated code block.
                var rendered = FormatCodeBlock(_codeLang, _codeBuf.ToString());
                _inCodeFence = false;
                _codeLang = null;
                _codeBuf.Clear();
                return [rendered + "\n"];
            }
            else
            {
                // Opening fence.
                _inCodeFence = true;
                _codeLang = line.Length > 3 ? line[3..].Trim() : null;
                _codeBuf.Clear();
                return [];
            }
        }

        if (_inCodeFence)
        {
            _codeBuf.AppendLine(line);
            return [];
        }

        // --- Normal markdown line ---
        var markup = RenderLine(line);
        return [markup + "\n"];
    }

    // -----------------------------------------------------------------------
    // Private: inline markdown to Spectre markup
    // -----------------------------------------------------------------------

    private static string RenderLine(string line)
    {
        // ATX headers
        if (line.StartsWith("### "))
            return $"[bold]{EscapeAndInline(line[4..]).EscapeMarkup()}[/]";
        if (line.StartsWith("## "))
            return $"[bold underline]{EscapeAndInline(line[3..]).EscapeMarkup()}[/]";
        if (line.StartsWith("# "))
            return $"[bold underline]{EscapeAndInline(line[2..]).EscapeMarkup()}[/]";

        // Horizontal rules
        if (line == "---" || line == "***" || line == "___")
            return "[grey]─────────────────────────────[/]";

        // Block quotes
        if (line.StartsWith("> "))
            return $"[grey]│ {EscapeAndInline(line[2..])}[/]";

        // Bullet list items
        if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
            return $"  • {RenderInline(line[2..])}";

        // Numbered list items (simple: detect "1. " pattern)
        var numberedMatch = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)\.\s+(.*)$");
        if (numberedMatch.Success)
            return $"  {numberedMatch.Groups[1].Value}. {RenderInline(numberedMatch.Groups[2].Value)}";

        // Empty line
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        // Normal paragraph line — apply inline formatting
        return RenderInline(line);
    }

    /// <summary>
    /// Applies inline markdown transforms (bold, italic, inline code, links)
    /// to a plain text segment. Returns a Spectre.Console markup string.
    /// </summary>
    private static string RenderInline(string text)
    {
        // Process inline elements left-to-right using a simple state machine.
        var sb = new StringBuilder();
        var i = 0;

        while (i < text.Length)
        {
            // Inline code: `...`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    var code = text[(i + 1)..end].EscapeMarkup();
                    sb.Append($"[bold invert] {code} [/]");
                    i = end + 1;
                    continue;
                }
            }

            // Bold: **text** or __text__
            if (i + 1 < text.Length
                && ((text[i] == '*' && text[i + 1] == '*')
                    || (text[i] == '_' && text[i + 1] == '_')))
            {
                var marker = text[i..Math.Min(i + 2, text.Length)];
                var end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    var content = RenderInline(text[(i + 2)..end]);
                    sb.Append($"[bold]{content}[/]");
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *text* or _text_ (single)
            if (text[i] == '*' || text[i] == '_')
            {
                var ch = text[i];
                // Make sure next char is not the same (already handled bold above).
                if (i + 1 < text.Length && text[i + 1] != ch)
                {
                    var end = text.IndexOf(ch, i + 1);
                    if (end > i + 1 && (end + 1 >= text.Length || text[end + 1] != ch))
                    {
                        var content = RenderInline(text[(i + 1)..end]);
                        sb.Append($"[italic]{content}[/]");
                        i = end + 1;
                        continue;
                    }
                }
            }

            // Markdown link: [text](url)
            if (text[i] == '[')
            {
                var closeLabel = text.IndexOf(']', i + 1);
                if (closeLabel > i && closeLabel + 1 < text.Length && text[closeLabel + 1] == '(')
                {
                    var closeUrl = text.IndexOf(')', closeLabel + 2);
                    if (closeUrl > closeLabel)
                    {
                        var linkText = text[(i + 1)..closeLabel].EscapeMarkup();
                        var url = text[(closeLabel + 2)..closeUrl].EscapeMarkup();
                        sb.Append($"[blue underline]{linkText}[/] [grey]({url})[/]");
                        i = closeUrl + 1;
                        continue;
                    }
                }
            }

            // Plain character — escape for Spectre markup.
            sb.Append(text[i].ToString().EscapeMarkup());
            i++;
        }

        return sb.ToString();
    }

    private static string EscapeAndInline(string text) => RenderInline(text);

    /// <summary>
    /// Renders a code block with a language header and monospace styling.
    /// </summary>
    private static string FormatCodeBlock(string? lang, string code)
    {
        var sb = new StringBuilder();
        var langLabel = string.IsNullOrWhiteSpace(lang) ? "code" : lang;
        sb.AppendLine($"[grey on default]  {langLabel.EscapeMarkup()}  [/]");

        // Print each line of the code with a dark background hint.
        foreach (var line in code.TrimEnd().Split('\n'))
        {
            sb.AppendLine($"[grey]{line.EscapeMarkup()}[/]");
        }
        return sb.ToString().TrimEnd('\n', '\r');
    }
}
