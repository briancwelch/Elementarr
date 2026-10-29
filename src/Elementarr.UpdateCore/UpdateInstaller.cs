namespace Elementarr.UpdateCore;

public static class UpdateInstaller
{
    /// <summary>Replaces only application binaries after stopping, and restores the previous application when the new service fails validation.</summary>
    public static async Task ApplyAsync(string staged, string installed, string backup,
        Func<Task> stop, Func<Task> start, Func<Task<bool>> healthy, Func<Task>? backupData = null, Func<Task>? restoreData = null)
    {
        ValidateDistinct(staged, installed, backup);
        CopyTree(installed, backup);
        await stop();
        try
        {
            if (backupData is not null) await backupData();
            ReplaceTree(staged, installed);
            await start();
            if (!await healthy()) throw new IOException("The updated service did not become healthy.");
        }
        catch
        {
            await stop();
            ReplaceTree(backup, installed);
            if (restoreData is not null) await restoreData();
            await start();
            throw;
        }
    }

    /// <summary>Rejects overlapping application/staging locations and filesystem links before a rollback-capable replacement.</summary>
    private static void ValidateDistinct(params string[] paths)
    {
        var resolved = paths.Select(x => Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        for (var i = 0; i < resolved.Length; i++)
            for (var j = i + 1; j < resolved.Length; j++)
                if (resolved[i].StartsWith(resolved[j], StringComparison.OrdinalIgnoreCase) || resolved[j].StartsWith(resolved[i], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update directories must be separate.");
        foreach (var directory in paths.Where(Directory.Exists))
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Any(x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("Update directories cannot contain filesystem links.");
    }

    /// <summary>Copies an application tree without following links or changing any data directory.</summary>
    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    /// <summary>Removes obsolete application files only within the previously validated installation root, then copies the new tree.</summary>
    private static void ReplaceTree(string source, string destination)
    {
        ValidateDistinct(source, destination);
        foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)) File.Delete(file);
        CopyTree(source, destination);
    }
}
