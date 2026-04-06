namespace ClaudeCode.Services.TeamMemorySync;

using System.Text.RegularExpressions;

/// <summary>
/// Detects high-value secrets in text content before team memory uploads.
/// Mirrors src/services/teamMemorySync.ts secret scanning patterns.
/// </summary>
public static class SecretScanner
{
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("AWS Access Key",     new(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled)),
        ("AWS Secret Key",     new(@"(?i)aws[_\-\.]?secret[_\-\.]?(?:access[_\-\.]?)?key\s*[:=]\s*['""]?[A-Za-z0-9/+=]{40}['""]?", RegexOptions.Compiled)),
        ("GitHub Token",       new(@"ghp_[A-Za-z0-9]{36}|github_pat_[A-Za-z0-9_]{82}", RegexOptions.Compiled)),
        ("Anthropic Key",      new(@"sk-ant-[A-Za-z0-9\-_]{95}", RegexOptions.Compiled)),
        ("Private Key Header", new(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("Password Assignment",new(@"(?i)password\s*[:=]\s*['""][^'""]{8,}['""]", RegexOptions.Compiled)),
        ("Connection String",  new(@"(?i)(?:Server|Data Source|mongodb(?:\+srv)?|postgresql|mysql|redis)://[^\s""']{10,}", RegexOptions.Compiled)),
        ("Bearer Token",       new(@"(?i)Authorization:\s*Bearer\s+[A-Za-z0-9\-._~+/]{20,}", RegexOptions.Compiled)),
    ];

    /// <summary>
    /// Scans <paramref name="content"/> for high-value secret patterns.
    /// Returns the list of detected secret type names (empty if clean).
    /// </summary>
    public static IReadOnlyList<string> Scan(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var found = new List<string>();
        foreach (var (name, pattern) in Patterns)
            if (pattern.IsMatch(content))
                found.Add(name);
        return found;
    }
}
