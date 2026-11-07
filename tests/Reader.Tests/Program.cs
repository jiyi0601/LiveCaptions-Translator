using System.Globalization;
using System.Text;
using CsvHelper;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.transcript;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "LiveCaptionsReaderTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
var failures = new List<string>();
HistoryStore NewStore(string name) => new(Path.Combine(root, name + ".db"));
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> run)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(20)); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + ex); }
}
TaskCompletionSource<string> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

await Test("Three sentences append in source order and retain their translations", async () =>
{
    var store = NewStore("three");
    var segmenter = new CaptionSegmenter();
    await using var engine = new TranscriptEngine(store, (text, _) => Task.FromResult("译：" + text));
    foreach (var update in segmenter.Update("First sentence. Second sentence. Third sentence."))
        await engine.AcceptAsync(update, "zh-CN", "Test");
    await engine.WhenIdleAsync();
    var rows = await store.ReadBeforeAsync(null, 200);
    Check(rows.Count == 3 && rows[0].Id < rows[2].Id, "Source ordering or count");
    Check(rows.All(r => r.TranslatedText == "译：" + r.SourceText), "Pairing");
});

await Test("Partial recognition and correction update one stable row", async () =>
{
    var store = NewStore("revision");
    var segmenter = new CaptionSegmenter();
    await using var engine = new TranscriptEngine(store, (text, _) => Task.FromResult("译：" + text), draftDelay: TimeSpan.Zero);
    foreach (var snapshot in new[] { "We test the first", "We test the second", "We test the second result." })
        foreach (var update in segmenter.Update(snapshot)) await engine.AcceptAsync(update, "zh-CN", "Test");
    await engine.WhenIdleAsync();
    var rows = await store.ReadBeforeAsync(null, 200);
    Check(rows.Count == 1 && rows[0].SourceText == "We test the second result.", "Revision duplicated");
    Check(rows[0].TranslatedText == "译：" + rows[0].SourceText, "Revised pairing");
});

await Test("Equal text in separate utterances remains separate", async () =>
{
    var segmenter = new CaptionSegmenter();
    var first = segmenter.Update("Yes.").Single();
    var second = segmenter.Update("Yes. Yes.").Single();
    Check(first.Key != second.Key, "Repeated text merged");
    Check(segmenter.Update("Yes. Yes.").Count == 0, "Unchanged snapshot duplicated");
    segmenter.Update("");
    Check(segmenter.Update("Yes.").Single().Key != second.Key, "Cleared caption window merged utterances");
    await Task.CompletedTask;
});

await Test("Rolling caption window preserves identities and complete leading text", async () =>
{
    var segmenter = new CaptionSegmenter();
    var initial = segmenter.Update("This is the first sentence. Another");
    var next = segmenter.Update("first sentence. Another sentence.");
    Check(next.Count == 1 && next[0].Key == initial[1].Key, "Truncated leading history duplicated");
    var shifted = segmenter.Update("Another sentence. Final sentence.");
    Check(shifted.Count == 1 && shifted[0].Text == "Final sentence.", "Window shift duplicate");
    await Task.CompletedTask;
});

await Test("Completed tail correction stays paired with the existing paragraph", async () =>
{
    var segmenter = new CaptionSegmenter();
    var original = segmenter.Update("First sentence. We use model A.");
    var changed = segmenter.Update("First sentence. We use model B.");
    Check(changed.Count == 1 && changed[0].Key == original[1].Key, "Tail revision became a new paragraph");
    await Task.CompletedTask;
});

await Test("Decimal number does not cause an extra paragraph", async () =>
{
    var rows = new CaptionSegmenter().Update("The value is 1.25. Next result.");
    Check(rows.Count == 2 && rows[0].Text.Contains("1.25"), "Decimal split");
    await Task.CompletedTask;
});

await Test("Out-of-order translations update the correct persistent ids", async () =>
{
    var store = NewStore("outoforder");
    var first = Completion(); var second = Completion();
    await using var engine = new TranscriptEngine(store, (text, _) => text == "First." ? first.Task : second.Task, concurrency: 2);
    await engine.AcceptAsync(new(1, 1, "First.", true), "zh-CN", "Test");
    await engine.AcceptAsync(new(2, 1, "Second.", true), "zh-CN", "Test");
    second.SetResult("第二句");
    await Task.Delay(50);
    first.SetResult("第一句");
    await engine.WhenIdleAsync();
    var rows = await store.ReadBeforeAsync(null, 200);
    Check(rows.Count == 2 && rows[0].TranslatedText == "第一句" && rows[1].TranslatedText == "第二句", "Out of order mismatch");
});

await Test("Stale translation cannot overwrite a later recognition revision", async () =>
{
    var store = NewStore("stale"); var old = Completion(); var revised = Completion();
    await using var engine = new TranscriptEngine(store, (text, _) => text == "Old." ? old.Task : revised.Task, concurrency: 2);
    await engine.AcceptAsync(new(1, 1, "Old.", true), "zh-CN", "Test");
    await engine.AcceptAsync(new(1, 2, "Revised.", true), "zh-CN", "Test");
    revised.SetResult("修订结果"); old.SetResult("过期结果");
    await engine.WhenIdleAsync();
    var row = (await store.ReadBeforeAsync(null, 1)).Single();
    Check(row.SourceText == "Revised." && row.TranslatedText == "修订结果", "Stale overwrite");
    Check(row.Version == 5, "Version was not persisted");
});

await Test("Source is durable before a delayed translation finishes", async () =>
{
    var store = NewStore("pending"); var completion = Completion();
    await using var engine = new TranscriptEngine(store, (_, _) => completion.Task);
    await engine.AcceptAsync(new(1, 1, "Keep this English sentence.", true), "zh-CN", "Test");
    var row = (await store.ReadBeforeAsync(null, 1)).Single();
    Check(row.SourceText == "Keep this English sentence." && row.TranslatedText == "", "Source not durable");
    completion.SetResult("保存这句英文。"); await engine.WhenIdleAsync();
});

await Test("Failure and paused mode preserve English", async () =>
{
    var store = NewStore("error");
    await using var engine = new TranscriptEngine(store, (_, _) => Task.FromException<string>(new Exception("offline")));
    await engine.AcceptAsync(new(1, 1, "Failure example.", true), "zh-CN", "Test");
    await engine.WhenIdleAsync();
    await engine.AcceptAsync(new(2, 1, "Paused example.", true), "zh-CN", "Test", paused: true);
    var rows = await store.ReadBeforeAsync(null, 2);
    Check(rows[0].TranslatedText.Contains("[ERROR]") && rows[0].SourceText == "Failure example.", "Error lost source");
    Check(rows[1].TranslatedText == "[Paused]", "Paused state");
});

