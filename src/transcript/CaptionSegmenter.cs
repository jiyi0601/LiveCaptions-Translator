using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.transcript;

public sealed record SegmentUpdate(long Key, int Revision, string Text, bool IsFinal)
{
    public long ParagraphKey { get; init; }
    public LiveCaptionsTranslator.speech.SpeechInfo? Speech { get; init; }
    public LiveCaptionsTranslator.speech.SpeechDraft? RemainingDraft { get; init; }
}

/// <summary>Tracks word ownership across rolling captions and later punctuation changes.</summary>
public sealed class CaptionSegmenter
{
    private sealed class Segment(long key)
    {
        public long Key { get; } = key;
        public int Revision;
        public string Text = "";
        public bool Final;
    }
    private sealed record Word(string Text, Segment Owner);
    private sealed record Alignment(int Start, int End, int?[] Owners);
    private readonly List<Word> words = new();
    private readonly TimeSpan pauseInterval;
    private DateTimeOffset lastChangedAt;
    private long nextKey;
    private string previous = "";

    public CaptionSegmenter(TimeSpan? pauseInterval = null) =>
        this.pauseInterval = pauseInterval ?? TimeSpan.FromMilliseconds(1200);

    public void SealCurrent()
    {
        if (words.Count > 0) words[^1].Owner.Final = true;
    }

    public void Reset() { words.Clear(); previous = ""; }

    public IReadOnlyList<SegmentUpdate> Update(string snapshot, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        snapshot = Regex.Replace(snapshot, @"\s+", " ").Trim();
        if (now - lastChangedAt >= pauseInterval) SealCurrent();
        if (snapshot == previous) return Array.Empty<SegmentUpdate>();
        previous = snapshot;
        lastChangedAt = now;
        // A cleared source window is a real discontinuity, so identical later speech is new.
        if (snapshot.Length == 0) { words.Clear(); return Array.Empty<SegmentUpdate>(); }

        var incoming = snapshot.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var alignment = Align(incoming);
        int start = alignment?.Start ?? words.Count;
        int alignedEnd = alignment?.End ?? 0;
        var owners = alignment?.Owners ?? new int?[incoming.Length];
        // A completely rewritten unfinished tail is a revision, not a second utterance.
        if (alignment == null && words.Count > 0 && !words[^1].Owner.Final)
        {
            var tail = words[^1].Owner;
            start = words.FindLastIndex(w => w.Owner != tail) + 1;
            for (int j = 0; j < incoming.Length; j++) owners[j] = start;
            alignedEnd = incoming.Length;
        }

        var affected = words.Skip(start).Select(w => w.Owner).ToHashSet();
        var replacement = new List<Word>();
        Segment? last = start > 0 ? words[start - 1].Owner : null;
        string lastWord = start > 0 ? words[start - 1].Text : "";
        for (int j = 0; j < incoming.Length; j++)
        {
            Segment? owner = owners[j] is int index ? words[index].Owner : null;
            if (owner == null && j < alignedEnd)
            {
                // Insertions within known speech belong to an existing fragment.
                // This keeps a newly inserted sentence boundary from shifting every later ID.
                for (int k = j + 1; k < alignedEnd; k++)
                    if (owners[k] is int next) { owner = words[next].Owner; break; }
                owner ??= last;
            }
            if (owner == null)
                owner = last != null && !last.Final && !EndsSentence(lastWord)
                    ? last : new Segment(++nextKey);
            replacement.Add(new(incoming[j], owner));
            affected.Add(owner);
            last = owner;
            lastWord = incoming[j];
        }
        words.RemoveRange(start, words.Count - start);
        words.AddRange(replacement);

        var textByOwner = words.GroupBy(w => w.Owner).ToDictionary(g => g.Key, g => string.Join(" ", g.Select(w => w.Text)));
        var updates = new List<SegmentUpdate>();
        foreach (var owner in affected.OrderBy(s => s.Key))
        {
            string text = textByOwner.GetValueOrDefault(owner, "");
            bool final = owner.Final || (text.Length > 0 && (owner != last || EndsSentence(text)));
            if (owner.Text == text && owner.Final == final) continue;
            owner.Text = text;
            owner.Final = final;
            updates.Add(new(owner.Key, ++owner.Revision, text, final));
        }
        return updates;
    }

    private Alignment? Align(string[] incoming)
    {
        if (words.Count == 0) return null;
        // Only the recent window can overlap. Scrolled-out prefixes remain in their original owner.
        int offset = Math.Max(0, words.Count - Math.Max(512, incoming.Length + 64));
        int m = words.Count - offset, n = incoming.Length;
        var old = words.Skip(offset).Select(w => Normalize(w.Text)).ToArray();
        var current = incoming.Select(Normalize).ToArray();
        int prefix = 0;
        while (prefix < Math.Min(m, n) && old[prefix] == current[prefix]) prefix++;
        var owners = new int?[n];
        if (prefix == Math.Min(m, n))
        {
            for (int j = 0; j < prefix; j++) owners[j] = offset + j;
            return new(offset, prefix, owners);
        }
        // Most ASR updates only change the last few words. Pin a long unchanged prefix
        // so those updates do not allocate a quadratic matrix for the entire window.
        int pinned = prefix >= 6 && prefix >= m / 2 ? prefix : 0;
        for (int j = 0; j < pinned; j++) owners[j] = offset + j;
        old = old[pinned..]; current = current[pinned..];
        m -= pinned; n -= pinned;
        var score = new int[m + 1, n + 1];
        var direction = new byte[m + 1, n + 1];
        if (pinned > 0)
            for (int i = 1; i <= m; i++) { score[i, 0] = -3 * i; direction[i, 0] = 2; }
        for (int j = 1; j <= n; j++) { score[0, j] = -3 * j; direction[0, j] = 3; }
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
            {
                int diagonal = score[i - 1, j - 1] + (old[i - 1] == current[j - 1] ? 5 : -3);
                int deletion = score[i - 1, j] - 3;
                int insertion = score[i, j - 1] - 3;
                score[i, j] = Math.Max(diagonal, Math.Max(deletion, insertion));
                direction[i, j] = score[i, j] == diagonal ? (byte)1 : score[i, j] == deletion ? (byte)2 : (byte)3;
            }
        // Free old prefix = scrolling; free new suffix = genuinely new speech.
        int end = 0;
        for (int j = 1; j <= n; j++)
            if (score[m, j] >= score[m, end]) end = j;
        if (score[m, end] + pinned * 5 <= 0) return null;
        int x = m, y = end, matches = pinned;
        while (y > 0)
        {
            if (direction[x, y] == 1)
            {
                owners[pinned + y - 1] = offset + pinned + x - 1;
                if (old[x - 1] == current[y - 1]) matches++;
                x--; y--;
            }
            else if (direction[x, y] == 2) x--;
            else y--;
        }
        if (matches < Math.Min(3, Math.Min(m - x + pinned, end + pinned))) return null;
        return new(pinned > 0 ? offset : offset + x, end + pinned, owners);
    }

    private static string Normalize(string word) =>
        Regex.Replace(word, @"[^\p{L}\p{Nd}]", "").ToUpperInvariant();
    private static bool EndsSentence(string text) => Regex.IsMatch(text, "[.!?。！？][\\\"'”’)]*$");
}
