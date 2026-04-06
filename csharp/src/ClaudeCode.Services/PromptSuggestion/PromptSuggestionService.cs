namespace ClaudeCode.Services.PromptSuggestion;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeCode.Services.Api;

/// <summary>
/// After each completed assistant turn, generates a short "ghost text" suggestion predicting
/// what the user will type next. The suggestion is displayed dimly in the REPL input line and
/// can be accepted with Tab.
///
/// Port of <c>src/services/PromptSuggestion/promptSuggestion.ts</c>.
/// </summary>
public sealed class PromptSuggestionService
{
    private const string SuggestionPrompt =
        "[SUGGESTION MODE: Suggest what the user might naturally type next into Claude Code.]\n\n" +
        "FIRST: Look at the user's recent messages and original request.\n\n" +
        "Your job is to predict what THEY would type – not what you think they should do.\n\n" +
        "THE TEST: Would they think \"I was just about to type that\"?\n\n" +
        "EXAMPLES:\n" +
        "User asked \"fix the bug and run tests\", bug is fixed → \"run the tests\"\n" +
        "After code written → \"try it out\"\n" +
        "Claude offers options → suggest the one the user would likely pick\n" +
        "Claude asks to continue → \"yes\" or \"go ahead\"\n" +
        "Task complete, obvious follow-up → \"commit this\" or \"push it\"\n" +
        "After error or misunderstanding → silence (let them assess/correct)\n\n" +
        "Be specific: \"run the tests\" beats \"continue\".\n\n" +
        "NEVER SUGGEST:\n" +
        "- Evaluative (\"looks good\", \"thanks\")\n" +
        "- Questions (\"what about...?\")\n" +
        "- Claude-voice (\"Let me...\", \"I'll...\", \"Here's...\")\n" +
        "- New ideas they didn't ask about\n" +
        "- Multiple sentences\n\n" +
        "Stay silent if the next step isn't obvious from what the user said.\n\n" +
        "Format: 2-12 words, match the user's style. Or nothing.\n\n" +
        "Reply with ONLY the suggestion, no quotes or explanation.";

