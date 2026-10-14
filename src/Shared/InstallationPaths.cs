namespace Kugutsu.Hosting;

/// <summary>Resolves the standard installation layout without creating directories.</summary>
internal sealed class InstallationPaths
{
    private InstallationPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        BinDirectory = Path.Combine(rootDirectory, "bin");
        ConfigDirectory = Path.Combine(rootDirectory, "config");
        DataDirectory = Path.Combine(rootDirectory, "dat");
        LogsDirectory = Path.Combine(rootDirectory, "logs");
        SettingsFile = Path.Combine(ConfigDirectory, "appsettings.json");
        DetectedLaunchTargetsFile = Path.Combine(ConfigDirectory, "detected-launch-targets.json");
    }

    /// <summary>Gets the normalized installation root.</summary>
    public string RootDirectory { get; }
    /// <summary>Gets the executable directory.</summary>
    public string BinDirectory { get; }
    /// <summary>Gets the configuration directory.</summary>
    public string ConfigDirectory { get; }
    /// <summary>Gets the data directory.</summary>
    public string DataDirectory { get; }
    /// <summary>Gets the log directory.</summary>
    public string LogsDirectory { get; }
    /// <summary>Gets the application settings file path.</summary>
    public string SettingsFile { get; }
    /// <summary>Gets the optional setup-generated launch target file.</summary>
    public string DetectedLaunchTargetsFile { get; }

    /// <summary>Resolves a fully qualified installation root.</summary>
    public static InstallationPaths FromRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The installation root must be fully qualified.", nameof(rootDirectory));
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        return new InstallationPaths(root);
    }

    /// <summary>Resolves the installation root from its bin directory.</summary>
    public static InstallationPaths FromExecutableDirectory(string executableDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        if (!Path.IsPathFullyQualified(executableDirectory))
        {
            throw new ArgumentException("The executable directory must be fully qualified.", nameof(executableDirectory));
        }

        var bin = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        if (!string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Executables must be located in the installation bin directory.", nameof(executableDirectory));
        }

        var parent = Directory.GetParent(bin)
            ?? throw new ArgumentException("The bin directory must have an installation root.", nameof(executableDirectory));
        return FromRoot(parent.FullName);
    }
}
