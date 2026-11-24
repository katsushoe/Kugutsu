namespace Kugutsu.App;

internal static class KugutsuIcon
{
    private const string ResourceName = "Kugutsu.App.kugutsu.ico";

    internal static Icon Load(Size size)
    {
        using var stream = typeof(KugutsuIcon).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded icon resource '{ResourceName}' was not found.");
        return new Icon(stream, size);
    }
}
