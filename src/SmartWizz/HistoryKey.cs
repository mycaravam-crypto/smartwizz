using System.Security.Cryptography;

namespace SmartWizz;

internal static class HistoryKey
{
    internal static string DirectoryPath()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("local application data unavailable");
        return Path.GetFullPath(Path.Combine(local, "smartwizz", "data"));
    }

    internal static bool PreviouslyInitialized()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return File.Exists(Path.Combine(DirectoryPath(), "history-key.dpapi")); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static async Task<string> SafeDirectoryAsync(string repo, CancellationToken ct)
    {
        string root = await GitDiff.RepositoryRootAsync(repo, ct);
        string directory = DirectoryPath();
        string relative = Path.GetRelativePath(root, directory);
        if (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("history must be outside the checkout");
        // Reject redirected storage components rather than following junctions or symlinks.
        for (DirectoryInfo? parent = new(directory); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("redirected history directory");
        return directory;
    }

    internal static byte[]? Load(string directory, bool initialize)
    {
        string? encoded = Environment.GetEnvironmentVariable("SMARTWIZZ_HISTORY_KEY");
        if (!string.IsNullOrEmpty(encoded))
        {
            byte[] supplied = Convert.FromBase64String(encoded);
            if (supplied.Length == 32) return supplied;
            CryptographicOperations.ZeroMemory(supplied);
            throw new ArgumentException("invalid history key");
        }

        if (!OperatingSystem.IsWindows()) return null;
        string path = Path.Combine(directory, "history-key.dpapi");
        if (File.Exists(path)) return ReadWindowsKey(path);
        if (!initialize) return null;
        Directory.CreateDirectory(directory);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] protectedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(protectedKey);
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { }
            return ReadWindowsKey(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            File.Delete(temp);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static byte[] ReadWindowsKey(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("redirected history key");
        byte[] key = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("invalid protected history key");
    }
}
