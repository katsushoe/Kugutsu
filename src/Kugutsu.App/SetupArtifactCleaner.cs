using Kugutsu.Hosting;

namespace Kugutsu.App;

/// <summary>Removes installer-generated files that are not owned by MSI components during uninstall.</summary>
internal static class SetupArtifactCleaner
{
    public static int Run(InstallationPaths paths)
    {
        try
        {
            EnsureNotReparsePoint(paths.RootDirectory);
            DeleteChildDirectory(paths.RootDirectory, "bin", "drivers");
            DeleteChildDirectory(paths.RootDirectory, "dat", "driver-setup");
            DeleteChildFile(paths.RootDirectory, "config", "detected-launch-targets.json");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or ArgumentException)
        {
            return 1;
        }
    }

    private static void DeleteChildDirectory(string rootDirectory, string parentName, string childName)
    {
        var parent = GetChildPath(rootDirectory, parentName);
        if (!Directory.Exists(parent)) { return; }
        EnsureNotReparsePoint(parent);

        var target = GetChildPath(parent, childName);
        if (!Directory.Exists(target)) { return; }
        DeleteDirectoryTree(new DirectoryInfo(target));
    }

    private static void DeleteChildFile(string rootDirectory, string parentName, string fileName)
    {
        var parent = GetChildPath(rootDirectory, parentName);
        if (!Directory.Exists(parent)) { return; }
        EnsureNotReparsePoint(parent);

        var target = GetChildPath(parent, fileName);
        if (!File.Exists(target)) { return; }
        File.Delete(target);
    }

    private static string GetChildPath(string parentDirectory, string childName)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory));
        var child = Path.GetFullPath(Path.Combine(parent, childName));
        if (!child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The installer cleanup target is outside its parent directory.", nameof(childName));
        }
        return child;
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The installer cleanup path is a reparse point.");
        }
    }

    private static void DeleteDirectoryTree(DirectoryInfo directory)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            directory.Delete();
            return;
        }

        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if (child is DirectoryInfo childDirectory)
            {
                DeleteDirectoryTree(childDirectory);
            }
            else
            {
                child.Delete();
            }
        }
        directory.Delete();
    }
}
