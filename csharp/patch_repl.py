"""Apply targeted string replacements to ReplSession.cs (patches 1-3 only)"""
path = 'd:/projects/claude-code/csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs'

with open(path, 'r', encoding='utf-8') as f:
    src = f.read()

# --- 1. Add _pendingNextPrompt field after _toolUsageSummary ---
old1 = '    private ClaudeCode.Services.ToolUseSummary.ToolUseSummaryService? _toolUsageSummary;\n'
new1 = ('    private ClaudeCode.Services.ToolUseSummary.ToolUseSummaryService? _toolUsageSummary;\n'
        '    private string? _pendingNextPrompt;  // set by /autofix-pr et al. via CommandContext.SetNextPrompt\n')
if old1 in src:
    src = src.replace(old1, new1, 1)
    print("Patch 1 applied.")
else:
    print("Patch 1 already applied or not needed.")

# --- 2. Wire SetNextPrompt in CommandContext construction ---
old2 = ('            Memory           = _sessionMemory,\n'
        '        };')
new2 = ('            Memory           = _sessionMemory,\n'
        '            SetNextPrompt    = prompt => _pendingNextPrompt = prompt,\n'
        '        };')
if old2 in src:
    src = src.replace(old2, new2, 1)
    print("Patch 2 applied.")
else:
    print("Patch 2 already applied or not needed.")

# --- 3. After HandleSlashCommand check, handle _pendingNextPrompt ---
old3 = ('                    if (exitRequested) break;\n'
        '                    continue;')
new3 = ('                    if (exitRequested) break;\n'
        '                    // A command (e.g. /autofix-pr) may have set a follow-up prompt.\n'
        '                    if (_pendingNextPrompt is not null)\n'
        '                    {\n'
        '                        var next = _pendingNextPrompt;\n'
        '                        _pendingNextPrompt = null;\n'
        '                        await SubmitTurnAsync(next, _activeModel, cts.Token).ConfigureAwait(false);\n'
        '                        if (cts.IsCancellationRequested) break;\n'
        '                    }\n'
        '                    continue;')
if old3 in src:
    src = src.replace(old3, new3, 1)
    print("Patch 3 applied.")
else:
    print("Patch 3 already applied or not needed.")

with open(path, 'w', encoding='utf-8', newline='\n') as f:
    f.write(src)

print(f"Done. Total lines: {src.count(chr(10))}")
