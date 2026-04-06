namespace ClaudeCode.Configuration;

/// <summary>
/// Loads optional remote-managed (MDM/enterprise) settings from a configured HTTP endpoint.
/// Settings from MDM override local settings for enterprise deployments.
/// </summary>
/// <remarks>
/// Configure the endpoint via the <c>CLAUDE_MDM_ENDPOINT</c> environment variable.
/// The endpoint must return a flat JSON object whose keys map to setting names.
/// All values are applied as environment variables prefixed with <c>CLAUDE_</c>.
/// </remarks>
public static class MdmSettingsLoader
{
    private const string EnvVar = "CLAUDE_MDM_ENDPOINT";
    private const int TimeoutSeconds = 3;

    /// <summary>
    /// Attempts to load remote MDM settings from the configured endpoint.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A dictionary mapping setting keys to their string values when the endpoint
    /// is configured and reachable; <see langword="null"/> otherwise.
    /// Any I/O or parse failure is swallowed and <see langword="null"/> is returned.
    /// </returns>
    public static async Task<Dictionary<string, string>?> TryLoadAsync(CancellationToken ct)
    {
        var endpoint = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrEmpty(endpoint))
            return null;

        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            };

            var json = await http.GetStringAsync(endpoint, ct).ConfigureAwait(false);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.GetRawText().Trim('"');

            return result.Count > 0 ? result : null;
        }
        catch
        {
            // Any network or parse failure is non-fatal.
            // MDM is an optional enterprise feature; the application continues without it.
            return null;
        }
    }
}
