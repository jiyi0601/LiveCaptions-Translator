using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LiveCaptionsTranslator.models;
namespace LiveCaptionsTranslator.transcript;

public sealed class ReaderEntry : INotifyPropertyChanged
{
    private TranslationHistoryEntry entry;
    public long Id => entry.Id;
    public string Time => entry.TimestampFull;
    public string Source => entry.SourceText;
    public string Translation => Regex.Replace(entry.TranslatedText, @"^\[\d+\s*ms\]\s*", "");
    public string Status => string.IsNullOrWhiteSpace(entry.TranslatedText) ? "等待译文…"
        : entry.TranslatedText == "[Paused]" || entry.TranslatedText == "N/A" ? "仅记录原文"
        : entry.TranslatedText.Contains("[ERROR]") ? "翻译失败 · 原文已保存" : "";
    public bool HasStatus => Status.Length > 0;
    public string DisplayTranslation => HasStatus ? "" : Translation;
    public ReaderEntry(TranslationHistoryEntry entry) => this.entry = entry;
    public void Update(TranslationHistoryEntry value)
    {
        if (value.Version < entry.Version) return; entry = value;
        foreach (var name in new[] { nameof(Time), nameof(Source), nameof(Translation), nameof(Status), nameof(HasStatus), nameof(DisplayTranslation) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public string CopyText => Time + Environment.NewLine + Source + Environment.NewLine
        + (HasStatus ? Status : Translation);
    public event PropertyChangedEventHandler? PropertyChanged;
}
