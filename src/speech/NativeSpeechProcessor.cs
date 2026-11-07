using System.IO;
using System.Diagnostics;
using SherpaOnnx;

namespace LiveCaptionsTranslator.speech;

public sealed class NativeSpeechProcessor : IDisposable
{
    private readonly OnlineRecognizer recognizer;
    private OnlineStream stream;
    private readonly OfflineSpeakerDiarization diarizer;
    private readonly SpeakerEmbeddingExtractor extractor;
    private readonly OnlinePunctuation punctuation;
    private readonly AudioRing ring = new();
    private readonly SpeakerRegistry registry = new();
    private readonly List<TimedWord> completed = new();
    private readonly List<SpeakerSpan> timeline = new();
    private double streamStart, nextAnalysis = 2;
    private long streamNumber, snapshotRevision, committedKey = -1;

    private bool disposed;
    public const double ConfirmationDelay = 6;
    public double AudioTime => ring.Written / (double)AudioRing.Rate;
    public double LastAnalysisMilliseconds { get; private set; }
    public int SpeakerCount => registry.Count;
    public IReadOnlyList<SpeakerSpan> Timeline => timeline;
    public NativeSpeechProcessor(string root)
    {
        var paths = new ModelPaths(root);
        paths.CheckExist();
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioRing.Rate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = paths.Encoder;
        config.ModelConfig.Transducer.Decoder = paths.Decoder;
        config.ModelConfig.Transducer.Joiner = paths.Joiner;
        config.ModelConfig.Tokens = paths.Tokens;
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Provider = "cpu";
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.4f;
        config.Rule2MinTrailingSilence = 1.2f;
        config.Rule3MinUtteranceLength = 60;
        recognizer = new(config);
        stream = recognizer.CreateStream();
        var speaker = new OfflineSpeakerDiarizationConfig();
        speaker.Segmentation.Pyannote.Model = paths.Segmentation;
        speaker.Segmentation.NumThreads = 2;
        speaker.Segmentation.Provider = "cpu";
        speaker.Embedding.Model = paths.Embedding;
        speaker.Embedding.NumThreads = 2;
        speaker.Embedding.Provider = "cpu";
        speaker.Clustering.NumClusters = -1;
        speaker.Clustering.Threshold = 0.35f;
        speaker.MinDurationOn = 0.15f;
        speaker.MinDurationOff = 0.15f;
        diarizer = new(speaker);
        extractor = new(speaker.Embedding);
        var punct = new OnlinePunctuationConfig();
        punct.Model.CnnBiLstm = paths.Punctuation;
        punct.Model.BpeVocab = paths.Vocabulary;
        punct.Model.NumThreads = 1;
        punct.Model.Provider = "cpu";
        punctuation = new(punct);
    }
    public string Punctuate(string text) => punctuation.AddPunct(text.ToLowerInvariant());
    public SpeechSnapshot Accept(float[] block, bool finish = false)
    {
        if (disposed) throw new ObjectDisposedException(nameof(NativeSpeechProcessor));
        ring.Add(block);
        stream.AcceptWaveform(AudioRing.Rate, block);
        while (recognizer.IsReady(stream)) recognizer.Decode(stream);
        if (finish || recognizer.IsEndpoint(stream))
        {
            // Finish before replacing the stream so its right context cannot lose a trailing word.
            stream.AcceptWaveform(AudioRing.Rate, new float[AudioRing.Rate / 2]);
            stream.InputFinished();
            while (recognizer.IsReady(stream)) recognizer.Decode(stream);
            completed.AddRange(ReadCurrentWords(true).Where(w => w.Key > committedKey));
            stream.Dispose();
            stream = recognizer.CreateStream();
            streamStart = AudioTime;
            streamNumber++;

        }
        if (AudioTime >= nextAnalysis || finish)
        {
            AnalyzeSpeakers();
            nextAnalysis = AudioTime + 2;
        }
        var words = completed.Concat(ReadCurrentWords(false)).Where(w => w.Key > committedKey).ToArray();
        return new(words, timeline.ToArray(), AudioTime, finish, ++snapshotRevision);
    }
    public void Acknowledge(long key)
    {
        committedKey = Math.Max(committedKey, key);
        completed.RemoveAll(w => w.Key <= committedKey);
    }
    private IReadOnlyList<TimedWord> ReadCurrentWords(bool final)
    {
        var result = recognizer.GetResult(stream);
        var words = new List<TimedWord>();
        string text = "";
        double start = 0, end = 0;
        int startToken = 0;
        for (int i = 0; i < result.Tokens.Length && i < result.Timestamps.Length; i++)
        {
            string token = result.Tokens[i].Replace('▁', ' ');
            if (token.StartsWith(' ') && text.Length > 0)
            {
                words.Add(new((streamNumber << 32) + startToken, text, start, end));
                text = "";
            }
            if (text.Length == 0)
            {
                startToken = i;
                // Transducer token times are emission times, not UI arrival times.
                start = streamStart + Math.Max(0, result.Timestamps[i] - .2);
            }
            text += token.Trim();
            end = streamStart + Math.Min(Math.Max(0, result.Timestamps[i] - .2) + .12, Math.Max(0, AudioTime - streamStart));
        }
        // The last word remains revisable until a following word or endpoint exists.
        if (text.Length > 0 && final) words.Add(new((streamNumber << 32) + startToken, text, start, Math.Max(start + .02, end)));
        return words;
    }
    public string PreviewText()
    {
        string finalText = string.Join(" ", completed.Where(w => w.Key > committedKey).Select(w => w.Text));
        var current = ReadCurrentWords(true).Where(w => w.Key > committedKey);
        return string.Join(" ", new[] { finalText, string.Join(" ", current.Select(w => w.Text)) }.Where(t => t.Length > 0));
    }
    private void AnalyzeSpeakers()
    {
        var watch = Stopwatch.StartNew();
        double start = Math.Max(0, AudioTime - 10);
        var audio = ring.Read((long)(start * AudioRing.Rate), ring.Written);
        if (audio.Length < AudioRing.Rate / 2) return;
        var local = diarizer.Process(audio);
        var mapped = new List<SpeakerSpan>();
        // A local segmentation slot can be reused for disconnected turns. Never concatenate
        // those turns before embedding: doing so learns a mixture of different people's voices.
        foreach (var segment in local.OrderBy(s => s.Start))
        {
            var exclusive = new List<float>();
            int a = Math.Clamp((int)(segment.Start * AudioRing.Rate), 0, audio.Length);
            int b = Math.Clamp((int)(segment.End * AudioRing.Rate), 0, audio.Length);
            for (int i = a; i < b && exclusive.Count < 4 * AudioRing.Rate; i++)
            {
                double point = i / (double)AudioRing.Rate;
                if (!local.Any(s => s.Speaker != segment.Speaker && s.Start <= point && s.End > point))
                    exclusive.Add(audio[i]);
            }
            string? id = null;
            if (exclusive.Count >= AudioRing.Rate * .6)
            {
                using var embeddingStream = extractor.CreateStream();
                embeddingStream.AcceptWaveform(AudioRing.Rate, exclusive.ToArray());
                embeddingStream.InputFinished();
                if (extractor.IsReady(embeddingStream))
                    id = registry.Match(extractor.Compute(embeddingStream), allowNew: exclusive.Count >= AudioRing.Rate * 1.2);
            }
            mapped.Add(new(start + segment.Start, Math.Min(AudioTime, start + segment.End), id,
                id == null ? SpeakerState.Unknown : SpeakerState.Identified));
            foreach (var other in local.Where(s => s.Speaker != segment.Speaker && s.Start < segment.End && s.End > segment.Start))
                mapped.Add(new(start + Math.Max(segment.Start, other.Start), start + Math.Min(segment.End, other.End),
                    null, SpeakerState.Overlap));
        }
        var prior = timeline.Where(s => s.Start < start).Select(s => s with { End = Math.Min(s.End, start) }).ToList();
        prior.AddRange(mapped);

        timeline.Clear();
        timeline.AddRange(prior.Where(s => s.End > AudioTime - 30 && s.End > s.Start));
        LastAnalysisMilliseconds = watch.Elapsed.TotalMilliseconds;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose(); recognizer.Dispose(); diarizer.Dispose(); extractor.Dispose(); punctuation.Dispose();
    }
}

public sealed class ModelPaths(string root)
{
    private string P(string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    private const string Asr = "sherpa-onnx-streaming-zipformer-en-2023-06-21/";
    private const string Punct = "sherpa-onnx-online-punct-en-2024-08-06/";
    public string Encoder => P(Asr + "encoder-epoch-99-avg-1.int8.onnx");
    public string Decoder => P(Asr + "decoder-epoch-99-avg-1.int8.onnx");
    public string Joiner => P(Asr + "joiner-epoch-99-avg-1.int8.onnx");
    public string Tokens => P(Asr + "tokens.txt");
    public string Segmentation => P("sherpa-onnx-pyannote-segmentation-3-0/model.onnx");
    public string Embedding => P("nemo_en_titanet_small.onnx");
    public string Punctuation => P(Punct + "model.int8.onnx");
    public string Vocabulary => P(Punct + "bpe.vocab");
    public IEnumerable<string> Files => new[] { Encoder, Decoder, Joiner, Tokens, Segmentation, Embedding, Punctuation, Vocabulary };
    public void CheckExist()
    {
        foreach (var file in Files)
            if (!File.Exists(file)) throw new FileNotFoundException("说话人模型尚未安装或不完整", file);
    }
}
