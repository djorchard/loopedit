using System.Reflection;

namespace OrchardSound;

public static class Branding
{
    public const string Name = "LoopEdit";

    // Keep existing export and mouse preferences available to both builds.
    internal const string SettingsDirectoryName = "Loopsmith";

    public static Icon LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Name + ".AppIcon")
            ?? throw new InvalidOperationException($"The {Name} application icon is missing.");
        using var source = new Icon(stream, 32, 32);
        return (Icon)source.Clone();
    }
}
