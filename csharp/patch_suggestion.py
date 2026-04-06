"""Wire PromptSuggestionService into ReplSession.cs"""
path = 'd:/projects/claude-code/csharp/src/ClaudeCode.Cli/Repl/ReplSession.cs'

with open(path, 'r', encoding='utf-8') as f:
    src = f.read()

# --- 1. Add fields ---
old1 = '    private string? _pendingNextPrompt;  // set by /autofix-pr et al. via CommandContext.SetNextPrompt\n'
new1 = ('    private string? _pendingNextPrompt;  // set by /autofix-pr et al. via CommandContext.SetNextPrompt\n'
        '    private string? _promptSuggestion;   // ghost-text hint for the next REPL input\n'
        '    private ClaudeCode.Services.PromptSuggestion.PromptSuggestionService? _promptSuggestionSvc;\n')
if old1 in src:
    src = src.replace(old1, new1, 1)
    print("Patch 1 (fields) applied.")
else:
    print("Patch 1 already applied or target missing.")

# --- 2. Fire suggestion generation after each turn ---
old2 = ('            // Fire-and-forget memory extraction — runs every N turns in the background.\n'
        '            _ = _memoryExtractorService?.MaybeExtractAsync(_engine!.Messages, CancellationToken.None);')
new2 = ('            // Fire-and-forget memory extraction — runs every N turns in the background.\n'
        '            _ = _memoryExtractorService?.MaybeExtractAsync(_engine!.Messages, CancellationToken.None);\n\n'
        '            // Fire-and-forget prompt suggestion — generates ghost text for the next prompt.\n'
        '            if (_promptSuggestionSvc is not null && _engine?.Messages is { Count: > 0 } msgs)\n'
        '            {\n'
        '                _ = Task.Run(async () =>\n'
        '                {\n'
        '                    using var suggestionCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));\n'
        '                    _promptSuggestion = await _promptSuggestionSvc\n'
        '                        .GenerateAsync(msgs, suggestionCts.Token)\n'
        '                        .ConfigureAwait(false);\n'
        '                }, CancellationToken.None);\n'
        '            }')
if old2 in src:
    src = src.replace(old2, new2, 1)
    print("Patch 2 (suggestion fire) applied.")
else:
    print("Patch 2 already applied or target missing.")

# --- 3. Initialise _promptSuggestionSvc after engine is ready ---
old3 = '        var inputHistory = new InputHistory();\n'
new3 = ('        var inputHistory = new InputHistory();\n'
        '        // Initialise prompt suggestion service when the feature is enabled.\n'
        '        if (ClaudeCode.Services.PromptSuggestion.PromptSuggestionService.IsEnabled())\n'
        '            _promptSuggestionSvc = new ClaudeCode.Services.PromptSuggestion.PromptSuggestionService(\n'
        '                _client, _activeModel);\n\n')
if old3 in src:
    src = src.replace(old3, new3, 1)
    print("Patch 3 (init service) applied.")
else:
    print("Patch 3 already applied or target missing.")

# --- 4. Show hint before prompt ---
old4 = ('        // Write initial prompt with dynamic mode indicator when vim is active.\n'
        '        if (vimMode)\n')
new4 = ('        // Show any pending prompt suggestion as a dim hint above the prompt.\n'
        '        var suggestion = _promptSuggestion;\n'
        '        _promptSuggestion = null;\n'
        '        if (!string.IsNullOrWhiteSpace(suggestion))\n'
        '        {\n'
        '            Console.WriteLine();\n'
        '            Console.Write($"\\x1b[2m[Tab] {suggestion}\\x1b[0m");\n'
        '        }\n\n'
        '        // Write initial prompt with dynamic mode indicator when vim is active.\n'
        '        if (vimMode)\n')
if old4 in src:
    src = src.replace(old4, new4, 1)
    print("Patch 4 (hint display) applied.")
else:
    print("Patch 4 already applied or target missing.")

# --- 5a. Accept suggestion on Tab in VIM mode handler (20-space indent) ---
old5a = ('                    if (key.Key == ConsoleKey.Tab)\n'
         '                    {\n'
         '                        var partialInput = buffer.ToString().TrimStart();\n'
         '                        if (partialInput.StartsWith(\'/\') && _commandRegistry is not null)\n')
new5a = ('                    if (key.Key == ConsoleKey.Tab)\n'
         '                    {\n'
         '                        var partialInput = buffer.ToString().TrimStart();\n'
         '                        // Accept prompt suggestion when Tab is pressed on an empty buffer.\n'
         '                        if (partialInput.Length == 0 && !string.IsNullOrWhiteSpace(suggestion))\n'
         '                        {\n'
         '                            buffer.Clear();\n'
         '                            buffer.Append(suggestion);\n'
         '                            cursorPos = buffer.Length;\n'
         '                            suggestion = null;\n'
         '                            RedrawCurrentLine();\n'
         '                            continue;\n'
         '                        }\n'
         '                        if (partialInput.StartsWith(\'/\') && _commandRegistry is not null)\n')
if old5a in src:
    src = src.replace(old5a, new5a, 1)
    print("Patch 5a (vim Tab suggestion) applied.")
else:
    print("Patch 5a already applied or not found.")

# --- 5b. Accept suggestion on Tab in regular (non-vim) handler (12-space indent) ---
old5b = ('            if (key.Key == ConsoleKey.Tab)\n'
         '            {\n'
         '                var partialInput = buffer.ToString().TrimStart();\n'
         '                if (partialInput.StartsWith(\'/\') && _commandRegistry is not null)\n')
new5b = ('            if (key.Key == ConsoleKey.Tab)\n'
         '            {\n'
         '                var partialInput = buffer.ToString().TrimStart();\n'
         '                // Accept prompt suggestion when Tab is pressed on an empty buffer.\n'
         '                if (partialInput.Length == 0 && !string.IsNullOrWhiteSpace(suggestion))\n'
         '                {\n'
         '                    buffer.Clear();\n'
         '                    buffer.Append(suggestion);\n'
         '                    cursorPos = buffer.Length;\n'
         '                    suggestion = null;\n'
         '                    RedrawCurrentLine();\n'
         '                    continue;\n'
         '                }\n'
         '                if (partialInput.StartsWith(\'/\') && _commandRegistry is not null)\n')
if old5b in src:
    src = src.replace(old5b, new5b, 1)
    print("Patch 5b (non-vim Tab suggestion) applied.")
else:
    print("Patch 5b already applied or not found.")

with open(path, 'w', encoding='utf-8', newline='\n') as f:
    f.write(src)

print(f"Done. Total lines: {src.count(chr(10))}")
