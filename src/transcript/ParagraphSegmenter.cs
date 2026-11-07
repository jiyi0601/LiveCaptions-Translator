using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.transcript;

public enum ParagraphLength { Balanced, Short, Long }

/// <summary>Assigns stable translation fragments to readable, bounded paragraphs.</summary>
public sealed class ParagraphSegmenter
{
    private readonly CaptionSegmenter sentences = new(TimeSpan.FromMilliseconds(1200));
    private readonly Dictionary<long, Sentence> sentenceStates = new();
    private Paragraph? current;
    private DateTimeOffset? lastContentAt;
    private long nextFragment, nextParagraph;
    private string? context;
    private const int FragmentWords = 20;
    public const int DefaultPauseSeconds = 3;
    public static IReadOnlyList<int> PauseOptions { get; } = Array.AsReadOnly(new[] { 2, 3, 5, 8, 12 });
    public int PauseSeconds { get; private set; }
    public static int NormalizePauseSeconds(int seconds) => PauseOptions.Contains(seconds) ? seconds : DefaultPauseSeconds;
    public void SetPauseSeconds(int seconds) => PauseSeconds = NormalizePauseSeconds(seconds);
    public ParagraphLength Length { get; private set; }
    public (int Minimum, int Maximum) Limits => Length switch
    {
        ParagraphLength.Short => (30, 60),
        ParagraphLength.Long => (60, 120),
        _ => (40, 80)
    };

    private sealed class Paragraph(long key)
    {
        public long Key { get; } = key;
        public int Words;
        public bool Closed;
    }
    private sealed class Sentence
    {
        public List<Fragment> Fragments { get; } = new();
        public int Words;
    }
    private sealed class Fragment(long key, int start, Paragraph paragraph)
    {
        public long Key { get; } = key;
        public int Start { get; } = start;
        public int? End;
        public Paragraph Paragraph { get; } = paragraph;
        public int Revision;
        public string Text = "";
        public bool Final;
        public int Words;
    }

    public ParagraphSegmenter(ParagraphLength length = ParagraphLength.Balanced, int pauseSeconds = DefaultPauseSeconds)
    {
        SetLength(length);
        SetPauseSeconds(pauseSeconds);
    }
    public void SetLength(ParagraphLength length) =>
        Length = Enum.IsDefined(length) ? length : ParagraphLength.Balanced;

    public void SetContext(string value)
    {
        if (context != null && context != value)
        {
            if (current != null) current.Closed = true;
            foreach (var sentence in sentenceStates.Values)
                if (sentence.Fragments.LastOrDefault() is { End: null } tail)
                    tail.End = sentence.Words;
            sentences.SealCurrent();
        }
        context = value;
    }

    public void Reset()
    {
        sentences.Reset();
        sentenceStates.Clear();
        current = null;
        lastContentAt = null;
        context = null;
        // Never reuse keys while canceled translations might still be completing.
    }

    public IReadOnlyList<SegmentUpdate> Update(string snapshot, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        if (current != null && lastContentAt.HasValue)
        {
            var idle = now - lastContentAt.Value;
            if (idle >= TimeSpan.FromSeconds(PauseSeconds) ||
                (current.Words >= Limits.Minimum && idle >= TimeSpan.FromMilliseconds(1500)))
                current.Closed = true;
        }
        var updates = sentences.Update(snapshot, now);
        var result = new List<SegmentUpdate>();
        bool contentChanged = false;
        foreach (var update in updates)
        {
            if (!sentenceStates.TryGetValue(update.Key, out var sentence))
                sentenceStates.Add(update.Key, sentence = new Sentence());
            var words = Words(update.Text);
            sentence.Words = words.Length;
            int cursor = 0;
            foreach (var fragment in sentence.Fragments)
            {
                if (fragment.End == null)
                    Limit(fragment, words.Length);
                int end = Math.Min(fragment.End ?? words.Length, words.Length);
                Change(fragment, words, end, fragment.End.HasValue || update.IsFinal, result, ref contentChanged);
                cursor = Math.Max(cursor, fragment.End ?? words.Length);
            }
            while (cursor < words.Length)
            {
                if (current == null || current.Closed || current.Words >= Limits.Maximum)
                    current = new Paragraph(++nextParagraph);
                var fragment = new Fragment(++nextFragment, cursor, current);
                sentence.Fragments.Add(fragment);
                Limit(fragment, words.Length);
                int end = fragment.End ?? words.Length;
                Change(fragment, words, end, fragment.End.HasValue || update.IsFinal, result, ref contentChanged);
                cursor = end;
            }
            if (Regex.IsMatch(update.Text, "[.!?。！？][\\\"'”’)]*$") &&
                sentence.Fragments.LastOrDefault()?.Paragraph is { } paragraph &&
                paragraph.Words >= Limits.Minimum)
                paragraph.Closed = true;
        }
        if (contentChanged) lastContentAt = now;
        return result.OrderBy(u => u.Key).ToArray();
    }

    private void Limit(Fragment fragment, int totalWords)
    {
        int room = Math.Max(1, Limits.Maximum - (fragment.Paragraph.Words - fragment.Words));
        int limit = Math.Min(FragmentWords, room);
        if (totalWords - fragment.Start >= limit) fragment.End = fragment.Start + limit;
    }

    private void Change(Fragment fragment, string[] words, int end, bool final,
        List<SegmentUpdate> result, ref bool contentChanged)
    {
        string text = fragment.Start < end ? string.Join(" ", words[fragment.Start..end]) : "";
        if (fragment.Text == text && fragment.Final == final) return;
        contentChanged |= fragment.Text != text;
        int count = Math.Max(0, end - fragment.Start);
        fragment.Paragraph.Words += count - fragment.Words;
        fragment.Words = count;
        fragment.Text = text;
        fragment.Final = final;
        if (fragment.Paragraph.Words >= Limits.Maximum) fragment.Paragraph.Closed = true;
        result.Add(new SegmentUpdate(fragment.Key, ++fragment.Revision, text, final)
        { ParagraphKey = fragment.Paragraph.Key });
    }

    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
