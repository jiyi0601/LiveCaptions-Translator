using System.IO;
using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveCaptionsTranslator.speech;

public sealed record PlaybackDevice(string Id, string Name);
public sealed class OutputDeviceChangedException() : Exception("系统播放设备已变化");
public sealed class LoopbackSpeechSource(NativeSpeechProcessor processor, string deviceId) : ISpeechSource
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public event Action<SpeechSnapshot>? Updated;
    public static IReadOnlyList<PlaybackDevice> Devices()
    {
        var result = new List<PlaybackDevice> { new("", "跟随系统播放设备") };
        using var devices = new MMDeviceEnumerator();
        var list = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var item in list) { result.Add(new(item.ID, item.FriendlyName)); item.Dispose(); }
        return result;
    }
    public async Task RunAsync(CancellationToken token)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = deviceId.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        using var capture = new WasapiLoopbackCapture(device);
        capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(AudioRing.Rate, 1);
        var buffer = new BufferedWaveProvider(capture.WaveFormat)
        { BufferDuration = TimeSpan.FromSeconds(3), DiscardOnBufferOverflow = false, ReadFully = true };
        var queue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(60)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        Exception? captureError = null;
        capture.DataAvailable += (_, e) =>
        {
            try { buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); }
            catch (Exception ex) { Interlocked.CompareExchange(ref captureError, ex, null); }
        };
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Interlocked.CompareExchange(ref captureError, e.Exception, null);
        };
        capture.StartRecording();
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pump = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, pumpStop.Token);
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                int tick = 0;
                while (await timer.WaitForNextTickAsync(pumpStop.Token))
                {
                    if (captureError != null) throw new IOException("声音采集未能及时处理，原文草稿已保留。", captureError);
                    if (++tick % 20 == 0 && deviceId.Length == 0)
                    {
                        using var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (current.ID != device.ID) throw new OutputDeviceChangedException();
                    }
                    byte[] bytes = new byte[AudioRing.Rate / 10 * sizeof(float)];
                    buffer.Read(bytes, 0, bytes.Length); // Emits real silence if WASAPI has no packets.
                    var samples = new float[bytes.Length / sizeof(float)];
                    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                    if (!queue.Writer.TryWrite(samples))
                        throw new IOException("声音处理积压超过 6 秒，请切回 Windows 字幕模式。");
                }
            }
            catch (OperationCanceledException) when (pumpStop.IsCancellationRequested) { }
            catch (Exception ex) { queue.Writer.TryComplete(ex); return; }
            finally { queue.Writer.TryComplete(); }
        }, CancellationToken.None);
        try
        {
            await foreach (var block in queue.Reader.ReadAllAsync())
                Updated?.Invoke(processor.Accept(block) with { SessionId = SessionId });
        }
        finally
        {
            pumpStop.Cancel();
            capture.StopRecording();
            await pump;
            // WASAPI is now stopped. Drain the prebuffer too, so stopping or changing devices
            // cannot silently drop the final 200 ms of captured speech.
            while (buffer.BufferedBytes >= sizeof(float))
            {
                byte[] bytes = new byte[Math.Min(buffer.BufferedBytes / sizeof(float), AudioRing.Rate / 10) * sizeof(float)];
                buffer.Read(bytes, 0, bytes.Length);
                var samples = new float[bytes.Length / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                Updated?.Invoke(processor.Accept(samples) with { SessionId = SessionId });
            }
            Updated?.Invoke(processor.Accept(Array.Empty<float>(), finish: true) with { SessionId = SessionId });
        }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
