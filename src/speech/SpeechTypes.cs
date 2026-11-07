using System.Text.RegularExpressions;
using LiveCaptionsTranslator.transcript;

namespace LiveCaptionsTranslator.speech;

public enum SpeakerState { Unknown, Identified, Overlap }
public sealed record SpeechInfo(string SessionId, string SourceKey, string? SpeakerId,
    SpeakerState State, double Start, double End)
{
    public string Label => State switch
    {
        SpeakerState.Overlap => "多人重叠",
        SpeakerState.Identified when SpeakerId != null => "说话人 " + SpeakerId,
        _ => "未确定"
    };
}
public sealed record SpeechDraft(string SessionId, string Text, string Language, string Api,
    double Start, double End);
public sealed record TimedWord(long Key, string Text, double Start, double End);
public sealed record SpeakerSpan(double Start, double End, string? SpeakerId, SpeakerState State);
public sealed record SpeechSnapshot(IReadOnlyList<TimedWord> Words, IReadOnlyList<SpeakerSpan> Speakers,
    double AudioTime, bool Finished = false, long Revision = 0, string? SessionId = null);
public interface ISpeechSource : IAsyncDisposable
{
    string SessionId { get; }
    event Action<SpeechSnapshot>? Updated;
    Task RunAsync(CancellationToken token);
}
public static class SpeakerLabels
{
    public static string At(int index)
    {
        string label = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
            label = (char)('A' + (n - 1) % 26) + label;
        return label;
    }
    public static SpeakerSpan ForWord(TimedWord word, IReadOnlyList<SpeakerSpan> spans)
    {
        double point = (word.Start + word.End) / 2;
        var matches = spans.Where(s => s.Start <= point && s.End > point).ToArray();
        if (matches.Any(s => s.State == SpeakerState.Overlap) ||
            matches.Where(s => s.SpeakerId != null).Select(s => s.SpeakerId).Distinct().Count() > 1)
            return new(word.Start, word.End, null, SpeakerState.Overlap);
        return matches.FirstOrDefault(s => s.State == SpeakerState.Identified)
            ?? new(word.Start, word.End, null, SpeakerState.Unknown);
    }
}

