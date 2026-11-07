using System.IO;
namespace LiveCaptionsTranslator;

public static class RuntimeOptions
{
    public static bool Demo { get; private set; }
    public static string DataDirectory { get; private set; } = AppContext.BaseDirectory;
    public static void Initialize(string[] args)
    {
        Demo = args.Contains("--reader-demo");
        var argument = args.FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.Ordinal));
        if (argument != null) DataDirectory = Path.GetFullPath(argument["--data-dir=".Length..]);
        else if (Demo) DataDirectory = Path.Combine(AppContext.BaseDirectory, "demo-data");
        Directory.CreateDirectory(DataDirectory);
        Directory.SetCurrentDirectory(DataDirectory);
    }
}