await Test("Reopening the database recovers the complete bilingual record", async () =>
{
    string path = Path.Combine(root, "reopen.db");
    var original = new HistoryStore(path);
    var entry = await original.CreateAsync("Saved English.", "zh-CN", "Test", "保存的中文。");
    var reopened = new HistoryStore(path);
    var loaded = (await reopened.ReadBeforeAsync(null, 1)).Single();
    Check(entry == loaded, "Reload changed record");
});

await Test("200/100 keyset pagination has no gaps during new inserts", async () =>
{
    var store = NewStore("paging");
    for (int i = 1; i <= 450; i++) await store.CreateAsync("English " + i, "zh-CN", "Test", "中文 " + i);
    var recent = await store.ReadBeforeAsync(null, 200);
    await store.CreateAsync("Newest", "zh-CN", "Test", "最新");
    var earlier = await store.ReadBeforeAsync(recent[0].Id, 100);
    var oldest = await store.ReadBeforeAsync(earlier[0].Id, 100);
    var first = await store.ReadBeforeAsync(oldest[0].Id, 100);
    var all = first.Concat(oldest).Concat(earlier).Concat(recent).ToArray();
    Check(all.Length == 450 && all.Select(r => r.Id).Distinct().Count() == 450, "Pagination duplicate or gap");
    Check(all[0].SourceText == "English 1" && all[^1].SourceText == "English 450", "Pagination order");
});

await Test("CSV preserves Chinese, quotes, line breaks and original columns", async () =>
{
    var store = NewStore("csv");
    await store.CreateAsync("English, with \"quotes\"\nand a new line.", "zh-CN", "Test", "中文，带标点。\n第二行");
    string path = Path.Combine(root, "export.csv");
    await store.ExportAsync(path);
    Check(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "UTF-8 BOM missing");
    using var reader = new StreamReader(path);
    using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
    var data = csv.GetRecords<TranslationHistoryEntry>().ToList();
    Check(data.Count == 1 && data[0].SourceText.Contains("\nand a new line.") && data[0].TranslatedText.Contains("第二行"), "CSV escaping");
    Check(!csv.HeaderRecord!.Contains("Id") && !csv.HeaderRecord!.Contains("Version") && csv.HeaderRecord!.Length == 5, "CSV compatibility");
});

await Test("Legacy history schema and timestamps load without losing data", async () =>
{
    string path = Path.Combine(root, "legacy.db");
    using (var connection = new SqliteConnection("Data Source=" + path))
    {
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = @"CREATE TABLE TranslationHistory (Id INTEGER PRIMARY KEY AUTOINCREMENT,Timestamp TEXT,
            SourceText TEXT,TranslatedText TEXT,TargetLanguage TEXT,ApiUsed TEXT);
            INSERT INTO TranslationHistory VALUES (7,'2025-04-03 10:11:12','Legacy English','旧记录','zh-CN','Google');";
        command.ExecuteNonQuery();
    }
    var store = new HistoryStore(path);
    var row = (await store.ReadBeforeAsync(null, 1)).Single();
    Check(row.Id == 7 && row.Version == 0 && row.TimestampFull == "2025-04-03 10:11:12" && row.TranslatedText == "旧记录", "Legacy migration loss");
});

await Test("A delayed older notification does not replace the reader's latest text", async () =>
{
    var store = NewStore("viewversion");
    var first = await store.CreateAsync("English", "zh-CN", "Test");
    var item = new ReaderEntry(first with { TranslatedText = "最新译文", Version = 5 });
    item.Update(first with { TranslatedText = "过期译文", Version = 3 });
    Check(item.DisplayTranslation == "最新译文", "Reader stale event");
});

await Test("Clearing history never reuses an id held by an outstanding task", async () =>
{
    var store = NewStore("clear");
    var old = await store.CreateAsync("Old", "zh-CN", "Test");
    await store.ClearAsync();
    var current = await store.CreateAsync("New", "zh-CN", "Test");
    await store.UpdateAsync(old with { TranslatedText = "Late result", Version = 3 });
    Check(current.Id > old.Id, "Id reused");
    Check((await store.ReadBeforeAsync(null, 10)).Single().SourceText == "New", "Stale task changed a new record");
});


await Test("Unpunctuated Windows captions split after a recognition pause", async () =>
{
    var store = NewStore("pauses");
    await using var engine = new TranscriptEngine(store, (s, _) => Task.FromResult("译：" + s));
    var parser = new CaptionSegmenter();
    var now = DateTimeOffset.UtcNow;
    foreach (var update in parser.Update("Let us look at the first result", now))
        await engine.AcceptAsync(update, "zh-CN", "Test");
    parser.Update("Let us look at the first result", now.AddSeconds(1.3));
    foreach (var update in parser.Update("Let us look at the first result The difference is clear", now.AddSeconds(1.5)))
        await engine.AcceptAsync(update, "zh-CN", "Test");
    parser.Update("Let us look at the first result The difference is clear", now.AddSeconds(2.8));
    foreach (var update in parser.Update("Let us look at the first result The difference is clear Thank you", now.AddSeconds(3)))
        await engine.AcceptAsync(update, "zh-CN", "Test");
    await engine.WhenIdleAsync();
    var rows = await store.ReadBeforeAsync(null, 10);
    Check(rows.Count == 3 && rows[0].SourceText == "Let us look at the first result" &&
        rows[1].SourceText == "The difference is clear" && rows[2].SourceText == "Thank you", "Pause segmentation lost a paragraph");
});
await Test("Repeated unpunctuated words remain separate after a pause", () =>
{
    var parser = new CaptionSegmenter();
    var now = DateTimeOffset.UtcNow;
    var first = parser.Update("Thank you", now).Single();
    parser.Update("Thank you", now.AddSeconds(2));
    var second = parser.Update("Thank you Thank you", now.AddSeconds(3)).Single();
    Check(first.Key != second.Key && first.Text == second.Text, "Repeated utterance was merged");
    return Task.CompletedTask;
});
await Test("Rolling unpunctuated captions retain the full sealed leading sentence", () =>
{
    var parser = new CaptionSegmenter();
    var now = DateTimeOffset.UtcNow;
    var first = parser.Update("Let us look at the first result", now).Single();
    parser.Update("Let us look at the first result", now.AddSeconds(2));
    var change = parser.Update("the first result The difference is clear", now.AddSeconds(3)).Single();
    Check(change.Key != first.Key && change.Text == "The difference is clear", "Rolling pause boundary lost");
    return Task.CompletedTask;
});


