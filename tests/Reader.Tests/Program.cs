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

Console.WriteLine($"Results: {passed} passed, {failures.Count} failed.");
Console.WriteLine("Test data: " + root);
Environment.ExitCode = failures.Count == 0 ? 0 : 1;
