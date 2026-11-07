using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.transcript;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator;

public static class Translator
{
    public static AutomationElement? Window { get; set; }
    public static Caption Caption { get; } = Caption.GetInstance();
    public static Setting Setting { get; } = Setting.Load();
    public static bool LogOnlyFlag { get; set; }
    public static bool FirstUseFlag { get; set; }
    public static event Action? TranslationLogged;
    public static event Action<TranslationHistoryEntry>? TranscriptChanged;
    public static event Action<string>? CaptureStatusChanged;
    private static readonly ParagraphSegmenter segmenter = new(Setting.ReaderWindow.ParagraphLength, Setting.ReaderWindow.ParagraphPauseSeconds);
    private static readonly CancellationTokenSource stopping = new();
    private static readonly SortedDictionary<long, TranslationHistoryEntry> context = new();
    private static readonly TranscriptEngine engine = new(SQLiteHistoryLogger.Store, TranslateTextAsync, entryTranslator: TranslateEntryAsync);
    private static long latestId;
    private static Task? captureTask;
    public static string CaptureStatus { get; private set; } = "正在连接 Windows 实时字幕…";

    static Translator()
    {
        FirstUseFlag = !RuntimeOptions.Demo && !System.IO.File.Exists(System.IO.Path.Combine(RuntimeOptions.DataDirectory, Setting.FILENAME));
        engine.Changed += EntryChanged;
        SQLiteHistoryLogger.HistoryCleared += () =>
        {
            _ = engine.ResetAsync();
            lock (segmenter) segmenter.Reset();
            Application.Current.Dispatcher.BeginInvoke(() => { context.Clear(); ClearContexts(); });
        };
        if (!RuntimeOptions.Demo)
        {
            Window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            if (!FirstUseFlag) LiveCaptionsHandler.HideLiveCaptions(Window);
        }
    }
    public static void Start() => captureTask = Task.Run(() => RuntimeOptions.Demo ? RunDemoAsync(stopping.Token) : SyncLoopAsync());

