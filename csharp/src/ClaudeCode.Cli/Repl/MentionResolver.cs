namespace ClaudeCode.Cli.Repl;

/// <summary>
/// Expands @-mentions in user input before the text is sent to the model.
/// </summary>
/// <remarks>
/// Supported mention forms:
/// <list type="bullet">
///   <item><description><c>@file.txt</c> — reads the file content and injects it as a fenced code block.</description></item>
///   <item><description><c>@url:http://…</c> — placeholder; URL fetch is not yet implemented.</description></item>
///   <item><description><c>@git:branch</c> — runs <c>git diff &lt;branch&gt;</c> and injects the output.</description></item>
///   <item><description><c>@selection</c> — placeholder for IDE selection injection.</description></item>
/// </list>
/// </remarks>
public static class MentionResolver
{
    /// <summary>
    /// Expands @-mentions in user input before sending to the model.
    /// @file.txt → reads file content and injects as a context block
    /// @url:http://... → fetches URL content (not implemented, placeholder)
    /// @git:branch → runs git diff against branch
    /// @selection → placeholder for IDE selection injection
    /// </summary>
    public static async Task<string> ExpandAsync(string input, string cwd, CancellationToken ct)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(
            input,
            @"@([\w./\\-]+|url:[^\s]+|git:[^\s]+|selection)",
            m => ExpandMentionSync(m.Value, cwd));

        // For async file reads, use a two-pass approach
        var mentions = System.Text.RegularExpressions.Regex.Matches(input, @"@([\w./\\-]+)");
        var sb = new System.Text.StringBuilder(input);
        foreach (System.Text.RegularExpressions.Match match in mentions.Cast<System.Text.RegularExpressions.Match>().Reverse())
        {
            var path = match.Groups[1].Value;
            var fullPath = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(cwd, path);
            if (System.IO.File.Exists(fullPath))
            {
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(fullPath, ct);
                    var ext = System.IO.Path.GetExtension(fullPath).TrimStart('.');
                    var block = $"\n\n[File: {path}]\n```{ext}\n{content}\n```\n";
                    sb.Remove(match.Index, match.Length);
                    sb.Insert(match.Index, block);
                }
                catch { /* ignore unreadable files */ }
            }
            else if (path.StartsWith("git:"))
            {
                var branch = path[4..];
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("git", $"diff {branch}")
                    { WorkingDirectory = cwd, RedirectStandardOutput = true, UseShellExecute = false };
                    var proc = System.Diagnostics.Process.Start(psi)!;
                    var diff = await proc.StandardOutput.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);
                    var block = $"\n\n[Git diff vs {branch}]\n```diff\n{diff}\n```\n";
                    sb.Remove(match.Index, match.Length);
                    sb.Insert(match.Index, block);
                }
                catch { }
            }
        }
        return sb.ToString();
    }

    private static string ExpandMentionSync(string mention, string cwd) => mention; // sync pass is a no-op; async pass handles it
}
