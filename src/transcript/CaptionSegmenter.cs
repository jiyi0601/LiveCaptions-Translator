using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.transcript;

public sealed record SegmentUpdate(long Key, int Revision, string Text, bool IsFinal)
{
    public long ParagraphKey { get; init; }
}

/// <summary>Reconciles the rolling Live Captions text window, not translation results.</summary>
public sealed class CaptionSegmenter
{
    private List<SegmentUpdate> visible = new();
    private TimeSpan pauseInterval;
    private DateTimeOffset lastChangedAt;
    private long nextKey;
    private string previous = "";

    public CaptionSegmenter(TimeSpan? pauseInterval = null) =>
        this.pauseInterval = pauseInterval ?? TimeSpan.FromMilliseconds(1200);

    public void SealCurrent()
    {
        if (visible.Count > 0) visible[^1] = visible[^1] with { IsFinal = true };
    }

    public void Reset() { visible.Clear(); previous = ""; }

    public IReadOnlyList<SegmentUpdate> Update(string snapshot, DateTimeOffset? observedAt = null)
    {
        var now = observedAt ?? DateTimeOffset.UtcNow;
        snapshot = Regex.Replace(snapshot, @"\s+", " ").Trim();
        if (snapshot == previous)
        {
            // Some Windows language models omit all punctuation. Seal a stable tail after
            // a pause; a later extension becomes a new translation fragment.
            if (visible.Count > 0 && !visible[^1].IsFinal && now - lastChangedAt >= pauseInterval)
                visible[^1] = visible[^1] with { IsFinal = true };
            return Array.Empty<SegmentUpdate>();
        }
        if (visible.Count > 0 && now - lastChangedAt >= pauseInterval) SealCurrent();
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
                else if (RevisionMatch(old.Text, text)) score += 6;
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
                else if (!RevisionMatch(old.Text, part.Text) &&
                    (offset + i != visible.Count - 1 || (i == 0 && !Similar(old.Text, part.Text))))
                {
                    old = null;
                    matched = false;
                }
            }
            if (old != null && i == 0)
                part = (PreserveRollingPrefix(old.Text, part.Text), part.Final);
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

    private static string PreserveRollingPrefix(string oldText, string text)
    {
        if (text.StartsWith(oldText, StringComparison.Ordinal) || oldText.StartsWith(text, StringComparison.Ordinal))
            return text;
        // The caption window can scroll in the middle of a long unpunctuated sentence.
        // Retain words that scrolled off, while allowing corrections in the visible suffix.
        for (int start = 1; start < oldText.Length; start++)
        {
            if (oldText[start - 1] != ' ') continue;
            var suffix = oldText[start..];
            if (suffix.Length < 8) continue;
            if (text.StartsWith(suffix, StringComparison.Ordinal) &&
                (text.Length == suffix.Length || char.IsWhiteSpace(text[suffix.Length]) || IsEnd(text[suffix.Length])))
                return oldText[..start] + text;
        }
        // The visible suffix may contain a recognition correction, so a full suffix match
        // is not always possible. Anchor its unchanged leading words in the retained text.
        var head = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3).ToArray();
        if (head.Length == 3)
        {
            string anchor = string.Join(" ", head);
            int start = oldText.IndexOf(anchor, StringComparison.Ordinal);
            while (start >= 0)
            {
                if (start > 0 && oldText[start - 1] == ' ')
                    return oldText[..start] + text;
                start = oldText.IndexOf(anchor, start + 1, StringComparison.Ordinal);
            }
        }
        return text;
    }

    // Windows can revise a completed sentence (for example "second" -> "2nd")
    // while older lines scroll away. Match it together with its following sentences.
    private static bool RevisionMatch(string a, string b)
    {
        var left = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var right = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count = Math.Min(left.Length, right.Length), prefix = 0;
        while (prefix < count && string.Equals(left[prefix], right[prefix], StringComparison.OrdinalIgnoreCase)) prefix++;
        return (count >= 3 && prefix >= 3) || (count >= 5 &&
            left.Intersect(right, StringComparer.OrdinalIgnoreCase).Count() >= Math.Ceiling(count * 0.8));
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