string Speech(int count, int start = 0) => string.Join(" ", Enumerable.Range(start, count).Select(i => "word" + i.ToString("D3")));
Dictionary<long, SegmentUpdate> Capture(ParagraphSegmenter parser, params (string Text, double Seconds)[] snapshots)
{
    var result = new Dictionary<long, SegmentUpdate>();
    var origin = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    foreach (var snapshot in snapshots)
        foreach (var update in parser.Update(snapshot.Text, origin.AddSeconds(snapshot.Seconds)))
            result[update.Key] = update;
    return result;
}
int WordCount(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
IEnumerable<IGrouping<long, SegmentUpdate>> Groups(Dictionary<long, SegmentUpdate> items) =>
    items.Values.OrderBy(i => i.Key).GroupBy(i => i.ParagraphKey);

await Test("Natural paragraphs merge short sentences while translating each fragment", async () =>
{
    var parser = new ParagraphSegmenter();
    string text = "Let us look at the first result. The difference is quite clear. Thank you.";
    var updates = Capture(parser, (text, 0));
    Check(Groups(updates).Count() == 1 && updates.Count >= 3, "Short sentences were not grouped");
    var calls = new List<string>();
    var store = NewStore("paragraph_short");
    await using var engine = new TranscriptEngine(store, (s, _) => { calls.Add(s); return Task.FromResult("译：" + s); });
    foreach (var update in updates.Values) await engine.AcceptAsync(update, "zh-CN", "Test");
    await engine.WhenIdleAsync();
    var rows = await store.ReadParagraphsBeforeAsync(null, 200);
    var view = new ReaderEntry(rows[0]);
    foreach (var row in rows.Skip(1)) view.Update(row);
    Check(rows.All(r => r.GroupId == rows[0].Id) && view.Source == text, "Persistent paragraph pairing");
    Check(calls.Count == updates.Count && !calls.Contains(text), "Entire growing paragraph was retranslated");
});

await Test("One to two second pauses below forty words stay in one paragraph", () =>
{
    var rows = Capture(new ParagraphSegmenter(),
        ("First short thought", 0),
        ("First short thought Second short thought", 2),
        ("First short thought Second short thought Third short thought", 4));
    Check(Groups(rows).Count() == 1 && rows.Count == 3, "Short pauses fragmented the reader");
    return Task.CompletedTask;
});
await Test("Forty word sentence boundary closes a balanced paragraph", () =>
{
    var rows = Capture(new ParagraphSegmenter(), (Speech(40) + ".", 0), (Speech(40) + ". Another sentence.", 1));
    Check(Groups(rows).Count() == 2 && WordCount(string.Join(" ", Groups(rows).First().Select(i => i.Text))) == 40, "Sentence boundary ignored");
    return Task.CompletedTask;
});
await Test("Qualified 1.5 second pause closes a paragraph but 1.49 seconds does not", () =>
{
    string text = Speech(40);
    var before = Capture(new ParagraphSegmenter(), (text, 0), (text + " continuation", 1.49));
    var after = Capture(new ParagraphSegmenter(), (text, 0), (text, 1.5), (text + " continuation", 1.6));
    Check(Groups(before).Count() == 1 && Groups(after).Count() == 2, "Pause threshold");
    return Task.CompletedTask;
});
await Test("Eight second silence starts a new short paragraph without empty placeholder rows", () =>
{
    var parser = new ParagraphSegmenter(pauseSeconds: 8);
    var rows = Capture(parser, ("Short thought", 0), ("Short thought", 8),
        ("Short thought Another thought", 8.1));
    Check(Groups(rows).Count() == 2 && rows.Values.All(r => r.Text.Length > 0), "Long pause grouping");
    var before = Capture(new ParagraphSegmenter(pauseSeconds: 8), ("Short thought", 0), ("Short thought Another thought", 7.9));
    Check(Groups(before).Count() == 1, "Long pause fired too early");
    return Task.CompletedTask;
});
await Test("Continuous unpunctuated speech is bounded at eighty words without loss", () =>
{
    string source = Speech(250);
    var rows = Capture(new ParagraphSegmenter(), (source, 0));
    var sizes = Groups(rows).Select(g => WordCount(string.Join(" ", g.Select(i => i.Text)))).ToArray();
    Check(sizes.SequenceEqual(new[] { 80, 80, 80, 10 }), "Hard paragraph bound");
    Check(string.Join(" ", rows.Values.OrderBy(i => i.Key).Select(i => i.Text)) == source, "Hard split lost or duplicated words");
    return Task.CompletedTask;
});
await Test("Short and long presets apply their own bounds and survive serialization", () =>
{
    foreach (var (preset, limit) in new[] { (ParagraphLength.Short, 60), (ParagraphLength.Long, 120) })
    {
        var rows = Capture(new ParagraphSegmenter(preset), (Speech(limit + 10), 0));
        Check(Groups(rows).Select(g => g.Sum(i => WordCount(i.Text))).SequenceEqual(new[] { limit, 10 }), "Preset bounds");
        var setting = new ReaderWindowState { ParagraphLength = preset };
        var json = System.Text.Json.JsonSerializer.Serialize(setting);
        Check(System.Text.Json.JsonSerializer.Deserialize<ReaderWindowState>(json)!.ParagraphLength == preset, "Setting persistence");
    }
    Check(System.Text.Json.JsonSerializer.Deserialize<ReaderWindowState>("{}")!.ParagraphLength == ParagraphLength.Balanced, "Old setting default");
    return Task.CompletedTask;
});
await Test("Recognition corrections keep fragment keys and paragraph assignments stable", () =>
{
    var parser = new ParagraphSegmenter();
    var now = DateTimeOffset.UtcNow;
    var initial = parser.Update(Speech(100), now).ToDictionary(i => i.Key);
    string revised = Speech(100).Replace("word003", "corrected003").Replace("word095", "corrected095");
    var updates = parser.Update(revised, now.AddMilliseconds(100));
    Check(updates.Count == 2 && updates.All(u => initial.ContainsKey(u.Key) && initial[u.Key].ParagraphKey == u.ParagraphKey), "Correction regrouped old records");
    return Task.CompletedTask;
});
await Test("Rolling drafts retain scrolled prefixes even when their visible tail is corrected", () =>
{
    var rows = Capture(new ParagraphSegmenter(),
        (Speech(100), 0),
        (Speech(100, 30).Replace("word095", "corrected095"), 0.5),
        (Speech(100, 60).Replace("word095", "corrected095"), 1));
    string expected = Speech(160).Replace("word095", "corrected095");
    Check(string.Join(" ", rows.Values.OrderBy(i => i.Key).Select(i => i.Text)) == expected, "Rolling revision lost the leading words");
    return Task.CompletedTask;
});
await Test("Repeated utterances remain present within and across natural paragraphs", () =>
{
    var rows = Capture(new ParagraphSegmenter(), ("Thank you", 0), ("Thank you Thank you", 2),
        ("Thank you Thank you Thank you", 11));
    Check(rows.Count == 3 && Groups(rows).Count() == 2 && rows.Values.All(i => i.Text == "Thank you"), "Repeated text lost");
    return Task.CompletedTask;
});
await Test("Only the growing translation fragment changes when more words arrive", () =>
{
    var parser = new ParagraphSegmenter();
    var now = DateTimeOffset.UtcNow;
    var first = parser.Update(Speech(25), now);
    var next = parser.Update(Speech(29), now.AddMilliseconds(100));
    Check(first.Count == 2 && next.Count == 1 && next[0].Key == first[1].Key, "Stable fragment was resubmitted");
    Check(parser.Update(Speech(29), now.AddMilliseconds(200)).Count == 0, "Unchanged text resubmitted");
    return Task.CompletedTask;
});
await Test("Service changes start a new paragraph without duplicating the earlier source", () =>
{
    var parser = new ParagraphSegmenter();
    parser.SetContext("zh-CN|Google");
    var now = DateTimeOffset.UtcNow;
    var first = parser.Update("Original words", now);
    parser.SetContext("ja|DeepL");
    var next = parser.Update("Original words new language words", now.AddMilliseconds(100));
    Check(first.Count == 1 && next.Count == 1 && first[0].ParagraphKey != next[0].ParagraphKey, "Service change reused paragraph");
    Check(next[0].Text == "new language words", "Service change duplicated source");
    return Task.CompletedTask;
});
await Test("Paragraph view retains successful translations beside failures and late revisions", async () =>
{
    var store = NewStore("paragraph_partial");
    var first = await store.CreateAsync("First sentence.", "zh-CN", "Test", "第一句。");
    var failed = await store.CreateAsync("Second sentence.", "zh-CN", "Test", "[ERROR] offline", first.GroupId);
    var pending = await store.CreateAsync("Third sentence.", "zh-CN", "Test", paragraphId: first.GroupId);
    var view = new ReaderEntry(first);
    view.Update(failed); view.Update(pending);
    Check(view.DisplayTranslation.Contains("第一句") && view.Status.Contains("部分内容翻译失败") && view.Status.Contains("补充"), "A failing part blanked the whole translation");
    view.Update(pending with { Version = 3, TranslatedText = "第三句。" });
    view.Update(pending);
    Check(view.DisplayTranslation.Contains("第三句") && view.CopyText.Contains("Second sentence."), "Late notification or copy lost content");
});
await Test("Out-of-order results stay in their original paragraph and preserve job language", async () =>
{
    var store = NewStore("paragraph_oo");
    var firstDone = Completion(); var secondDone = Completion();
    var seen = new List<string>();
    await using var engine = new TranscriptEngine(store, (_, _) => Task.FromResult("unused"), concurrency: 2,
        entryTranslator: (entry, _) => { seen.Add(entry.TargetLanguage + "|" + entry.ApiUsed); return entry.SourceText == "First." ? firstDone.Task : secondDone.Task; });
    await engine.AcceptAsync(new(1, 1, "First.", true) { ParagraphKey = 1 }, "zh-CN", "Google");
    await engine.AcceptAsync(new(2, 1, "Second.", true) { ParagraphKey = 2 }, "ja", "DeepL");
    secondDone.SetResult("二番目"); firstDone.SetResult("第一句");
    await engine.WhenIdleAsync();
    var rows = await store.ReadParagraphsBeforeAsync(null, 10);
    Check(rows.Select(r => r.GroupId).Distinct().Count() == 2 && rows[0].TranslatedText == "第一句" && rows[1].TranslatedText == "二番目", "Paragraph mismatch");
    Check(seen.SequenceEqual(new[] { "zh-CN|Google", "ja|DeepL" }), "Translation job metadata changed");
});
await Test("Paragraph cursor pagination returns complete groups during new fragment inserts", async () =>
{
    var store = NewStore("paragraph_pages");
    for (int i = 1; i <= 215; i++)
    {
        var first = await store.CreateAsync("Paragraph " + i + " first", "zh-CN", "Test", "译一");
        await store.CreateAsync("second", "zh-CN", "Test", "译二", first.GroupId);
    }
    var recent = await store.ReadParagraphsBeforeAsync(null, 200);
    var currentId = recent.Last().GroupId;
    await store.CreateAsync("third", "zh-CN", "Test", "译三", currentId);
    var earlier = await store.ReadParagraphsBeforeAsync(recent[0].GroupId, 100);
    Check(recent.Count == 400 && recent.Select(r => r.GroupId).Distinct().Count() == 200, "Page counted fragments");
    Check(earlier.Count == 30 && earlier.Select(r => r.GroupId).Distinct().Count() == 15, "Earlier groups missing");
    Check(!recent.Select(r => r.GroupId).Intersect(earlier.Select(r => r.GroupId)).Any(), "Page overlap");
    Check((await store.ReadParagraphAsync(currentId)).Count == 3, "New fragment lost");
});
await Test("Paragraph export uses original columns and restart recovers group ids", async () =>
{
    string path = Path.Combine(root, "paragraph_export.db");
    var store = new HistoryStore(path);
    var a = await store.CreateAsync("First, \"quoted\" sentence.", "zh-CN", "Test", "第一句。");
    await store.CreateAsync("Second sentence.", "zh-CN", "Test", "第二句。", a.GroupId);
    await store.CreateAsync("Next paragraph.", "zh-CN", "Test", "下一段。");
    store = new HistoryStore(path);
    var rows = await store.ReadParagraphsBeforeAsync(null, 10);
    Check(rows.Count == 3 && rows[0].GroupId == rows[1].GroupId && rows[2].GroupId != rows[0].GroupId, "Restart regrouped records");
    string csvPath = Path.Combine(root, "paragraphs.csv");
    await store.ExportAsync(csvPath);
    using var input = new StreamReader(csvPath);
    using var csv = new CsvReader(input, CultureInfo.InvariantCulture);
    var exported = csv.GetRecords<TranslationHistoryEntry>().ToList();
    Check(exported.Count == 2 && exported[0].SourceText.Contains("Second sentence.") && exported[0].TranslatedText == "第一句。 第二句。", "CSV grouping");
    Check(csv.HeaderRecord!.Length == 5, "CSV columns changed");
});
await Test("Old history rows remain independent after the paragraph migration", async () =>
{
    string path = Path.Combine(root, "legacy_paragraphs.db");
    using (var connection = new SqliteConnection("Data Source=" + path))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"CREATE TABLE TranslationHistory (Id INTEGER PRIMARY KEY AUTOINCREMENT,Timestamp TEXT,
            SourceText TEXT,TranslatedText TEXT,TargetLanguage TEXT,ApiUsed TEXT,Version INTEGER DEFAULT 0);
            INSERT INTO TranslationHistory VALUES (1,'2025-01-01 10:00:00','Old first','旧一','zh-CN','Test',0),
            (2,'2025-01-01 10:00:01','Old second','旧二','zh-CN','Test',0);";
        command.ExecuteNonQuery();
    }
    var rows = await new HistoryStore(path).ReadParagraphsBeforeAsync(null, 10);
    Check(rows.Count == 2 && rows[0].GroupId == 1 && rows[1].GroupId == 2 && rows.All(r => r.Version == 0), "Old history was merged");
});
await Test("Rolling window revisions of earlier completed sentences do not duplicate speech", () =>
{
    const string a = "Today we are reviewing the results from our recent experiment.";
    const string b = "The first measurement shows a clear improvement while the second measurement remains stable.";
    const string c = "We should compare these observations with the earlier data before making a final decision.";
    const string d = "Our next step is to repeat the measurement with a fresh sample";
    var parser = new ParagraphSegmenter();
    var rows = Capture(parser, (a + " " + b + " " + c + " " + d, 0),
        (b.Replace("second", "2nd") + " " + c + " " + d + " and record the result.", 1));
    string actual = string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text));
    string expected = a + " " + b.Replace("second", "2nd") + " " + c + " " + d + " and record the result.";
    Check(actual == expected, "Earlier sentence correction was duplicated or lost: " + actual);
    return Task.CompletedTask;
});
await Test("Recorded synthetic speech snapshots retain every recognized word without duplicates", () =>
{
    using var trace = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "long-speech.json")));
    var snapshots = trace.RootElement.EnumerateArray().Select(s => (s.GetProperty("Text").GetString()!, s.GetProperty("Seconds").GetDouble())).ToArray();
    var rows = Capture(new ParagraphSegmenter(), snapshots);
    string actual = string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text).Where(t => t.Length > 0));
    string expected = System.Text.RegularExpressions.Regex.Replace(snapshots[^1].Item1, @"\s+", " ").Trim();
    Check(actual == expected, "Captured speech lost or duplicated recognized words");
    Check(Groups(rows).All(g => WordCount(string.Join(" ", g.Select(p => p.Text))) <= 80), "Captured speech exceeded paragraph limit");
    return Task.CompletedTask;
});

