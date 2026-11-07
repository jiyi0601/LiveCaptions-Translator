using NAudio.Wave;
using SherpaOnnx;
using LiveCaptionsTranslator.speech;

public static class EmbeddingDiagnostic
{
    public static void Run(string root, string source)
    {
        var config = new SpeakerEmbeddingExtractorConfig { Model = new ModelPaths(root).Embedding, NumThreads = 2, Provider = "cpu" };
        using var extractor = new SpeakerEmbeddingExtractor(config);
        var vectors = new List<(string Name, float[] Vector)>();
        foreach (var speaker in new[] { "61", "260", "121" })
        foreach (var path in Directory.GetFiles(Path.Combine(source, "LibriSpeech", "test-clean", speaker), "*.flac", SearchOption.AllDirectories).OrderBy(x => x))
        {
            using var audio = new AudioFileReader(path);
            var samples = new float[(int)(audio.Length / 4)];
            int n = 0, read;
            while (n < samples.Length && (read = audio.Read(samples, n, samples.Length - n)) > 0) n += read;
            foreach (var interval in new[] { (Start: 0, Length: n), (Start: 0, Length: Math.Min(n, 32000)), (Start: Math.Max(0, n - 32000), Length: Math.Min(n, 32000)) })
            {
                using var stream = extractor.CreateStream();
                stream.AcceptWaveform(16000, samples.Skip(interval.Start).Take(interval.Length).ToArray());
                stream.InputFinished();
                var vector = extractor.Compute(stream);
                double length = Math.Sqrt(vector.Sum(x => (double)x * x));
                vectors.Add((Path.GetFileNameWithoutExtension(path) + ":" + interval.Start + "/" + interval.Length,
                    vector.Select(x => (float)(x / length)).ToArray()));
            }
        }
        for (int i = 0; i < vectors.Count; i++)
        {
            Console.WriteLine(vectors[i].Name);
            Console.WriteLine(string.Join(" ", vectors.Select(v => v.Vector.Zip(vectors[i].Vector, (a,b) => (double)a*b).Sum().ToString("F2"))));
        }
    }
}