    public static async Task StopAsync()
    {
        stopping.Cancel();
        if (captureTask != null)
        {
            try { await captureTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        await engine.DisposeAsync().ConfigureAwait(false);
    }
    private static void SetStatus(string message)
    {
        if (CaptureStatus == message) return;
        CaptureStatus = message;
        CaptureStatusChanged?.Invoke(message);
    }
    private static async Task SyncLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (Window == null)
                {
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    LiveCaptionsHandler.FixLiveCaptions(Window);
                    LiveCaptionsHandler.HideLiveCaptions(Window);
                    lock (segmenter) segmenter.Reset();
                }
                string fullText = LiveCaptionsHandler.GetCaptions(Window);
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                IReadOnlyList<SegmentUpdate> changes;
                string language = Setting.TargetLanguage, api = Setting.ApiName;
                bool logOnly = LogOnlyFlag;
                lock (segmenter)
                {
                    segmenter.SetContext(language + "|" + api + "|" + logOnly);
                    changes = segmenter.Update(fullText);
                }
                foreach (var update in changes)
                    await engine.AcceptAsync(update, language, api, logOnly);
                SetStatus(LogOnlyFlag ? "仅记录原文 · 翻译已暂停" : string.IsNullOrWhiteSpace(fullText)
                    ? "正在聆听 · 等待电脑播放语音" : "实时记录中 · 英文在上，译文在下");
            }
            catch (ElementNotAvailableException) { Window = null; SetStatus("Windows 实时字幕已关闭，正在重新连接…"); }
            catch (Exception ex) { SetStatus("字幕暂不可用：" + ex.Message); }
            await Task.Delay(100, stopping.Token);
        }
    }
    private static void EntryChanged(TranslationHistoryEntry entry)
    {
        TranscriptChanged?.Invoke(entry);
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            latestId = Math.Max(latestId, entry.Id);
            context[entry.Id] = entry;
            while (context.Count > Caption.MAX_CONTEXTS + 1) context.Remove(context.Keys.First());
            Caption.SetContexts(context.Values.Where(e => e.Id < latestId && !string.IsNullOrWhiteSpace(e.TranslatedText)));
            if (entry.Id == latestId)
            {
                Caption.OriginalCaption = entry.SourceText;
                Caption.DisplayOriginalCaption = entry.SourceText;
                Caption.OverlayOriginalCaption = entry.SourceText;
                Caption.TranslatedCaption = entry.TranslatedText;
                Caption.DisplayTranslatedCaption = string.IsNullOrEmpty(entry.TranslatedText) ? "正在翻译…" : entry.TranslatedText;
                Caption.OverlayCurrentTranslation = Caption.DisplayTranslatedCaption;
                Caption.OverlayNoticePrefix = "";
            }
            TranslationLogged?.Invoke();
        });
    }
    private static async Task<string> TranslateEntryAsync(TranslationHistoryEntry entry, CancellationToken token)
    {
        using var scope = TranslateAPI.UseOptions(entry.ApiUsed, entry.TargetLanguage);
        return await TranslateTextAsync(entry.SourceText, token);
    }

    private static async Task<string> TranslateTextAsync(string text, CancellationToken token)
    {
        if (RuntimeOptions.Demo)
        {
            await Task.Delay(550, token);
            var translatedParts = new List<string>();
            if (text.Contains("first result", StringComparison.OrdinalIgnoreCase)) translatedParts.Add("我们来看第一个结果。");
            if (text.Contains("difference", StringComparison.OrdinalIgnoreCase)) translatedParts.Add("这个差异非常明显。");
            if (text.Contains("complete bilingual transcript", StringComparison.OrdinalIgnoreCase)) translatedParts.Add("我们可以回看完整的双语记录。");
            return translatedParts.Count > 0 ? string.Join("", translatedParts) : "这是一段用于检查连续阅读界面的示例译文。";
        }
        var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;
        string translated;
        if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
        {
            translated = await TranslateAPI.TranslateFunction(Caption.AwareContextsCaption + " 🔤 " + text + " 🔤", token);
            if (!translated.Contains("[ERROR]"))
            {
                var match = RegexPatterns.TargetSentence().Match(translated);
                if (match.Success) translated = match.Groups[1].Value;
            }
        }
        else translated = await TranslateAPI.TranslateFunction(text, token);
        translated = translated.Replace("🔤", "");
        if (sw != null && !translated.Contains("[ERROR]")) translated = "[" + sw.ElapsedMilliseconds + " ms] " + translated;
        return translated;
    }
    public static void SetParagraphPauseSeconds(int seconds)
    {
        lock (segmenter) segmenter.SetPauseSeconds(seconds);
        Setting.ReaderWindow.ParagraphPauseSeconds = seconds;
        Setting.Save();
    }
    public static void SetParagraphLength(ParagraphLength length)
    {
        lock (segmenter) segmenter.SetLength(length);
        Setting.ReaderWindow.ParagraphLength = length;
        Setting.Save();
    }

    public static void ClearContexts()
    {
        context.Clear();
        Caption.SetContexts(Array.Empty<TranslationHistoryEntry>());
    }
    private static async Task RunDemoAsync(CancellationToken token)
    {
        SetStatus("演示模式 · 示例数据，不采集音频");
        if ((await SQLiteHistoryLogger.Store.ReadBeforeAsync(null, 1)).Count == 0)
            for (int i = 1; i <= 215; i++)
                await SQLiteHistoryLogger.Store.CreateAsync("Earlier discussion " + i + ": Please keep the original sentence together with its translation.",
                    "zh-CN", "Demo", "较早的讨论 " + i + "：请把英文原文和对应译文保存在同一条记录中。");
        await Task.Delay(1500, token);
        string[] snapshots = {
            "Let’s look at the first", "Let’s look at the first result.",
            "Let’s look at the first result. The difference is quite clear.",
            "Let’s look at the first result. The difference is quite clear. We can scroll back through the complete bilingual transcript."
        };
        for (int i = 0; i < snapshots.Length; i++)
        {
            foreach (var update in segmenter.Update(snapshots[i]))
                await engine.AcceptAsync(update, "zh-CN", "Demo");
            await Task.Delay(i == 0 ? 400 : 1700, token);
        }
    }
}