await Test("Late punctuation and apostrophe revisions do not repeat coffee or pizza", () =>
{
    string draft = "Delicious So tell me about something delicious from your weekend The green curry I ordered was delicious My moms roast chicken was delicious and my pizza was delicious My coffee was delicious It's a very useful word";
    string final = "Delicious. So tell me about something delicious from your weekend. The green curry I ordered was delicious. My mom's roast chicken was delicious and my pizza was delicious. My coffee was delicious. It's a very useful word. It is okay next word.";
    var rows = Capture(new ParagraphSegmenter(), (draft, 0), (final, 2));
    Check(string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text).Where(t => t.Length > 0)) == final, "Punctuation revisions duplicated or lost speech");
    return Task.CompletedTask;
});
await Test("Changing sentence boundaries in a rolling window preserves each occurrence", () =>
{
    const string a = "My mom's roast chicken was delicious and my pizza was delicious";
    const string b = "My coffee was delicious";
    const string c = "It's a very useful word";
    var rows = Capture(new ParagraphSegmenter(),
        (a + " " + b + " " + c, 0),
        (a + ". " + b + ". " + c + ". It", 0.5),
        (b + ". " + c + ". It is okay next word. We talked about a special meal.", 1),
        (c + ". It is okay next word. We talked about a special meal. Brunch brunch BRUNCH.", 1.5));
    string expected = a + ". " + b + ". " + c + ". It is okay next word. We talked about a special meal. Brunch brunch BRUNCH.";
    Check(string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text).Where(t => t.Length > 0)) == expected, "Rolling punctuation shifted segment ownership");
    return Task.CompletedTask;
});


