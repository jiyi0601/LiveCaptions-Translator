using System.ComponentModel;
using System.Text.RegularExpressions;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.transcript;

/// <summary>A paragraph view assembled from individually versioned translation fragments.</summary>
public sealed class ReaderEntry : INotifyPropertyChanged
{
    private readonly SortedDictionary<long, TranslationHistoryEntry> parts = new();
    public long Id { get; }
    private TranslationHistoryEntry First => parts.Values.First();
    public string Time => First.TimestampFull;
    public string Source => string.Join(" ", parts.Values.Select(p => p.SourceText).Where(s => s.Length > 0));
    private static bool Error(TranslationHistoryEntry p) => p.TranslatedText.Contains("[ERROR]");
    private static bool Paused(TranslationHistoryEntry p) => p.TranslatedText is "[Paused]" or "N/A";
    private static bool Pending(TranslationHistoryEntry p) => p.SourceText.Length > 0 && !Error(p) && !Paused(p) &&
        (string.IsNullOrWhiteSpace(p.TranslatedText) || (p.Version > 0 && p.Version % 2 == 0));
    private static string Clean(string text) => Regex.Replace(text, @"^\[\s*\d+\s*ms\]\s*", "");
    public string Translation => string.Join(" ", parts.Values
        .Where(p => !Error(p) && !Paused(p)).Select(p => Clean(p.TranslatedText)).Where(s => !string.IsNullOrWhiteSpace(s)));
    public string DisplayTranslation => Translation;
    public string Status
    {
        get
        {
            var labels = new List<string>();
            if (parts.Values.Any(Error))
                labels.Add(parts.Count > 1 ? "部分内容翻译失败 · 原文已保存" : "翻译失败 · 原文已保存");
            if (parts.Values.Any(Pending)) labels.Add(Translation.Length > 0 ? "正在补充译文…" : "等待译文…");
            if (parts.Values.All(Paused)) labels.Add("仅记录原文");
            return string.Join(" · ", labels);
        }
    }
    public bool HasStatus => Status.Length > 0;
    public ReaderEntry(TranslationHistoryEntry entry)
    {
        Id = entry.GroupId;
        parts.Add(entry.Id, entry);
    }
    public void Update(TranslationHistoryEntry value)
    {
        if (value.GroupId != Id) throw new ArgumentException("Fragment belongs to another paragraph.", nameof(value));
        if (parts.TryGetValue(value.Id, out var old) && value.Version < old.Version) return;
        parts[value.Id] = value;
        foreach (var name in new[] { nameof(Time), nameof(Source), nameof(Translation), nameof(Status), nameof(HasStatus), nameof(DisplayTranslation) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public string CopyText => Time + Environment.NewLine + Source + Environment.NewLine
        + DisplayTranslation + (HasStatus ? Environment.NewLine + Status : "");
    public TranslationHistoryEntry ExportEntry() => First with
    {
        Id = Id,
        ParagraphId = Id,
        Timestamp = Time,
        SourceText = Source,
        TranslatedText = string.Join(" ", parts.Values.Select(p =>
            string.IsNullOrWhiteSpace(p.TranslatedText) && p.SourceText.Length > 0 ? "[等待译文]" : p.TranslatedText).Where(s => s.Length > 0))
    };
    public event PropertyChangedEventHandler? PropertyChanged;
}
