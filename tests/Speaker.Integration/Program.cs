using System.Diagnostics;
using System.Text.Json;
using LiveCaptionsTranslator.speech;
using LiveCaptionsTranslator.transcript;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

if (args.Length == 4 && args[0] == "--compare") { ComparisonReport.Run(args[1], args[2], args[3]); return 0; }
if (args.Length == 2 && args[0] == "--install") { await ModelInstaller.EnsureAsync(args[1], Console.WriteLine, CancellationToken.None); return 0; }
if (args.Length == 3 && args[0] == "--embeddings") { EmbeddingDiagnostic.Run(args[1], args[2]); return 0; }
if (args.Length == 3 && args[0] == "--make-fixtures") { FixtureBuilder.Build(args[1], args[2]); return 0; }
if (args.Length < 3) { Console.Error.WriteLine("Usage: models wav output.json [--realtime] [--seconds=N]"); return 2; }
string root = Path.GetFullPath(args[0]), wav = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
bool realtime = args.Contains("--realtime");
double duration = double.Parse(args.FirstOrDefault(a => a.StartsWith("--seconds="))?[10..] ?? "0");
AudioReference? reference = File.Exists(Path.ChangeExtension(wav, ".reference.json"))
    ? JsonSerializer.Deserialize<AudioReference>(File.ReadAllText(Path.ChangeExtension(wav, ".reference.json"))) : null;
var load = Stopwatch.StartNew();
using var processor = new NativeSpeechProcessor(root);
Console.WriteLine("Models loaded: " + load.Elapsed.TotalSeconds.ToString("F2") + "s");
var db = new HistoryStore(Path.ChangeExtension(output, ".db"));
await using var engine = new TranscriptEngine(db, (t, _) => Task.FromResult("[测试译文] " + t), draftDelay: TimeSpan.Zero);
var session = new SpeechSession(Guid.NewGuid().ToString("N"), processor, db, engine, () => ("zh-CN", "Test", false));
var records = new List<object>();
var latencies = new List<double>();
var analysisTimes = new List<double>();
var mistakes = new List<object>();
var timer = Stopwatch.StartNew();
var committedIds = new HashSet<long>();
engine.Changed += entry =>
{
    if (entry.Speech != null && committedIds.Add(entry.Id))
        latencies.Add((realtime ? timer.Elapsed.TotalSeconds : processor.AudioTime) - entry.Speech.End);
};
using var input = new AudioFileReader(wav);
ISampleProvider audio = input;
if (input.WaveFormat.Channels == 2) audio = new StereoToMonoSampleProvider(audio);
if (audio.WaveFormat.SampleRate != 16000) audio = new WdlResamplingSampleProvider(audio, 16000);
float[] buffer = new float[1600];
int count, loops = 0, expectedFrames = 0, correctFrames = 0, unknownFrames = 0;
double nextLog = 10, nextEvaluate = 0, maxWorkingMiB = 0;
void Evaluate(SpeechSnapshot snapshot)
{
    if (reference == null) return;
    double end = snapshot.Finished ? snapshot.AudioTime : snapshot.AudioTime - NativeSpeechProcessor.ConfirmationDelay;
    while (nextEvaluate <= end)
    {
        double inFile = nextEvaluate % reference.Duration;
        var truth = reference.Frames.FirstOrDefault(f => Math.Abs(f.Time - inFile) < .051);
        if (truth != null)
        {
            expectedFrames++;
            var actual = SpeakerLabels.ForWord(new(0, "", nextEvaluate, nextEvaluate + .01), snapshot.Speakers);
            string value = actual.State == SpeakerState.Overlap ? "Overlap" : actual.SpeakerId ?? "Unknown";
            if (value == truth.Speaker) correctFrames++;
            else if (mistakes.Count < 200) mistakes.Add(new { Time = nextEvaluate, Expected = truth.Speaker, Actual = value });
            if (value == "Unknown") unknownFrames++;
        }
        nextEvaluate += .1;
    }
}
while ((count = audio.Read(buffer, 0, buffer.Length)) > 0 || (duration > processor.AudioTime && duration > 0))
{
    if (count == 0)
    {
        input.Position = 0; loops++;
        audio = input;
        if (input.WaveFormat.Channels == 2) audio = new StereoToMonoSampleProvider(audio);
        if (audio.WaveFormat.SampleRate != 16000) audio = new WdlResamplingSampleProvider(audio, 16000);
        count = audio.Read(buffer, 0, buffer.Length);
        if (count == 0) break;
    }
    var snapshot = processor.Accept(buffer[..count]);
    await session.AcceptAsync(snapshot);
    Evaluate(snapshot);
    if (snapshot.AudioTime >= nextLog)
    {
        maxWorkingMiB = Math.Max(maxWorkingMiB, Process.GetCurrentProcess().WorkingSet64 / 1048576.0);
        Console.WriteLine("Audio " + snapshot.AudioTime.ToString("F1") + "s; wall " + timer.Elapsed.TotalSeconds.ToString("F1") +
            "s; speakers " + processor.SpeakerCount + "; memory " + maxWorkingMiB.ToString("F0") + " MiB");
        records.Add(new { snapshot.AudioTime, Wall = timer.Elapsed.TotalSeconds, Speakers = snapshot.Speakers, MemoryMiB = maxWorkingMiB });
        analysisTimes.Add(processor.LastAnalysisMilliseconds);
        nextLog += 10;
    }
    if (realtime)
    {
        double wait = snapshot.AudioTime - timer.Elapsed.TotalSeconds;
        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
    }
    if (duration > 0 && processor.AudioTime >= duration) break;
}
var final = processor.Accept(Array.Empty<float>(), true);
await session.AcceptAsync(final);
Evaluate(final);
await engine.WhenIdleAsync();
var history = new List<LiveCaptionsTranslator.models.TranslationHistoryEntry>();
long? cursor = null;
while (true)
{
    var page = await db.ReadParagraphsBeforeAsync(cursor, 200);
    if (page.Count == 0) break;
    history.AddRange(page);
    cursor = page.Min(e => e.GroupId);
}
history = history.OrderBy(e => e.Id).ToList();
latencies.Sort();
double p95 = latencies.Count > 0 ? latencies[(int)((latencies.Count - 1) * .95)] : 0;
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
{
    AudioSeconds = processor.AudioTime, WallSeconds = timer.Elapsed.TotalSeconds, RealTimeFactor = timer.Elapsed.TotalSeconds / processor.AudioTime,
    processor.SpeakerCount, MaxWorkingMiB = maxWorkingMiB, AnalysisMilliseconds = analysisTimes,
    ExpectedFrames = expectedFrames, CorrectFrames = correctFrames, UnknownFrames = unknownFrames,
    SpeakerAccuracy = expectedFrames > 0 ? (double)correctFrames / expectedFrames : (double?)null,
    CommitLatencyP95Seconds = p95, Observations = records, Mistakes = mistakes, History = history
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Report: " + output + "; accuracy " + (expectedFrames > 0 ? (100.0 * correctFrames / expectedFrames).ToString("F1") : "-") +
    "%; commit p95 " + p95.ToString("F2") + "s");
return 0;
