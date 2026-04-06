namespace ClaudeCode.Services.Keychain;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Provides cross-platform secure storage and retrieval of named credential strings.
/// </summary>
public interface IKeychainService
{
    /// <summary>Retrieves the stored value for <paramref name="key"/>, or <see langword="null"/> if not found.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Stores or overwrites <paramref name="value"/> under <paramref name="key"/>.</summary>
    Task SetAsync(string key, string value);

    /// <summary>Deletes the stored value for <paramref name="key"/>. No-op if the key does not exist.</summary>
    Task DeleteAsync(string key);
}

/// <summary>
/// Cross-platform implementation of <see cref="IKeychainService"/>.
/// <list type="bullet">
///   <item><description>Windows — DPAPI (<see cref="ProtectedData"/>) encrypted files under <c>%APPDATA%\.claude\credentials\</c>.</description></item>
///   <item><description>macOS — OS keychain via the <c>security</c> CLI.</description></item>
///   <item><description>Linux — AES-256 encrypted files under <c>~/.claude/credentials/</c>, key derived from machine+user identity.</description></item>
///   <item><description>Fallback — plaintext files with a console warning (used only when the platform path fails unexpectedly).</description></item>
/// </list>
/// </summary>
public sealed class KeychainService : IKeychainService
{
    // Salt used for Linux PBKDF2 key derivation. Changing this breaks existing stored credentials.
    private const string LinuxKdfSalt = "claude-code-credentials-v1";
    private const int LinuxKdfIterations = 10_000;

    // -------------------------------------------------------------------------
    // IKeychainService
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task<string?> GetAsync(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        try
        {
            if (OperatingSystem.IsWindows())
                return await WindowsGetAsync(key).ConfigureAwait(false);

            if (OperatingSystem.IsMacOS())
                return await MacGetAsync(key).ConfigureAwait(false);

            if (OperatingSystem.IsLinux())
                return await LinuxGetAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[keychain] GetAsync({key}) platform path failed: {ex.Message}. " +
                "Falling back to plaintext store.").ConfigureAwait(false);
        }

