using CsvHelper.Configuration.Attributes;
namespace LiveCaptionsTranslator.models;

public record TranslationHistoryEntry
{
    [Ignore] public LiveCaptionsTranslator.speech.SpeechInfo? Speech { get; init; }
    [Ignore] public string SpeakerLabel => Speech?.Label ?? "";
    [Ignore] public long Id { get; init; }
    [Ignore] public long ParagraphId { get; init; }
    [Ignore] public long GroupId => ParagraphId > 0 ? ParagraphId : Id;
    [Ignore] public long Version { get; init; }
    public required string Timestamp { get; init; }
    [Ignore] public required string TimestampFull { get; init; }
    public required string SourceText { get; init; }
    public required string TranslatedText { get; init; }
    public required string TargetLanguage { get; init; }
    public required string ApiUsed { get; init; }
}