await Test("Every pause preset uses its exact threshold and persists safely", () =>
{
    foreach (int seconds in ParagraphSegmenter.PauseOptions)
    {
        var before = Capture(new ParagraphSegmenter(pauseSeconds: seconds),
            ("Short thought", 0), ("Short thought Another thought", seconds - 0.1));
        var after = Capture(new ParagraphSegmenter(pauseSeconds: seconds),
            ("Short thought", 0), ("Short thought Another thought", seconds));
        Check(Groups(before).Count() == 1 && Groups(after).Count() == 2, "Wrong pause threshold: " + seconds);
        var setting = new ReaderWindowState { ParagraphPauseSeconds = seconds };
        string json = System.Text.Json.JsonSerializer.Serialize(setting);
        Check(System.Text.Json.JsonSerializer.Deserialize<ReaderWindowState>(json)!.ParagraphPauseSeconds == seconds, "Pause was not saved");
    }
    Check(System.Text.Json.JsonSerializer.Deserialize<ReaderWindowState>("{}")!.ParagraphPauseSeconds == 3, "Upgrade default is not three seconds");
    foreach (int invalid in new[] { -1, 0, 100 })
        Check(new ReaderWindowState { ParagraphPauseSeconds = invalid }.ParagraphPauseSeconds == 3, "Invalid saved pause was not normalized");
    return Task.CompletedTask;
});
await Test("Changing the pause preset takes effect without clearing existing speech", () =>
{
    var parser = new ParagraphSegmenter(pauseSeconds: 8);
    var now = DateTimeOffset.UtcNow;
    var first = parser.Update("First short thought", now).Single();
    parser.SetPauseSeconds(2);
    var next = parser.Update("First short thought Another thought", now.AddSeconds(2.1)).Single();
    Check(first.Key != next.Key && first.ParagraphKey != next.ParagraphKey && next.Text == "Another thought", "Changing pause lost or duplicated the prefix");
    return Task.CompletedTask;
});
await Test("Word insertions deletions and punctuation changes preserve source order", () =>
{
    var rows = Capture(new ParagraphSegmenter(),
        ("My moms roast chicken was really very delicious and my pizza was delicious My coffee was delicious", 0),
        ("My mom's roast chicken was very delicious. And my fresh pizza was delicious. My coffee was delicious.", 0.4),
        ("My mom's roast chicken was delicious and my fresh pizza was delicious My coffee was delicious", 0.8));
    const string expected = "My mom's roast chicken was delicious and my fresh pizza was delicious My coffee was delicious";
    Check(string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text).Where(t => t.Length > 0)) == expected, "Edit changed word ownership incorrectly");
    return Task.CompletedTask;
});
await Test("Explicit repeated pizza and coffee utterances are retained", () =>
{
    const string repeated = "My pizza was delicious. My pizza was delicious. My coffee was delicious. My coffee was delicious.";
    var rows = Capture(new ParagraphSegmenter(),
        ("My pizza was delicious", 0), ("My pizza was delicious My pizza was delicious My coffee was delicious", 0.5),
        (repeated, 1));
    Check(string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text).Where(t => t.Length > 0)) == repeated, "Real repeated speech was deduplicated");
    return Task.CompletedTask;
});
await Test("Late sentence boundaries after a length split never move or duplicate the words", () =>
{
    var parser = new ParagraphSegmenter();
    var now = DateTimeOffset.UtcNow;
    var rows = parser.Update(Speech(130), now).ToDictionary(u => u.Key);
    var groups = rows.ToDictionary(p => p.Key, p => p.Value.ParagraphKey);
    string corrected = string.Join(" ", Speech(130).Split(' ').Select((w, i) => (i + 1) % 7 == 0 ? w + "." : w));
    foreach (var update in parser.Update(corrected, now.AddMilliseconds(200)))
    {
        Check(groups[update.Key] == update.ParagraphKey, "Punctuation moved an existing fragment");
        rows[update.Key] = update;
    }
    Check(string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text)) == corrected, "Punctuation duplicated words across paragraph boundary");
    return Task.CompletedTask;
});
await Test("Unrelated completed windows append instead of overwriting past speech", () =>
{
    var rows = Capture(new ParagraphSegmenter(),
        ("My cat is very quiet and my coffee was delicious.", 0),
        ("The car is very fast and the pizza looks great.", 4));
    Check(string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text)) ==
        "My cat is very quiet and my coffee was delicious. The car is very fast and the pizza looks great.", "Unrelated words falsely matched");
    return Task.CompletedTask;
});