    // Words that are valid single-word suggestions.
    private static readonly HashSet<string> AllowedSingleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yeah", "yep", "yea", "yup", "sure", "ok", "okay",
        "push", "commit", "deploy", "stop", "continue", "check", "exit", "quit", "no",
    };

    private static readonly Regex EvaluativePattern = new(
        @"thanks|thank you|looks good|sounds good|that works|that worked|that's all|nice|great|perfect|makes sense|awesome|excellent",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ClaudeVoicePattern = new(
        @"^(let me|i'll|i've|i'm|i can|i would|i think|i notice|here's|here is|here are|that's|this is|this will|you can|you should|you could|sure,|of course|certainly)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MultipleSentencesPattern = new(
        @"[.!?]\s+[A-Z]", RegexOptions.Compiled);

    private readonly IAnthropicClient _client;
    private readonly string _model;

    /// <summary>
    /// Constructs a new <see cref="PromptSuggestionService"/>.
    /// </summary>
    /// <param name="client">The Anthropic API client to use for suggestion generation.</param>
    /// <param name="model">The model to use for suggestion generation (same as the active session model).</param>
    public PromptSuggestionService(IAnthropicClient client, string model)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>Returns <see langword="true"/> when prompt suggestion is enabled.</summary>
    public static bool IsEnabled()
    {
        var envVal = Environment.GetEnvironmentVariable("CLAUDE_CODE_ENABLE_PROMPT_SUGGESTION");
        if (!string.IsNullOrEmpty(envVal))
        {
            // "0", "false", "no" → disabled; anything else truthy → enabled
            return envVal is not ("0" or "false" or "no" or "off");
        }
        // Default: disabled (feature flag; can be toggled via settings later)
        return false;
    }

    /// <summary>
    /// Generates a suggestion for what the user might type next.
    /// Returns <see langword="null"/> when suppressed, filtered, or when the API call fails.
    /// </summary>
    /// <param name="conversationMessages">Current conversation history (user+assistant turns).</param>
    /// <param name="ct">Cancellation token. The caller should cancel this quickly (e.g. 5s timeout) to avoid blocking the REPL.</param>
    public async Task<string?> GenerateAsync(
        IReadOnlyList<MessageParam> conversationMessages,
        CancellationToken ct = default)
    {
        if (!IsEnabled()) return null;
        if (conversationMessages.Count < 4) return null;  // need at least 2 full turns

        // Count assistant turns.
        int assistantTurns = conversationMessages.Count(m => m.Role == "assistant");
        if (assistantTurns < 2) return null;

        try
        {
            // Build conversation + suggestion prompt as the final user turn.
            var messages = new List<MessageParam>(conversationMessages)
            {
                MakeUserMessage(SuggestionPrompt),
            };

            var request = new MessageRequest
            {
                Model      = _model,
                Messages   = messages,
                MaxTokens  = 64,
                Temperature = 0.3,
                Stream     = true,
            };

            var sb = new StringBuilder();
            await foreach (var evt in _client.StreamMessageAsync(request, ct).ConfigureAwait(false))
            {
                if (evt.EventType == "content_block_delta" && evt.Data is not null)
                {
                    try
                    {
                        var doc = JsonDocument.Parse(evt.Data);
                        if (doc.RootElement.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("text", out var text))
                        {
                            sb.Append(text.GetString());
                        }
                    }
                    catch { /* ignore parse errors */ }
                }
            }

            var suggestion = sb.ToString().Trim();
            return ShouldFilter(suggestion) ? null : suggestion;
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // Filter logic (port of shouldFilterSuggestion)
    // -----------------------------------------------------------------------

    internal static bool ShouldFilter(string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion)) return true;

        var lower = suggestion.Trim().ToLowerInvariant();
        var wordCount = suggestion.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;

        // Block meta-text: "nothing found", "silence", explicit refusals.
        if (lower is "done" or "nothing found." or "nothing found") return true;
        if (lower.StartsWith("nothing to suggest", StringComparison.Ordinal) ||
            lower.StartsWith("no suggestion", StringComparison.Ordinal))
            return true;
        if (Regex.IsMatch(lower, @"\bsilence is\b|\bstay(s|ing)? silent\b") ||
            Regex.IsMatch(lower, @"^\W*silence\W*$"))
            return true;
        // Parenthesised meta-reasoning: (silence — ...) or [no suggestion]
        if (Regex.IsMatch(suggestion.Trim(), @"^\(.*\)$|^\[.*\]$")) return true;
        // Error-like prefixes
        if (Regex.IsMatch(lower, @"^(api error:|prompt is too long|request timed out|invalid api key|image was too large)"))
            return true;
        // Prefixed labels ("Suggestion: ...")
        if (Regex.IsMatch(suggestion, @"^\w+:\s")) return true;
        // Word count guards
        if (wordCount < 2)
        {
            if (!suggestion.StartsWith('/') && !AllowedSingleWords.Contains(lower)) return true;
        }
        if (wordCount > 12) return true;
        if (suggestion.Length >= 100) return true;
        // Multiple sentences
        if (MultipleSentencesPattern.IsMatch(suggestion)) return true;
        // Markdown formatting
        if (suggestion.Contains('\n') || suggestion.Contains('*')) return true;
        // Evaluative language
        if (EvaluativePattern.IsMatch(lower)) return true;
        // Claude-voice
        if (ClaudeVoicePattern.IsMatch(suggestion)) return true;

        return false;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static MessageParam MakeUserMessage(string text)
    {
        var contentJson = JsonSerializer.Serialize(text);
        var element     = JsonDocument.Parse(contentJson).RootElement.Clone();
        return new MessageParam { Role = "user", Content = element };
    }
}
