using System.IO;
namespace LiveCaptionsTranslator;

public static class RuntimeOptions
{
    public static bool Demo { get; private set; }
    public static bool SpeakerTest { get; private set; }
    // IncludeAllContentForSelfExtract changes AppContext.BaseDirectory to a temporary directory.
    // Keep mutable files beside the launched executable, including when its filename is changed.
    public static string ProgramDirectory { get; } = LocateProgramDirectory(Environment.ProcessPath, AppContext.BaseDirectory);
    internal static string LocateProgramDirectory(string? processPath, string fallback) =>
        !string.IsNullOrWhiteSpace(processPath) &&
        !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.GetFullPath(processPath)) ?? fallback : fallback;
    public static string ModelDirectory { get; private set; } = Path.Combine(ProgramDirectory, "models", "speaker-v1");
    public static string DataDirectory { get; private set; } = ProgramDirectory;
    public static void Initialize(string[] args)
    {
        Demo = args.Contains("--reader-demo");
        SpeakerTest = args.Contains("--speaker-test");
        var model = args.FirstOrDefault(a => a.StartsWith("--speaker-model-dir=", StringComparison.Ordinal));
        if (model != null) ModelDirectory = Path.GetFullPath(model["--speaker-model-dir=".Length..]);
        if (SpeakerTest && !args.Any(a => a.StartsWith("--data-dir=", StringComparison.Ordinal)))
            throw new ArgumentException("--speaker-test requires an isolated --data-dir.");
        var argument = args.FirstOrDefault(a => a.StartsWith("--data-dir=", StringComparison.Ordinal));
        if (argument != null) DataDirectory = Path.GetFullPath(argument["--data-dir=".Length..]);
        else if (Demo) DataDirectory = Path.Combine(ProgramDirectory, "demo-data");
        Directory.CreateDirectory(DataDirectory);
        Directory.SetCurrentDirectory(DataDirectory);
    }
}
