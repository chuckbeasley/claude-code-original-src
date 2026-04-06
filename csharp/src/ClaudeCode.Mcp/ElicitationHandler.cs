namespace ClaudeCode.Mcp;

using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

/// <summary>
/// Handles MCP <c>elicitation/create</c> requests — interactive prompts from MCP servers
/// that ask for user input mid-tool-execution.
/// </summary>
/// <remarks>
/// <para>
/// The MCP elicitation protocol allows a server to pause and request structured input
/// from the user before continuing. This handler interprets the <c>requestedSchema</c>
/// field to select the most appropriate interactive prompt style:
/// <list type="bullet">
///   <item><description>Boolean properties use a yes/no confirm prompt.</description></item>
///   <item><description>Properties with an <c>enum</c> array use a selection prompt.</description></item>
///   <item><description>All other properties use a free-text ask prompt.</description></item>
/// </list>
/// </para>
/// <para>
/// Returns a JSON object shaped as <c>{ "action": "accept", "content": { ... } }</c>.
/// </para>
/// </remarks>
public sealed class ElicitationHandler
{
    /// <summary>
    /// Processes an elicitation request by presenting the server's prompt to the user
    /// interactively and returning their response as a JSON element.
    /// </summary>
    /// <param name="request">
    /// The full elicitation request JSON element. Expected to contain optional
    /// <c>"message"</c> and <c>"requestedSchema"</c> fields.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="JsonElement"/> shaped as
    /// <c>{ "action": "accept", "content": { ... } }</c> where <c>content</c>
    /// holds the user-supplied values keyed by schema property name.
    /// </returns>
    public async Task<JsonElement> HandleAsync(JsonElement request, CancellationToken ct)
    {
        // Extract display message and schema from the request.
        var message = request.TryGetProperty("message", out var msgEl)
            ? msgEl.GetString() ?? "The MCP server needs information:"
            : "The MCP server needs information:";

        JsonElement? schema = request.TryGetProperty("requestedSchema", out var schemaEl)
            ? schemaEl
            : null;

        Spectre.Console.AnsiConsole.MarkupLine(
            $"[yellow]MCP server request:[/] {Spectre.Console.Markup.Escape(message)}");

        var content = new JsonObject();

        if (schema.HasValue
            && schema.Value.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object)
        {
            // Schema-driven: prompt each property individually.
            foreach (var prop in props.EnumerateObject())
            {
                ct.ThrowIfCancellationRequested();

                var propType = prop.Value.TryGetProperty("type", out var typeEl)
                    ? typeEl.GetString() ?? "string"
                    : "string";

                var propDesc = prop.Value.TryGetProperty("description", out var descEl)
                    ? descEl.GetString() ?? prop.Name
                    : prop.Name;

                if (propType == "boolean")
                {
                    var val = Spectre.Console.AnsiConsole.Confirm($"{propDesc}?");
                    content[prop.Name] = val;
                }
                else if (prop.Value.TryGetProperty("enum", out var enumEl)
                    && enumEl.ValueKind == JsonValueKind.Array)
                {
                    // Build the option list from the enum array.
                    var options = new List<string>();
                    foreach (var opt in enumEl.EnumerateArray())
                    {
                        var optStr = opt.GetString();
                        if (optStr is not null)
                            options.Add(optStr);
                    }

                    if (options.Count > 0)
                    {
                        var selected = AnsiConsole.Prompt(
                            new SelectionPrompt<string>()
                                .Title(propDesc)
                                .AddChoices(options));
                        content[prop.Name] = selected;
                    }
                    else
                    {
                        var val = Spectre.Console.AnsiConsole.Ask<string>($"{propDesc}:");
                        content[prop.Name] = val;
                    }
                }
                else
                {
                    var val = Spectre.Console.AnsiConsole.Ask<string>($"{propDesc}:");
                    content[prop.Name] = val;
                }
            }
        }
        else
        {
            // No schema — collect a single free-text response.
            var response = Spectre.Console.AnsiConsole.Ask<string>("Response:");
            content["response"] = response;
        }

        // Satisfy async signature without actual I/O (all prompts above are synchronous).
        await Task.CompletedTask.ConfigureAwait(false);

        var result = new JsonObject
        {
            ["action"] = "accept",
            ["content"] = content,
        };

        return JsonSerializer.SerializeToElement(result);
    }
}
