using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;

public static class ComparisonReport
{
    public static void Run(string csvPath, string referencePath, string output)
    {
        var reference = JsonSerializer.Deserialize<AudioReference>(File.ReadAllText(referencePath))!;
        string expected = string.Join(" ", reference.Clips.Select(c => c.Text));
        using var csv = new CsvReader(new StreamReader(csvPath), CultureInfo.InvariantCulture);
        csv.Read(); csv.ReadHeader();
        var rows = new List<(string Mode, string Text)>();
        while (csv.Read())
            rows.Add((string.IsNullOrEmpty(csv.GetField("SessionId")) ? "Windows captions" : "Local speaker mode", csv.GetField("SourceText") ?? ""));
        var reports = rows.GroupBy(r => r.Mode).Select(g =>
        {
            string actual = string.Join(" ", g.Select(x => x.Text));
            var stats = WordErrors(expected, actual);
            return new { Mode = g.Key, stats.ReferenceWords, stats.Errors, stats.WordErrorRate, ActualText = actual };
        }).ToArray();
        File.WriteAllText(output, JsonSerializer.Serialize(new { reference.Source, reference.AudioSha256, ReferenceText = expected, Results = reports },
            new JsonSerializerOptions { WriteIndented = true }));
        foreach (var report in reports) Console.WriteLine(report.Mode + ": WER " + report.WordErrorRate.ToString("P1"));
    }
    public static (int ReferenceWords, int Errors, double WordErrorRate) WordErrors(string expected, string actual)
    {
        string[] Words(string text) => Regex.Matches(text.ToLowerInvariant().Replace("'", "").Replace("’", ""), "[a-z0-9]+")
            .Select(m => m.Value).ToArray();
        string[] a = Words(expected), b = Words(actual);
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1]; current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(previous[j] + 1, Math.Min(current[j - 1] + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
            previous = current;
        }
        int errors = previous[^1];
        return (a.Length, errors, a.Length == 0 ? 0 : (double)errors / a.Length);
    }
}
