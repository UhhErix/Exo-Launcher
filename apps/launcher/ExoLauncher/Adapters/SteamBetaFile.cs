using ExoLauncher.Helpers;

namespace ExoLauncher.Adapters;

/// <summary>
/// Steam's client branch file (<c>package\beta</c>). Written as Steam's own Client Beta Participation writes it: the branch
/// name alone, no line break and no byte order mark. The old contents are returned so a failed switch puts them back.
/// </summary>
internal static class SteamBetaFile
{
    private static string PathOf(string root) => Path.Combine(root, "package", "beta");

    /// <summary>Writes <paramref name="beta"/> as Steam's branch and returns the bytes it replaced (null when there was no file).</summary>
    /// <exception cref="InvalidOperationException">The folder has no steam.exe, so it is not a Steam install.</exception>
    internal static byte[]? Write(string root, string beta)
    {
        if (!File.Exists(Path.Combine(root, "steam.exe")))
            throw new InvalidOperationException("Not a Steam install: steam.exe is missing.");
        var path = PathOf(root);
        var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".exo-tmp";
        try
        {
            File.WriteAllBytes(temp, System.Text.Encoding.UTF8.GetBytes(beta));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) DeletionGate.DeleteFile(temp);
        }
        return previous;
    }

    /// <summary>
    /// Writes the branch, runs <paramref name="run"/> (close Steam, start it on this branch), and keeps the branch only when
    /// <paramref name="run"/> reports Steam is on it. Any other outcome, an exception or a cancellation included, puts the
    /// old file back. Throws what <see cref="Write"/> throws before anything changed.
    /// </summary>
    internal static async Task<bool> SwitchAsync(string root, string beta, Func<Task<bool>> run)
    {
        var previous = Write(root, beta);
        var onBeta = false;
        try
        {
            onBeta = await run().ConfigureAwait(false);
            return onBeta;
        }
        finally
        {
            if (!onBeta)
            {
                try { Restore(root, previous); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Warn("Steam: its branch file couldn't be put back: " + ex.GetType().Name);
                }
            }
        }
    }

    /// <summary>Puts back what <see cref="Write"/> replaced; null removes the file it created.</summary>
    internal static void Restore(string root, byte[]? previous)
    {
        var path = PathOf(root);
        if (previous is null)
        {
            if (File.Exists(path)) DeletionGate.DeleteFile(path);
            return;
        }
        File.WriteAllBytes(path, previous);
    }
}
