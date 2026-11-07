using System.IO;
using LiveCaptionsTranslator.transcript;

namespace LiveCaptionsTranslator.speech;

/// <summary>Only the provisional tail is revised; committed text has permanent database ids.</summary>
public sealed class SpeechSession(string id, NativeSpeechProcessor processor, HistoryStore store,
    TranscriptEngine engine, Func<(string Language, string Api, bool Paused)> options)
{
    private readonly SpeakerParagraphAssembler assembler = new();
    private long lastCommitted = -1;
    private string previousDraft = "";
    private double? firstReadyAt;
    private string? activeContext;
    private (string Language, string Api, bool Paused) activeOptions;
    public event Action<string>? PreviewChanged;
    public ParagraphLength Length { get => assembler.Length; set => assembler.Length = value; }
    public int PauseSeconds { get => assembler.PauseSeconds; set => assembler.PauseSeconds = value; }
    public async Task AcceptAsync(SpeechSnapshot snapshot)
    {
        var selected = options();
        string selectedContext = selected.Language + "|" + selected.Api + "|" + selected.Paused;
        if (activeContext == null) { activeContext = selectedContext; activeOptions = selected; }
        bool contextChanged = selectedContext != activeContext;
        double watermark = snapshot.Finished || contextChanged ? double.MaxValue : snapshot.AudioTime - NativeSpeechProcessor.ConfirmationDelay;
        var ready = snapshot.Words.Where(w => w.Key > lastCommitted && w.End <= watermark).ToArray();
        if (ready.Length > 0)
        {
            firstReadyAt ??= snapshot.AudioTime;
            if (!snapshot.Finished && !contextChanged && ready.Length < 4 && snapshot.AudioTime - firstReadyAt < 1)
                ready = Array.Empty<TimedWord>();
        }
        var pending = snapshot.Words.Where(w => w.Key > lastCommitted && (ready.Length == 0 || w.Key > ready[^1].Key)).ToArray();
        // Include the last incomplete ASR word in the persisted draft, not just complete words.
        string preview = processor.PreviewText();
        string remaining = string.Join(" ", pending.Select(w => w.Text));
        if (ready.Length > 0)
        {
            string readyRaw = string.Join(" ", ready.Select(w => w.Text));
            if (preview.StartsWith(readyRaw, StringComparison.Ordinal))
                remaining = preview[readyRaw.Length..].TrimStart();
            var updates = assembler.Assemble(id, ready, snapshot.Speakers, activeOptions.Language,
                activeOptions.Paused ? "LogOnly" : activeOptions.Api, remaining, processor.Punctuate, snapshot.AudioTime);
            foreach (var update in updates)
                await engine.AcceptAsync(update, activeOptions.Language, activeOptions.Api, activeOptions.Paused);
            firstReadyAt = null;
            lastCommitted = ready[^1].Key;
            processor.Acknowledge(lastCommitted);
            preview = processor.PreviewText();
        }
        if (contextChanged) { activeContext = selectedContext; activeOptions = selected; }
        if (snapshot.Finished && preview.Length > 0)
            throw new InvalidOperationException("收尾后仍有未保存原文，草稿已保留。");
        if (preview != previousDraft)
        {
            await store.SaveDraftAsync(new(id, preview, activeOptions.Language,
                activeOptions.Paused ? "LogOnly" : activeOptions.Api,
                Math.Max(0, snapshot.AudioTime - NativeSpeechProcessor.ConfirmationDelay), snapshot.AudioTime));
            previousDraft = preview;
            PreviewChanged?.Invoke(preview);
        }
    }
}
