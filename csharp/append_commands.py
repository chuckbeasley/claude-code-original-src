import io

new_content = r"""
/// <summary>
/// Guides the user through connecting their GitHub account to Claude Code on the web
/// (cloud remote sessions). Collects a GitHub Personal Access Token, validates it via
/// the CCR backend, and optionally opens the browser to claude.ai/code.
/// </summary>
public sealed class RemoteSetupCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/remote-setup";

    /// <inheritdoc/>
    public override string Description => "Connect your GitHub account to Claude Code (web/cloud setup)";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        AnsiConsole.Write(new Rule("[blue]Claude Code \u2014 Remote / Web Setup[/]").RuleStyle("grey"));
        ctx.Write(string.Empty);

        ctx.WriteMarkup("[grey]This wizard connects your GitHub account so Claude Code can run in the cloud.[/]");
        ctx.WriteMarkup("[grey]You need a GitHub Personal Access Token (classic) with the [white]repo[/] scope.[/]");
        ctx.Write(string.Empty);

        var accessToken = Environment.GetEnvironmentVariable("CLAUDE_CLAUDE_AI_ACCESS_TOKEN")
                       ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            ctx.WriteMarkup("[yellow]You are not signed in to Claude.[/]");
            ctx.WriteMarkup("[grey]Run [blue]/login[/] first, then re-run /remote-setup.[/]");
            return true;
        }

        ctx.WriteMarkup("[grey](Create a PAT at [blue]https://github.com/settings/tokens[/])[/]");
        ctx.Write(string.Empty);

        var pat = AnsiConsole.Prompt(
            new TextPrompt<string>("[grey]GitHub PAT:[/]")
                .PromptStyle("white")
                .Secret('*'));

        if (string.IsNullOrWhiteSpace(pat))
        {
            ctx.WriteMarkup("[yellow]No token entered \u2014 setup cancelled.[/]");
            return true;
        }

        try
        {
            await AnsiConsole.Status()
                .StartAsync("Validating GitHub token\u2026", async _ =>
                    await ImportGithubTokenAsync(accessToken, pat, ct).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            ctx.WriteMarkup("[red]Authentication failed.[/] [grey]Run /login again.[/]");
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            ctx.WriteMarkup("[red]Invalid GitHub token.[/] [grey]Ensure it has the [white]repo[/] scope.[/]");
            return true;
        }
        catch (OperationCanceledException) { ctx.WriteMarkup("[yellow]Request cancelled.[/]"); return true; }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[red]Backend unreachable:[/] {ex.Message.EscapeMarkup()}");
            return true;
        }

        ctx.WriteMarkup("[green]GitHub account connected successfully.[/]");
        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Open [blue]https://claude.ai/code[/] to start a remote session.[/]");

        if (AnsiConsole.Confirm("[grey]Open the browser now?[/]", defaultValue: true))
        {
            try { Process.Start(new ProcessStartInfo("https://claude.ai/code") { UseShellExecute = true }); }
            catch { ctx.WriteMarkup("[grey]Navigate to [blue]https://claude.ai/code[/] manually.[/]"); }
        }

        return true;
    }

    private static async Task ImportGithubTokenAsync(string accessToken, string githubPat, CancellationToken ct)
    {
        const string ImportUrl = "https://api.anthropic.com/v1/code/github/import-token";
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");
        http.DefaultRequestHeaders.Add("anthropic-beta", "ccr-byoc-2025-07-29");
        var body = JsonSerializer.Serialize(new { token = githubPat });
        using var content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json");
        (await http.PostAsync(ImportUrl, content, ct).ConfigureAwait(false)).EnsureSuccessStatusCode();
    }
}

/// <summary>
/// Examines failing CI checks on the current pull request via <c>gh pr checks</c>
/// and emits a structured prompt for Claude to fix each failure.
/// </summary>
public sealed class AutofixPrCommand : SlashCommand
{
    /// <inheritdoc/>
    public override string Name => "/autofix-pr";

    /// <inheritdoc/>
    public override string Description => "Analyze failing PR checks and generate a Claude fix prompt";

    /// <inheritdoc/>
    public override async Task<bool> ExecuteAsync(CommandContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        AnsiConsole.Write(new Rule("[blue]Auto-Fix PR Checks[/]").RuleStyle("grey"));
        ctx.Write(string.Empty);

        if (!IsToolOnPath("gh"))
        {
            ctx.WriteMarkup("[yellow]GitHub CLI ([blue]gh[/]) is not installed or not on PATH.[/]");
            ctx.WriteMarkup("[grey]Install from [blue]https://cli.github.com[/] and run [blue]gh auth login[/].[/]");
            return true;
        }

        string prNumber;
        try
        {
            prNumber = (await RunGhAsync(
                ["pr", "view", "--json", "number", "-q", ".number"], ctx.Cwd, ct)
                .ConfigureAwait(false)).Trim();
        }
        catch
        {
            ctx.WriteMarkup("[yellow]No pull request found for the current branch.[/]");
            ctx.WriteMarkup("[grey]Create one with [blue]gh pr create[/] first.[/]");
            return true;
        }

        if (string.IsNullOrWhiteSpace(prNumber))
        {
            ctx.WriteMarkup("[yellow]Could not detect the current PR number.[/]");
            return true;
        }

        ctx.WriteMarkup($"[grey]Fetching checks for PR [white]#{prNumber.EscapeMarkup()}[/]\u2026[/]");

        string checksJson;
        try
        {
            checksJson = await RunGhAsync(
                ["pr", "checks", prNumber, "--json", "name,state,conclusion,detailsUrl,description"],
                ctx.Cwd, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.WriteMarkup($"[red]Failed to fetch PR checks:[/] {ex.Message.EscapeMarkup()}");
            return true;
        }

        List<PrCheck> allChecks;
        try
        {
            allChecks = JsonSerializer.Deserialize<List<PrCheck>>(checksJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            ctx.WriteMarkup("[red]Could not parse check results.[/]");
            return true;
        }

        var failing = allChecks
            .Where(c => c.Conclusion is "failure" or "timed_out" or "cancelled"
                     || c.State    is "FAILURE"   or "ERROR")
            .ToList();

        if (failing.Count == 0)
        {
            ctx.WriteMarkup("[green]All checks are passing![/] [grey]Nothing to fix.[/]");
            return true;
        }

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Red);
        table.AddColumn("Check");
        table.AddColumn("Status");
        table.AddColumn("Details");

        foreach (var check in failing)
        {
            var status = (check.Conclusion ?? check.State ?? "failed").EscapeMarkup();
            var detail = string.IsNullOrWhiteSpace(check.Description)
                ? "-" : check.Description.EscapeMarkup();
            table.AddRow(check.Name.EscapeMarkup(), $"[red]{status}[/]", detail);
        }

        AnsiConsole.Write(table);
        ctx.Write(string.Empty);

        var failureList = string.Join(", ", failing.Select(f => f.Name));
        var sb = new StringBuilder();
        sb.AppendLine($"The following CI checks are failing on PR #{prNumber}: {failureList}.");
        sb.AppendLine();
        sb.AppendLine("Please:");
        sb.AppendLine("1. Investigate the root cause of each failure");
        sb.AppendLine("2. Fix the code so all failing checks pass");
        sb.AppendLine("3. Do NOT change unrelated code");
        sb.AppendLine("4. Run any relevant tests locally to verify the fix");

        foreach (var check in failing.Take(3).Where(c => !string.IsNullOrWhiteSpace(c.DetailsUrl)))
        {
            sb.AppendLine();
            sb.AppendLine($"Check '{check.Name}' details: {check.DetailsUrl}");
        }

        var prompt = sb.ToString().TrimEnd();

        ctx.Write(string.Empty);
        ctx.WriteMarkup("[grey]Generated fix prompt:[/]");
        ctx.Write(string.Empty);
        AnsiConsole.Write(new Panel(prompt).Header("[grey]Fix Prompt[/]").BorderColor(Color.Grey));

        if (ctx.SetNextPrompt is not null)
            ctx.SetNextPrompt(prompt);

        return true;
    }

    private static bool IsToolOnPath(string tool)
    {
        try
        {
            var which = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
            using var proc = Process.Start(new ProcessStartInfo(which, tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            proc?.WaitForExit(500);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<string> RunGhAsync(string[] args, string cwd, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("gh")
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"gh exited {proc.ExitCode}: {err.Trim()}");
        }
        return stdout;
    }

    private sealed record PrCheck(
        string Name,
        string? State,
        string? Conclusion,
        string? DetailsUrl,
        string? Description);
}
"""

path = 'd:/projects/claude-code/csharp/src/ClaudeCode.Commands/BuiltInCommands.cs'
with open(path, 'a', encoding='utf-8', newline='\n') as f:
    f.write(new_content)

lines = sum(1 for _ in open(path, encoding='utf-8'))
print(f"Done. Total lines: {lines}")
