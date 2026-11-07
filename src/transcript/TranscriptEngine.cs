using LiveCaptionsTranslator.models;
namespace LiveCaptionsTranslator.transcript;

/// <summary>Assigns stable database identities before translation and rejects stale revisions.</summary>
public sealed class TranscriptEngine : IAsyncDisposable
{
    private readonly HistoryStore store;
    private readonly Func<string, CancellationToken, Task<string>> translate;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim workers;
    private readonly Dictionary<long, State> states = new();
    private readonly HashSet<Task> running = new();
    private readonly TimeSpan draftDelay;
    private bool disposed;
    private sealed record State(int Revision, TranslationHistoryEntry Entry, CancellationTokenSource Cancellation);
    public event Action<TranslationHistoryEntry>? Changed;

    public TranscriptEngine(HistoryStore store, Func<string, CancellationToken, Task<string>> translate,
        int concurrency = 1, TimeSpan? draftDelay = null)
    {
        this.store = store;
        this.translate = translate;
        workers = new(concurrency, concurrency);
        this.draftDelay = draftDelay ?? TimeSpan.FromMilliseconds(350);
    }

    public async Task AcceptAsync(SegmentUpdate update, string language, string api, bool paused = false)
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            states.TryGetValue(update.Key, out var old);
            if (old != null && old.Revision >= update.Revision) return;
            old?.Cancellation.Cancel();
            var entry = old == null
                ? await store.CreateAsync(update.Text, language, api)
                : old.Entry with { SourceText = update.Text, TranslatedText = "", TargetLanguage = language, ApiUsed = api, Version = update.Revision * 2L };
            if (paused) entry = entry with { TranslatedText = "[Paused]", ApiUsed = "LogOnly" };
            await store.UpdateAsync(entry);
            var state = new State(update.Revision, entry, new CancellationTokenSource());
            states[update.Key] = state;
            Changed?.Invoke(entry);
            if (!paused)
            {
                var task = RunAsync(update, state);
                lock (running) running.Add(task);
                _ = task.ContinueWith(done => { lock (running) running.Remove(done); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally { gate.Release(); }
    }

    private async Task RunAsync(SegmentUpdate update, State state)
    {
        var token = state.Cancellation.Token;
        string result;
        try
        {
            if (!update.IsFinal) await Task.Delay(draftDelay, token);
            await workers.WaitAsync(token);
            try { result = await translate(update.Text, token); }
            finally { workers.Release(); }
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result)) result = "[ERROR] Translation returned no text.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) { result = "[ERROR] " + ex.Message; }

        await gate.WaitAsync();
        try
        {
            if (disposed || token.IsCancellationRequested ||
                !states.TryGetValue(update.Key, out var latest) || latest.Revision != update.Revision) return;
            var completed = latest.Entry with { TranslatedText = result, Version = update.Revision * 2L + 1 };
            await store.UpdateAsync(completed);
            states[update.Key] = latest with { Entry = completed };
            Changed?.Invoke(completed);
        }
        finally { gate.Release(); }
    }

    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (running) pending = running.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    public async Task ResetAsync()
    {
        await gate.WaitAsync();
        try
        {
            foreach (var state in states.Values) state.Cancellation.Cancel();
            states.Clear();
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            disposed = true;
            foreach (var state in states.Values) state.Cancellation.Cancel();
        }
        finally { gate.Release(); }
        await WhenIdleAsync();
        foreach (var state in states.Values) state.Cancellation.Dispose();
    }
}
