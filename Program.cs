namespace OrchardSound;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var instance = new Mutex(true, "Local\\OrchardSoundEditor", out bool firstInstance);
        ApplicationConfiguration.Initialize();
        if (!firstInstance)
        {
            MessageBox.Show("The audio editor is already open. Use the existing window to prevent overlapping playback.", Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.Run(new EditorForm(args.Where(File.Exists).ToArray()));
    }
}