/// <summary>Speaker boundaries precede all ordinary paragraph boundaries. Keys never collide with Windows captions.</summary>
public sealed class SpeakerParagraphAssembler
{
    private static long nextKey = -1000000;
    private long paragraphKey;
    private int words;
    private string? previousSpeaker, previousContext;
    private SpeakerState previousState;
    private double lastEnd = double.NegativeInfinity;
    private bool sentenceEnd;
    private string punctuationContext = "";
    public ParagraphLength Length { get; set; } = ParagraphLength.Balanced;
    public int PauseSeconds { get; set; } = 3;
    public IReadOnlyList<SegmentUpdate> Assemble(string session, IReadOnlyList<TimedWord> ready,
        IReadOnlyList<SpeakerSpan> spans, string language, string api, string remainingText,
        Func<string, string>? punctuate = null, double? pendingEnd = null)
    {
        var result = new List<SegmentUpdate>();
        int minimum = Length == ParagraphLength.Short ? 30 : Length == ParagraphLength.Long ? 60 : 40;
        int maximum = Length == ParagraphLength.Short ? 60 : Length == ParagraphLength.Long ? 120 : 80;
        string context = session + "|" + language + "|" + api;
        int i = 0;
        while (i < ready.Count)
        {
            var speaker = SpeakerLabels.ForWord(ready[i], spans);
            double gap = ready[i].Start - lastEnd;
            if (paragraphKey == 0 || context != previousContext || speaker.SpeakerId != previousSpeaker ||
                speaker.State != previousState || gap >= PauseSeconds || words >= maximum ||
                (words >= minimum && (sentenceEnd || gap >= 1.5)))
            {
                paragraphKey = Interlocked.Decrement(ref nextKey);
                words = 0;
                punctuationContext = "";
            }
            int start = i;
            while (i < ready.Count && i - start < 20 && words + i - start < maximum)
            {
                var who = SpeakerLabels.ForWord(ready[i], spans);
                if (who.SpeakerId != speaker.SpeakerId || who.State != speaker.State ||
                    (i > start && (ready[i].Start - ready[i - 1].End >= PauseSeconds ||
                        (words + i - start >= minimum && ready[i].Start - ready[i - 1].End >= 1.5)))) break;
                i++;
            }
            string text = string.Join(" ", ready.Skip(start).Take(i - start).Select(w => w.Text));
            string raw = text;
            if (punctuate != null)
            {
                string contextText = string.Join(" ", new[] { punctuationContext, raw }.Where(s => s.Length > 0));
                var rawWords = Regex.Matches(contextText, @"\S+").Select(m => m.Value).ToArray();
                var punctuated = Regex.Matches(punctuate(contextText), @"\S+").Select(m => m.Value).ToArray();
                string Normal(string w) => new(w.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                if (rawWords.Length == punctuated.Length && rawWords.Select(Normal).SequenceEqual(punctuated.Select(Normal)))
                    text = string.Join(" ", punctuated.Skip(rawWords.Length - (i - start)));
            }
            punctuationContext = string.Join(" ", (punctuationContext + " " + raw).Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(40));
            words += i - start;
            sentenceEnd = text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!');
            lastEnd = ready[i - 1].End;
            previousSpeaker = speaker.SpeakerId; previousState = speaker.State; previousContext = context;
            string tail = string.Join(" ", ready.Skip(i).Select(w => w.Text).Append(remainingText).Where(t => t.Length > 0));
            result.Add(new SegmentUpdate(Interlocked.Decrement(ref nextKey), 1, text, true)
            {
                ParagraphKey = paragraphKey,
                Speech = new(session, ready[start].Key.ToString(), speaker.SpeakerId, speaker.State, ready[start].Start, lastEnd),
                RemainingDraft = new(session, tail, language, api, i < ready.Count ? ready[i].Start : lastEnd,
                    Math.Max(lastEnd, pendingEnd ?? ready[^1].End))
            });
        }
        return result;
    }
}

/// <summary>A bounded prototype bank; uncertain or overlapping samples never train it.</summary>
public sealed class SpeakerRegistry
{
    private readonly List<float[]> prototypes = new();
    public double MatchThreshold { get; set; } = 0.5;
    public double AmbiguityMargin { get; set; } = 0.06;
    public int Count => prototypes.Count;
    public string? Match(float[] embedding, bool allowNew = true)
    {
        var normalized = Normalize(embedding);
        if (normalized == null) return null;
        var scores = prototypes.Select((p, i) => (Id: i, Score: Dot(p, normalized)))
            .OrderByDescending(x => x.Score).ToArray();
        if (scores.Length > 0 && scores[0].Score >= MatchThreshold)
        {
            if (scores.Length > 1 && scores[0].Score - scores[1].Score < AmbiguityMargin) return null;
            int id = scores[0].Id;
            prototypes[id] = Normalize(prototypes[id].Select((v, i) => v * .9f + normalized[i] * .1f).ToArray())!;
            return SpeakerLabels.At(id);
        }
        if (!allowNew || prototypes.Count >= 32) return null;
        // A grey zone must not become a confident new identity.
        if (scores.Length > 0 && scores[0].Score >= MatchThreshold - AmbiguityMargin) return null;
        prototypes.Add(normalized);
        return SpeakerLabels.At(prototypes.Count - 1);
    }
    private static double Dot(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();
    private static float[]? Normalize(float[] a)
    {
        double norm = Math.Sqrt(a.Sum(x => (double)x * x));
        return norm > 1e-8 && double.IsFinite(norm) ? a.Select(x => (float)(x / norm)).ToArray() : null;
    }
}