await Test("Repeated vocabulary survives many overlapping rolling windows", () =>
{
    string full = string.Join(" ", Enumerable.Range(0, 36).Select(i =>
        $"Topic{i} the coffee was delicious and the pizza was delicious"));
    var tokens = full.Split(' ');
    var snapshots = new List<(string, double)>();
    for (int end = 4; end < tokens.Length; end += 4)
    {
        int start = Math.Max(0, end - 65);
        string view = string.Join(" ", tokens[start..end].Select((w, i) =>
            (start + i) % 11 == 10 ? w + "." : w));
        snapshots.Add((view, end * 0.02));
    }
    snapshots.Add((string.Join(" ", tokens[^65..]), tokens.Length * 0.02));
    var rows = Capture(new ParagraphSegmenter(), snapshots.ToArray());
    string actual = string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text).Where(t => t.Length > 0));
    Check(actual.Replace(".", "") == full, "Overlapping repeated vocabulary duplicated or omitted speech");
    return Task.CompletedTask;
});


await Test("Recorded delicious speech remains identical to raw captions at two and three second pauses", () =>
{
    using var trace = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "delicious-speech.json")));
    var snapshots = trace.RootElement.EnumerateArray().Select(s => (s.GetProperty("Text").GetString()!, s.GetProperty("Seconds").GetDouble())).ToArray();
    string expected = System.Text.RegularExpressions.Regex.Replace(snapshots[^1].Item1, @"\s+", " ").Trim();
    foreach (int pause in new[] { 2, 3 })
    {
        var rows = Capture(new ParagraphSegmenter(pauseSeconds: pause), snapshots);
        string actual = string.Join(" ", rows.Values.OrderBy(r => r.Key).Select(r => r.Text).Where(t => t.Length > 0));
        Check(actual == expected, "Delicious speech differs from the source caption");
        Check(Groups(rows).Count() == (pause == 2 ? 3 : 2), "Captured pause setting was not applied");
    }
    return Task.CompletedTask;
});