        return await FallbackGetAsync(key).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetAsync(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await WindowsSetAsync(key, value).ConfigureAwait(false);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                await MacSetAsync(key, value).ConfigureAwait(false);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                await LinuxSetAsync(key, value).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[keychain] SetAsync({key}) platform path failed: {ex.Message}. " +
                "Falling back to plaintext store.").ConfigureAwait(false);
        }

        await FallbackSetAsync(key, value).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await WindowsDeleteAsync(key).ConfigureAwait(false);
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                await MacDeleteAsync(key).ConfigureAwait(false);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                await LinuxDeleteAsync(key).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[keychain] DeleteAsync({key}) platform path failed: {ex.Message}. " +
                "Falling back to plaintext store.").ConfigureAwait(false);
        }

        await FallbackDeleteAsync(key).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Windows — DPAPI (ProtectedData)
    // -------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static Task<string?> WindowsGetAsync(string key)
    {
        var path = WindowsCredentialPath(key);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        var encrypted = File.ReadAllBytes(path);
        var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(decrypted));
    }

    [SupportedOSPlatform("windows")]
    private static async Task WindowsSetAsync(string key, string value)
    {
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            null,
            DataProtectionScope.CurrentUser);

        var path = WindowsCredentialPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, encrypted).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static Task WindowsDeleteAsync(string key)
    {
        var path = WindowsCredentialPath(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsCredentialPath(string key)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ".claude", "credentials", $"{SanitizeKey(key)}.dat");
    }

    // -------------------------------------------------------------------------
    // macOS — `security` CLI
    // -------------------------------------------------------------------------

    [SupportedOSPlatform("macos")]
    private static async Task<string?> MacGetAsync(string key)
    {
        var (exitCode, stdout, _) = await RunProcessAsync(
            "security",
            $"find-generic-password -a {Environment.UserName} -s claudecode -l {SanitizeKey(key)} -w")
            .ConfigureAwait(false);

        if (exitCode != 0)
            return null; // item does not exist

        return stdout.Trim();
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacSetAsync(string key, string value)
    {
        // -U overwrites the item if it already exists.
        var (exitCode, _, stderr) = await RunProcessAsync(
            "security",
            $"add-generic-password -a {Environment.UserName} -s claudecode -l {SanitizeKey(key)} -w {value} -U")
            .ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"macOS 'security add-generic-password' failed (exit {exitCode}): {stderr.Trim()}");
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task MacDeleteAsync(string key)
    {
        // Exit code non-zero when the item doesn't exist — treat as a no-op.
        await RunProcessAsync(
            "security",
            $"delete-generic-password -a {Environment.UserName} -s claudecode -l {SanitizeKey(key)}")
            .ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Linux — AES-256 file encryption
    // -------------------------------------------------------------------------

    private static Task<string?> LinuxGetAsync(string key)
    {
        var path = LinuxCredentialPath(key, ".aes");
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        var fileBytes = File.ReadAllBytes(path);
        if (fileBytes.Length < 17) // 16-byte IV + at least 1 byte of ciphertext
            return Task.FromResult<string?>(null);

        var iv = fileBytes[..16];
        var ciphertext = fileBytes[16..];

        var (aesKey, _) = DeriveLinuxKey();
        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Task.FromResult<string?>(Encoding.UTF8.GetString(plainBytes));
    }

    private static async Task LinuxSetAsync(string key, string value)
    {
        var plainBytes = Encoding.UTF8.GetBytes(value);

        // Generate a random IV for each stored value.
        var iv = RandomNumberGenerator.GetBytes(16);
        var (aesKey, _) = DeriveLinuxKey();

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // File layout: [16-byte IV][ciphertext]
        var fileBytes = new byte[iv.Length + ciphertext.Length];
        iv.CopyTo(fileBytes, 0);
        ciphertext.CopyTo(fileBytes, iv.Length);

        var path = LinuxCredentialPath(key, ".aes");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, fileBytes).ConfigureAwait(false);
    }

    private static Task LinuxDeleteAsync(string key)
    {
        var path = LinuxCredentialPath(key, ".aes");
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private static string LinuxCredentialPath(string key, string extension)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "credentials", $"{SanitizeKey(key)}{extension}");
    }

    /// <summary>
    /// Derives a deterministic AES-256 key and IV from the machine name and user name via PBKDF2-SHA256.
    /// </summary>
    private static (byte[] Key, byte[] Iv) DeriveLinuxKey()
    {
        var passwordBytes = Encoding.UTF8.GetBytes(Environment.MachineName + Environment.UserName);
        var salt = Encoding.UTF8.GetBytes(LinuxKdfSalt);

        // Derive 48 bytes total: first 32 = AES-256 key, remaining 16 = deterministic IV base.
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes,
            salt,
            LinuxKdfIterations,
            HashAlgorithmName.SHA256,
            outputLength: 48);

        // The IV returned here is a deterministic base value only. LinuxSetAsync always
        // overwrites it with a fresh random IV which is stored prepended to the ciphertext.
        return (derived[..32], derived[32..]);
    }

    // -------------------------------------------------------------------------
    // Plaintext fallback
    // -------------------------------------------------------------------------

    private static Task<string?> FallbackGetAsync(string key)
    {
        var path = FallbackPath(key);
        if (!File.Exists(path))
            return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(File.ReadAllText(path));
    }

    private static async Task FallbackSetAsync(string key, string value)
    {
        await Console.Error.WriteLineAsync(
            $"[keychain] WARNING: storing '{key}' in plaintext at {FallbackPath(key)}. " +
            "No platform-native secure storage is available.").ConfigureAwait(false);

        var path = FallbackPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, value).ConfigureAwait(false);
    }

    private static Task FallbackDeleteAsync(string key)
    {
        var path = FallbackPath(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private static string FallbackPath(string key)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "credentials", $"{SanitizeKey(key)}.txt");
    }

    // -------------------------------------------------------------------------
    // Process helper (used by macOS paths)
    // -------------------------------------------------------------------------

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sanitizes a credential key so that it is safe to use as a filename component.
    /// Replaces any character that is not alphanumeric, a hyphen, or an underscore with an underscore.
    /// </summary>
    private static string SanitizeKey(string key)
    {
        var sb = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }
        return sb.ToString();
    }
}
