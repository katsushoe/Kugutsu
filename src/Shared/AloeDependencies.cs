using System.Reflection;
using System.Security.Cryptography;
using Kugutsu.Contracts;
using System.Runtime.Loader;

namespace Kugutsu.Hosting;

internal sealed record AloeDependency(string FileName, string AssemblyName, string Version, string Sha256);

/// <summary>Keeps verified assemblies read-locked for the lifetime of the caller's use.</summary>
internal sealed class AloeDependencyLease(IReadOnlyList<string> paths, IReadOnlyList<FileStream> streams) : IDisposable
{
    internal IReadOnlyList<string> Paths { get; } = paths;
    internal void LoadAssemblies()
    {
        foreach (var stream in streams.OrderBy(stream => Path.GetFileName(stream.Name) == "Aloe.CommonLib.dll" ? 0 : 1))
        {
            stream.Position = 0;
            AssemblyLoadContext.Default.LoadFromStream(stream);
        }
    }
    public void Dispose() { foreach (var stream in streams) { stream.Dispose(); } }
}

internal sealed class AloeDependencies(InstallationPaths installation)
{
    internal const string Bundle = "20261013";
    internal static IReadOnlyList<AloeDependency> Manifest { get; } = Array.AsReadOnly(new AloeDependency[]
    {
        new("CompilerLib.dll", "CompilerLib", "1.0.0.0", "677EA9E2176E640D3DA5F6ABAA5C459751FCFF6CE1ECF271AA5DE0DC137BE1BB"),
        new("Aloe.CommonLib.dll", "Aloe.CommonLib", "1.0.0.0", "629EA56CB31F690C752BC4C669795638E73D453D2CE5CE99D7E622A75588EBB8"),
        new("Aloe.RuntimeLib.dll", "Aloe.RuntimeLib", "1.0.0.0", "FD9E932EB230CD1A5164F25DB507EBFAF93CDC472628FA38D411EC784542C034")
    });

    internal AloeDependencyLease OpenVerified(CancellationToken token)
    {
        var paths = new List<string>();
        var streams = new List<FileStream>();
        try
        {
            foreach (var dependency in Manifest)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(installation.BinDirectory, "aloe", Bundle, dependency.FileName);
                for (string? ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                {
                    if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) { throw Unavailable(); }
                }
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                streams.Add(stream);
                if (stream.Length is <= 0 or > 16777216
                    || Convert.ToHexString(SHA256.HashData(stream)) != dependency.Sha256) { throw Unavailable(); }
                var name = AssemblyName.GetAssemblyName(path);
                if (name.Name != dependency.AssemblyName || name.Version?.ToString() != dependency.Version) { throw Unavailable(); }
                token.ThrowIfCancellationRequested();
                paths.Add(path);
            }
            return new(paths.AsReadOnly(), streams.AsReadOnly());
        }
        catch (OperationCanceledException) { foreach (var stream in streams) { stream.Dispose(); } throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or AloeDependencyException)
        {
            foreach (var stream in streams) { stream.Dispose(); }
            throw Unavailable();
        }
        catch { foreach (var stream in streams) { stream.Dispose(); } throw; }
    }

    private static AloeDependencyException Unavailable() => new();
}

internal sealed class AloeDependencyException() : Exception("Fixed Aloe dependencies unavailable.");