await Test("Speaker change splits a paragraph even below the minimum length", async () =>
{
    var a = new LiveCaptionsTranslator.speech.SpeakerParagraphAssembler();
    var words = new[] { new LiveCaptionsTranslator.speech.TimedWord(0, "Hello", 0, .5), new LiveCaptionsTranslator.speech.TimedWord(1, "Welcome", .6, 1) };
    var spans = new[] { new LiveCaptionsTranslator.speech.SpeakerSpan(0, .55, "A", LiveCaptionsTranslator.speech.SpeakerState.Identified),
        new LiveCaptionsTranslator.speech.SpeakerSpan(.55, 2, "B", LiveCaptionsTranslator.speech.SpeakerState.Identified) };
    var updates = a.Assemble("session", words, spans, "zh", "Test", "");
    Check(updates.Count == 2 && updates[0].ParagraphKey != updates[1].ParagraphKey, "Speakers merged");
    var store = NewStore("speaker-boundary");
    await using var engine = new TranscriptEngine(store, (text, _) => Task.FromResult(text));
    foreach (var update in updates) await engine.AcceptAsync(update, "zh", "Test");
    await engine.WhenIdleAsync();
    var rows = await store.ReadBeforeAsync(null, 10);
    Check(rows[0].Speech!.SpeakerId == "A" && rows[1].Speech!.SpeakerId == "B" && rows[0].GroupId != rows[1].GroupId, "Speaker metadata or grouping");
});
await Test("Same speaker continues the paragraph across translation fragments", () =>
{
    var a = new LiveCaptionsTranslator.speech.SpeakerParagraphAssembler();
    var span = new[] { new LiveCaptionsTranslator.speech.SpeakerSpan(0, 20, "A", LiveCaptionsTranslator.speech.SpeakerState.Identified) };
    var one = a.Assemble("s", new[] { new LiveCaptionsTranslator.speech.TimedWord(0, "First", 0, 1) }, span, "zh", "api", "");
    var two = a.Assemble("s", new[] { new LiveCaptionsTranslator.speech.TimedWord(1, "Second", 2, 3) }, span, "zh", "api", "");
    Check(one[0].ParagraphKey == two[0].ParagraphKey, "Short pause split");
    return Task.CompletedTask;
});
await Test("Speaker paragraphs preserve the hard word cap and repeated words", () =>
{
    var a = new LiveCaptionsTranslator.speech.SpeakerParagraphAssembler { Length = ParagraphLength.Short };
    var words = Enumerable.Range(0, 145).Select(i => new LiveCaptionsTranslator.speech.TimedWord(i, "again", i * .2, i * .2 + .1)).ToArray();
    var spans = new[] { new LiveCaptionsTranslator.speech.SpeakerSpan(0, 40, "A", LiveCaptionsTranslator.speech.SpeakerState.Identified) };
    var result = a.Assemble("s", words, spans, "zh", "api", "");
    Check(result.Sum(r => r.Text.Split(' ').Length) == 145, "Repeated genuine speech removed");
    Check(result.GroupBy(r => r.ParagraphKey).All(g => g.Sum(r => r.Text.Split(' ').Length) <= 60), "Word cap");
    return Task.CompletedTask;
});
await Test("Long pauses and translation option changes close speaker paragraphs", () =>
{
    var a = new LiveCaptionsTranslator.speech.SpeakerParagraphAssembler { PauseSeconds = 2 };
    var spans = new[] { new LiveCaptionsTranslator.speech.SpeakerSpan(0, 20, "A", LiveCaptionsTranslator.speech.SpeakerState.Identified) };
    var p1 = a.Assemble("s", new[] { new LiveCaptionsTranslator.speech.TimedWord(0, "One", 0, .5) }, spans, "zh", "api", "");
    var p2 = a.Assemble("s", new[] { new LiveCaptionsTranslator.speech.TimedWord(1, "Two", 3, 3.5) }, spans, "zh", "api", "");
    var p3 = a.Assemble("s", new[] { new LiveCaptionsTranslator.speech.TimedWord(2, "Three", 4, 4.5) }, spans, "ja", "api", "");
    Check(p1[0].ParagraphKey != p2[0].ParagraphKey && p2[0].ParagraphKey != p3[0].ParagraphKey, "Boundary ignored");
    return Task.CompletedTask;
});
await Test("Overlapping voices are not forced to a named speaker", () =>
{
    var spans = new[] { new LiveCaptionsTranslator.speech.SpeakerSpan(0, 2, "A", LiveCaptionsTranslator.speech.SpeakerState.Identified),
        new LiveCaptionsTranslator.speech.SpeakerSpan(1, 3, "B", LiveCaptionsTranslator.speech.SpeakerState.Identified) };
    var who = LiveCaptionsTranslator.speech.SpeakerLabels.ForWord(new(0, "yes", 1.2, 1.4), spans);
    Check(who.State == LiveCaptionsTranslator.speech.SpeakerState.Overlap && who.SpeakerId == null, "Overlap misattributed");
    var unknown = LiveCaptionsTranslator.speech.SpeakerLabels.ForWord(new(1, "no", 4, 4.1), spans);
    Check(unknown.State == LiveCaptionsTranslator.speech.SpeakerState.Unknown, "Missing evidence guessed");
    return Task.CompletedTask;
});
await Test("Voice registry keeps returning the same identity and rejects ambiguous samples", () =>
{
    var registry = new LiveCaptionsTranslator.speech.SpeakerRegistry();
    Check(registry.Match(new float[] { 1, 0, 0 }) == "A", "First speaker");
    Check(registry.Match(new float[] { 0, 1, 0 }) == "B", "Second speaker");
    Check(registry.Match(new float[] { .99f, .02f, 0 }) == "A", "Returning speaker changed");
    Check(registry.Match(new float[] { 1, 1, 0 }) == null, "Ambiguous identity forced");
    Check(registry.Match(new float[] { 0, 0, 1 }, false) == null && registry.Count == 2, "Short sample enrolled");
    Check(LiveCaptionsTranslator.speech.SpeakerLabels.At(26) == "AA", "Label overflow");
    return Task.CompletedTask;
});
await Test("Audio ring retains the exact latest samples with bounded storage", () =>
{
    var ring = new LiveCaptionsTranslator.speech.AudioRing(1);
    ring.Add(Enumerable.Range(0, 50000).Select(x => (float)x).ToArray());
    var retained = ring.Read(0, 50000);
    Check(retained.Length == 16000 && retained[0] == 34000 && retained[^1] == 49999, "Ring sample ownership");
    Check(ring.Read(49990, 50000).SequenceEqual(Enumerable.Range(49990, 10).Select(x => (float)x)), "Ring tail");
    return Task.CompletedTask;
});
await Test("Speaker commit and draft removal are atomic and idempotent", async () =>
{
    var store = NewStore("speaker-draft");
    var draft = new LiveCaptionsTranslator.speech.SpeechDraft("s", "First second", "zh", "api", 0, 2);
    await store.SaveDraftAsync(draft);
    var speech = new LiveCaptionsTranslator.speech.SpeechInfo("s", "word-0", "A", LiveCaptionsTranslator.speech.SpeakerState.Identified, 0, 1);
    var created = await store.CreateAsync("First", "zh", "api", speech: speech, remainingDraft: draft with { Text = "second" });
    var repeated = await store.CreateAsync("First", "zh", "api", speech: speech, remainingDraft: draft);
    Check(created.Id == repeated.Id && (await store.ReadBeforeAsync(null, 20)).Count == 1, "Replay duplicated");
    Check((await store.ReadDraftsAsync()).Single().Text == "second", "Replay regressed draft watermark");
    await store.CreateAsync("second", "zh", "api", speech: speech with { SourceKey = "word-1", Start = 1, End = 2 },
        remainingDraft: draft with { Text = "" });
    Check((await store.ReadDraftsAsync()).Count == 0, "Committed draft survived");
});
await Test("Draft recovery survives reopen without resurrecting committed text", async () =>
{
    string path = Path.Combine(root, "draft-reopen.db");
    var first = new HistoryStore(path);
    var draft = new LiveCaptionsTranslator.speech.SpeechDraft("crash", "Unfinished words", "zh", "api", 5, 6);
    await first.SaveDraftAsync(draft);
    var reopened = new HistoryStore(path);
    var recovered = (await reopened.ReadDraftsAsync()).Single();
    var speech = new LiveCaptionsTranslator.speech.SpeechInfo("crash", "recovered", null, LiveCaptionsTranslator.speech.SpeakerState.Unknown, 5, 6);
    await reopened.CreateAsync(recovered.Text, recovered.Language, recovered.Api, speech: speech, remainingDraft: recovered with { Text = "" });
    Check((await new HistoryStore(path).ReadDraftsAsync()).Count == 0, "Recovery was not durable");
    var row = (await reopened.ReadBeforeAsync(null, 10)).Single();
    Check(row.SourceText == draft.Text && row.SpeakerLabel == "未确定", "Recovery falsely assigned voice");
});
await Test("Session namespaces preserve identical repeated utterances", async () =>
{
    var store = NewStore("speaker-sessions");
    var speech = new LiveCaptionsTranslator.speech.SpeechInfo("meeting1", "0", "A", LiveCaptionsTranslator.speech.SpeakerState.Identified, 0, 1);
    await store.CreateAsync("Thank you", "zh", "api", speech: speech);
    await store.CreateAsync("Thank you", "zh", "api", speech: speech with { SourceKey = "1" });
    await store.CreateAsync("Thank you", "zh", "api", speech: speech with { SessionId = "meeting2" });
    Check((await store.ReadBeforeAsync(null, 10)).Count == 3, "Repeated speech or new session collapsed");
});
await Test("Speaker metadata survives late translations and historical paging", async () =>
{
    var store = NewStore("speaker-late"); var a = Completion(); var b = Completion();
    await using var engine = new TranscriptEngine(store, (text, _) => text == "Alpha" ? a.Task : b.Task, concurrency: 2);
    var info = new LiveCaptionsTranslator.speech.SpeechInfo("s", "0", "A", LiveCaptionsTranslator.speech.SpeakerState.Identified, 0, 1);
    await engine.AcceptAsync(new(-10, 1, "Alpha", true) { Speech = info }, "zh", "api");
    await engine.AcceptAsync(new(-11, 1, "Beta", true) { Speech = info with { SourceKey = "1", SpeakerId = "B" } }, "zh", "api");
    b.SetResult("乙"); a.SetResult("甲"); await engine.WhenIdleAsync();
    var rows = await store.ReadParagraphsBeforeAsync(null, 1);
    Check(rows.Single().SpeakerLabel == "说话人 B" && rows[0].TranslatedText == "乙", "Late speaker mismatch");
    var earlier = await store.ReadParagraphsBeforeAsync(rows[0].GroupId, 1);
    Check(earlier.Single().SpeakerLabel == "说话人 A", "Paging metadata lost");
});
await Test("Speaker copy and both CSV formats preserve their contracts", async () =>
{
    var store = NewStore("speaker-csv");
    var info = new LiveCaptionsTranslator.speech.SpeechInfo("s", "0", "A", LiveCaptionsTranslator.speech.SpeakerState.Identified, 0, 1);
    var row = await store.CreateAsync("Hello", "zh", "api", "你好", speech: info);
    Check(new ReaderEntry(row).CopyText.Contains("说话人 A"), "Copy omitted speaker");
    string legacy = Path.Combine(root, "legacy.csv"), extended = Path.Combine(root, "speaker.csv");
    await store.ExportAsync(legacy); await store.ExportAsync(extended, true);
    Check(File.ReadLines(legacy).First() == "Timestamp,SourceText,TranslatedText,TargetLanguage,ApiUsed", "Legacy CSV changed");
    Check(File.ReadLines(extended).First().EndsWith(",SessionId,Speaker") && File.ReadAllText(extended).Contains("说话人 A"), "Extended CSV");
});
await Test("Speaker settings roundtrip with a disabled default", () =>
{
    var saved = new LiveCaptionsTranslator.models.ReaderWindowState();
    Check(!saved.SpeakerEnabled && saved.SpeakerDeviceId == "", "Unexpected capture default");
    saved.SpeakerEnabled = true; saved.SpeakerDeviceId = "device-test";
    var json = System.Text.Json.JsonSerializer.Serialize(saved);
    var read = System.Text.Json.JsonSerializer.Deserialize<LiveCaptionsTranslator.models.ReaderWindowState>(json)!;
    Check(read.SpeakerEnabled && read.SpeakerDeviceId == "device-test", "Settings lost");
    return Task.CompletedTask;
});


