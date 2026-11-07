using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.transcript;

public sealed record SegmentUpdate(long Key, int Revision, string Text, bool IsFinal);

/// <summary>Reconciles the rolling Live Captions text window, not translation results.</summary>
public sealed class CaptionSegmenter
{
    private List<SegmentUpdate> visible = new();
    private readonly TimeSpan pauseInterval;
    private DateTimeOffset lastChangedAt;
    private long nextKey;
    private string previous = "";

    public CaptionSegmenter(TimeSpan? pauseInterval = null) =>
        this.pauseInterval = pauseInterval ?? TimeSpan.FromMilliseconds(1200);

    public void Reset() { visible.Clear(); previous = ""; }

    public IReadOnlyList<SegmentUpdate> Update(string snapshot, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        snapshot = Regex.Replace(snapshot, @"\s+", " ").Trim();
        if (snapshot == previous)
        {
            // Some Windows language models omit all punctuation. Seal a stable tail after
            // a pause; a later extension becomes a new paragraph. This is not diarization.
            if (visible.Count > 0 && !visible[^1].IsFinal && now - lastChangedAt >= pauseInterval)
                visible[^1] = visible[^1] with { IsFinal = true };
            return Array.Empty<SegmentUpdate>();
        }
        previous = snapshot;
        lastChangedAt = now;
        if (snapshot.Length == 0) { visible.Clear(); return Array.Empty<SegmentUpdate>(); }
        var parts = Split(snapshot);
        var changes = new List<SegmentUpdate>();
        var next = new List<SegmentUpdate>();
        // A suffix of the previous window is the prefix of the new one.
        // Prefer the earliest equal alignment so repeated sentences remain distinct.
        int offset = -1, best = 0;
        for (int start = 0; start < visible.Count; start++)
        {
            int score = 0;
            for (int j = 0; j < parts.Count && start + j < visible.Count; j++)
            {
                var old = visible[start + j];
                var text = parts[j].Text;
                if (old.Text == text) score += 10;
                else if (j == 0 && old.IsFinal && old.Text.EndsWith(text, StringComparison.Ordinal)) score += 5;
                else if (start + j == visible.Count - 1 && (j > 0 || Similar(old.Text, text))) score += 2;
                else break;
            }
            if (score > best) { best = score; offset = start; }
        }
        if (offset < 0 && visible.Count > 0 && !visible[^1].IsFinal)
            offset = visible.Count - 1;

        bool matched = offset >= 0;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            SegmentUpdate? old = matched && offset + i < visible.Count ? visible[offset + i] : null;
            if (old != null && old.IsFinal && old.Text != part.Text)
            {
                if (i == 0 && old.Text.EndsWith(part.Text, StringComparison.Ordinal))
                    part = (old.Text, old.IsFinal); // leading text scrolled out of Windows' window
                else if (offset + i != visible.Count - 1 || (i == 0 && !Similar(old.Text, part.Text)))
                {
                    old = null;
                    matched = false;
                }
            }
            var item = old == null
                ? new SegmentUpdate(++nextKey, 1, part.Text, part.Final)
                : old.Text == part.Text && old.IsFinal == part.Final
                    ? old : old with { Text = part.Text, IsFinal = part.Final, Revision = old.Revision + 1 };
            next.Add(item);
            if (old == null || item != old) changes.Add(item);
        }
        visible = next;
        return changes;
    }

    private static bool Similar(string a, string b)
    {
        if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)) return true;
        var left = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var right = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return left.Intersect(right, StringComparer.OrdinalIgnoreCase).Count() >=
            Math.Max(1, Math.Min(left.Length, right.Length) / 2);
    }

    private static bool IsEnd(char c) => ".!?。！？".Contains(c);

    private List<(string Text, bool Final)> Split(string text)
    {
        var boundaries = new SortedSet<int>();
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsEnd(text[i])) continue;
            if (text[i] == '.' && i > 0 && i + 1 < text.Length &&
                char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) continue;
            if (i + 1 < text.Length && IsEnd(text[i + 1])) continue;
            boundaries.Add(i + 1);
        }

        int searchFrom = 0;
        foreach (var old in visible.Where(v => v.IsFinal && !IsEnd(v.Text[^1])))
        {
            int start = text.IndexOf(old.Text, searchFrom, StringComparison.Ordinal);
            int end = start >= 0 ? start + old.Text.Length : -1;
            if (end < 0 && searchFrom == 0)
            {
                // Windows may have discarded the start of an already sealed paragraph.
                for (int i = 1; i < old.Text.Length; i++)
                {
                    if (old.Text[i - 1] != ' ') continue;
                    if (text.StartsWith(old.Text[i..], StringComparison.Ordinal))
                    {
                        start = 0; end = old.Text.Length - i; break;
                    }
                }
            }
            if (end < 0 || (start > 0 && !char.IsWhiteSpace(text[start - 1]) && !IsEnd(text[start - 1]))) continue;
            if (end < text.Length && !char.IsWhiteSpace(text[end])) continue;
            boundaries.Add(end);
            searchFrom = end;
        }

        var result = new List<(string, bool)>();
        int begin = 0;
        foreach (int end in boundaries)
        {
            var part = text[begin..end].Trim();
            if (part.Length > 0) result.Add((part, true));
            begin = end;
        }
        var tail = text[begin..].Trim();
        if (tail.Length > 0) result.Add((tail, false));
        return result;
    }
}
