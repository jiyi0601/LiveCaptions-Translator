using System.Diagnostics;
using LiveCaptionsTranslator.speech;
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
    public static event Action<string>? SpeechPreviewChanged;
    public static event Action<bool>? SpeakerModeChanged;
    public static string SpeechPreview { get; private set; } = "";
    private static readonly object speechGate = new();
    private static CancellationTokenSource? speechCancellation;
    private static Task speechCompletion = Task.CompletedTask;
    private static long recoveryKey = long.MinValue;
    private static long historyGeneration;
    private static volatile bool historyClearing;
    private static readonly ParagraphSegmenter segmenter = new(Setting.ReaderWindow.ParagraphLength, Setting.ReaderWindow.ParagraphPauseSeconds);
    private static readonly CancellationTokenSource stopping = new();
    private static readonly SortedDictionary<long, TranslationHistoryEntry> context = new();
    private static readonly TranscriptEngine engine = new(SQLiteHistoryLogger.Store, TranslateTextAsync, entryTranslator: TranslateEntryAsync);
    private static long latestId;
    private static Task? captureTask;
    public static string CaptureStatus { get; private set; } = "正在连接 Windows 实时字幕…";
    private static string? lastSpeakerFailure;

    static Translator()
    {
        FirstUseFlag = !RuntimeOptions.Demo && !RuntimeOptions.SpeakerTest && !System.IO.File.Exists(System.IO.Path.Combine(RuntimeOptions.DataDirectory, Setting.FILENAME));
        engine.Changed += EntryChanged;
        SQLiteHistoryLogger.HistoryCleared += () =>
        {
            lock (segmenter) segmenter.Reset();
            Application.Current.Dispatcher.BeginInvoke(() => { context.Clear(); ClearContexts(); });
        };
        if (RuntimeOptions.SpeakerTest) Setting.ReaderWindow.SpeakerEnabled = true;
        if (!RuntimeOptions.Demo && !Setting.ReaderWindow.SpeakerEnabled)
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
        if (lastSpeakerFailure != null && !Setting.ReaderWindow.SpeakerEnabled)
            message = "说话人模式不可用：" + lastSpeakerFailure + " · " + message;
        if (CaptureStatus == message) return;
        CaptureStatus = message;
        CaptureStatusChanged?.Invoke(message);
    }
    public static async Task ClearHistoryAsync()
    {
        Task activeSpeech;
        lock (speechGate)
        {
            historyClearing = true;
            Interlocked.Increment(ref historyGeneration);
            speechCancellation?.Cancel();
            activeSpeech = speechCompletion;
        }
        try
        {
            // Drain in-flight draft writes before clearing the database.
            await activeSpeech;
            await engine.ResetAsync();
            await SQLiteHistoryLogger.Store.ClearAsync();
        }
        finally { historyClearing = false; }
    }
    private static async Task RecoverDraftsAsync()
    {
        foreach (var draft in await SQLiteHistoryLogger.Store.ReadDraftsAsync())
        {
            var recovery = new SegmentUpdate(Interlocked.Increment(ref recoveryKey), 1, draft.Text, true)
            {
                Speech = new(draft.SessionId, "recovered", null, SpeakerState.Unknown, draft.Start, draft.End),
                RemainingDraft = draft with { Text = "" }
            };
            await engine.AcceptAsync(recovery, draft.Language, draft.Api, paused: draft.Api == "LogOnly");
        }
    }
    private static async Task SyncLoopAsync()
    {
        await RecoverDraftsAsync();
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (historyClearing) { await Task.Delay(100, stopping.Token); continue; }
                if (Setting.ReaderWindow.SpeakerEnabled)
                {
                    await RunSpeakerSessionAsync();
                    continue;
                }
                if (Window == null)
                {
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    LiveCaptionsHandler.FixLiveCaptions(Window);
                    LiveCaptionsHandler.HideLiveCaptions(Window);
                    lock (segmenter) segmenter.Reset();
                }
                long generation = Interlocked.Read(ref historyGeneration);
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
                {
                    if (historyClearing || generation != Interlocked.Read(ref historyGeneration)) break;
                    await engine.AcceptAsync(update, language, api, logOnly);
                }
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
        if (RuntimeOptions.Demo || RuntimeOptions.SpeakerTest)
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
    public static void ConfigureSpeaker(bool enabled, string? device = null)
    {
        lastSpeakerFailure = null;
        Setting.ReaderWindow.SpeakerEnabled = enabled;
        if (device != null) Setting.ReaderWindow.SpeakerDeviceId = device;
        Setting.Save();
        lock (speechGate) speechCancellation?.Cancel();
        SpeakerModeChanged?.Invoke(enabled);
    }
    private static void SetPreview(string text)
    {
        SpeechPreview = text;
        SpeechPreviewChanged?.Invoke(text);
    }
    private static async Task RunSpeakerSessionAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long generation;
        lock (speechGate)
        {
            if (historyClearing) return;
            generation = Interlocked.Read(ref historyGeneration);
            speechCancellation = cancellation;
            speechCompletion = completed.Task;
        }
        try
        {
            await ModelInstaller.EnsureAsync(RuntimeOptions.ModelDirectory, SetStatus, cancellation.Token);
            if (Window != null)
            {
                try { LiveCaptionsHandler.KillLiveCaptions(Window); } catch { }
                Window = null;
            }
            lock (segmenter) segmenter.Reset();
            SetStatus("正在加载本地说话人模型…");
            using var processor = new NativeSpeechProcessor(RuntimeOptions.ModelDirectory);
            await using var source = new LoopbackSpeechSource(processor, Setting.ReaderWindow.SpeakerDeviceId);
            var session = new SpeechSession(source.SessionId, processor, SQLiteHistoryLogger.Store, engine,
                () => (Setting.TargetLanguage, Setting.ApiName, LogOnlyFlag));
            session.PreviewChanged += SetPreview;
            source.Updated += snapshot =>
            {
                if (generation != Interlocked.Read(ref historyGeneration)) return;
                session.Length = Setting.ReaderWindow.ParagraphLength;
                session.PauseSeconds = Setting.ReaderWindow.ParagraphPauseSeconds;
                session.AcceptAsync(snapshot).GetAwaiter().GetResult();
            };
            SetStatus("本地区分说话人（测试）· 电脑播放声音 · 最近约 6 秒待确认");
            await source.RunAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OutputDeviceChangedException)
        {
            SetStatus("播放设备已变化，正在重新连接并开始新的说话人编号…");
        }
        catch (Exception ex)
        {
            try { await RecoverDraftsAsync(); }
            catch (Exception recoveryError) { SetStatus("草稿保留在历史库中，下次启动恢复：" + recoveryError.Message); }
            lastSpeakerFailure = ex.Message;
            Setting.ReaderWindow.SpeakerEnabled = false;
            Setting.Save();
            SpeakerModeChanged?.Invoke(false);
            SetStatus("已保留识别原文；正在切回 Windows 字幕，可重新勾选重试。");
            // Leave the actionable message visible before the ordinary capture loop resumes.
            try { await Task.Delay(3000, stopping.Token); } catch (OperationCanceledException) { }
        }
        finally
        {
            try
            {
                lock (speechGate) speechCancellation = null;
                SetPreview("");
            }
            finally { completed.TrySetResult(); }
        }
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