await Test("A medium-length speaker paragraph splits at a short audio pause within one batch", () =>
{
    var assembler = new LiveCaptionsTranslator.speech.SpeakerParagraphAssembler();
    var first = Enumerable.Range(0, 39).Select(i => new LiveCaptionsTranslator.speech.TimedWord(i, "word", i * .2, i * .2 + .1)).ToArray();
    var spans = new[] { new LiveCaptionsTranslator.speech.SpeakerSpan(0, 20, "A", LiveCaptionsTranslator.speech.SpeakerState.Identified) };
    var a = assembler.Assemble("s", first, spans, "zh", "test", "");
    var b = assembler.Assemble("s", new[] {
        new LiveCaptionsTranslator.speech.TimedWord(39, "forty", 7.8, 7.9),
        new LiveCaptionsTranslator.speech.TimedWord(40, "next", 9.5, 9.6)
    }, spans, "zh", "test", "");
    Check(b.Count == 2 && b[0].ParagraphKey == a[^1].ParagraphKey && b[1].ParagraphKey != b[0].ParagraphKey, "In-batch pause merged");
    return Task.CompletedTask;
});
await Test("Punctuation cannot insert, remove or change recognized words", () =>
{
    var assembler = new LiveCaptionsTranslator.speech.SpeakerParagraphAssembler();
    var words = new[] { new LiveCaptionsTranslator.speech.TimedWord(0, "hello", 0, 1),
        new LiveCaptionsTranslator.speech.TimedWord(1, "again", 1, 2) };
    var updates = assembler.Assemble("s", words, Array.Empty<LiveCaptionsTranslator.speech.SpeakerSpan>(),
        "zh", "test", "pending words", _ => "A hallucinated replacement.", pendingEnd: 5);
    Check(updates.Single().Text == "hello again", "Punctuation changed source text");
    Check(updates[0].RemainingDraft!.Text == "pending words" && updates[0].RemainingDraft!.End == 5, "Pending time range lost");
    return Task.CompletedTask;
});
await Test("A failed draft write rolls back the new formal record", async () =>
{
    string path = Path.Combine(root, "atomic-rollback.db");
    var store = new HistoryStore(path);
    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectDraft BEFORE INSERT ON SpeakerDrafts BEGIN SELECT RAISE(ABORT,'injected failure'); END;";
        command.ExecuteNonQuery();
    }
    bool failed = false;
    try
    {
        await store.CreateAsync("Should roll back", "zh", "test",
            speech: new("s", "key", "A", LiveCaptionsTranslator.speech.SpeakerState.Identified, 0, 1),
            remainingDraft: new("s", "remaining", "zh", "test", 1, 2));
    }
    catch (Microsoft.Data.Sqlite.SqliteException) { failed = true; }
    Check(failed && (await store.ReadBeforeAsync(null, 10)).Count == 0, "Partial commit survived failed transaction");
});
await Test("Speaker metadata and complete paragraphs survive 200 then 100 pagination", async () =>
{
    var store = NewStore("speaker-full-pages");
    for (int i = 0; i < 305; i++)
    {
        var info = new LiveCaptionsTranslator.speech.SpeechInfo("session", (i * 2).ToString(), "A",
            LiveCaptionsTranslator.speech.SpeakerState.Identified, i * 2, i * 2 + 1);
        var first = await store.CreateAsync("first " + i, "zh", "test", speech: info);
        await store.CreateAsync("second " + i, "zh", "test", paragraphId: first.Id, speech: info with { SourceKey = (i * 2 + 1).ToString() });
    }
    var p1 = await store.ReadParagraphsBeforeAsync(null, 200);
    var p2 = await store.ReadParagraphsBeforeAsync(p1.Min(r => r.GroupId), 100);
    var p3 = await store.ReadParagraphsBeforeAsync(p2.Min(r => r.GroupId), 100);
    var all = p1.Concat(p2).Concat(p3).ToArray();
    Check(p1.Count == 400 && p2.Count == 200 && p3.Count == 10, "Speaker pages split a paragraph");
    Check(all.Select(r => r.Id).Distinct().Count() == 610 && all.All(r => r.Speech!.SessionId == "session"), "Page overlap or metadata loss");
});

await Test("Single-file paths use the executable directory instead of its extraction cache", () =>
{
    string executable = Path.Combine(root, "renamed-reader.exe");
    string extraction = Path.Combine(root, "temporary-bundle");
    Check(LiveCaptionsTranslator.RuntimeOptions.LocateProgramDirectory(executable, extraction) == root, "Single-file data remained temporary");
    Check(LiveCaptionsTranslator.RuntimeOptions.LocateProgramDirectory(Path.Combine(root, "dotnet.exe"), extraction) == extraction, "DLL host directory used as application directory");
    return Task.CompletedTask;
});

Console.WriteLine($"Results: {passed} passed, {failures.Count} failed.");
Console.WriteLine("Test data: " + root);
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
