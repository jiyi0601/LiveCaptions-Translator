using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

public sealed record ReferenceFrame(double Time, string Speaker);
public sealed record ReferenceClip(string File, string Speaker, double Start, double End, string Text);
public sealed record AudioReference(string Source, string License, string AudioSha256, double Duration,
    ReferenceClip[] Clips, ReferenceFrame[] Frames);
public static class FixtureBuilder
{
    public static void Build(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        var speakers = new[] { "61", "260", "121" };
        var clips = new List<(string File, string Speaker, float[] Audio, string Text)>();
        for (int who = 0; who < speakers.Length; who++)
        {
            string root = Path.Combine(source, "LibriSpeech", "test-clean", speakers[who]);
            foreach (var file in Directory.GetFiles(root, "*.flac", SearchOption.AllDirectories).OrderBy(x => x))
            {
                using var reader = new AudioFileReader(file);
                ISampleProvider provider = reader;
                if (reader.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider);
                if (provider.WaveFormat.SampleRate != 16000) provider = new WdlResamplingSampleProvider(provider, 16000);
                var samples = new List<float>();
                float[] buffer = new float[16000]; int read;
                while ((read = provider.Read(buffer, 0, buffer.Length)) != 0) samples.AddRange(buffer.Take(read));
                string name = Path.GetFileNameWithoutExtension(file);
                string transcript = Directory.GetFiles(Path.GetDirectoryName(file)!, "*.trans.txt")
                    .SelectMany(File.ReadLines).First(line => line.StartsWith(name + " "))[ (name.Length + 1)..];
                clips.Add((Path.GetRelativePath(source, file).Replace('\\','/'), ((char)('A' + who)).ToString(), samples.ToArray(), transcript));
            }
        }
        Make("clean-three", new[] { 0, 2, 4, 1, 3, 5, 0, 2, 4 }, .35);
        Make("clean-two", new[] { 0, 2, 1, 3, 0, 2 }, .25);
        Make("same-speaker", new[] { 0, 1, 0, 1 }, .25);
        Make("rapid-turns", new[] { 0, 2, 1, 4, 3, 5, 0, 2 }, .1, crop: 1.8);
        Make("long-pauses", new[] { 0, 2, 1, 3 }, 5);
        Make("overlap", new[] { 0, 2, 4 }, 0, overlap: true);
        void Make(string name, int[] order, double gap, double crop = 0, bool overlap = false)
        {
            var samples = new List<float>();
            var references = new List<ReferenceClip>();
            var frames = new List<ReferenceFrame>();
            foreach (int index in order)
            {
                var clip = clips[index];
                var audio = crop > 0 ? clip.Audio.Take((int)(crop * 16000)).ToArray() : clip.Audio.ToArray();
                string speaker = clip.Speaker;
                if (overlap)
                {
                    var other = clips[(index + 2) % clips.Count];
                    for (int i = 0; i < audio.Length; i++) audio[i] = (audio[i] + other.Audio[i % other.Audio.Length]) * .5f;
                    speaker = "Overlap";
                }
                double start = samples.Count / 16000.0;
                for (int i = 0; i + 1600 <= audio.Length; i += 1600)
                {
                    double rms = Math.Sqrt(audio.Skip(i).Take(1600).Average(x => (double)x * x));
                    if (rms >= .004) frames.Add(new(start + i / 16000.0 + .05, speaker));
                }
                samples.AddRange(audio);
                references.Add(new(clip.File, speaker, start, samples.Count / 16000.0, crop > 0 || overlap ? "" : clip.Text));
                samples.AddRange(new float[(int)(gap * 16000)]);
            }
            samples.AddRange(new float[16000 * 8]);
            string path = Path.Combine(destination, name + ".wav");
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)))
                writer.WriteSamples(samples.ToArray(), 0, samples.Count);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            File.WriteAllText(Path.ChangeExtension(path, ".reference.json"), JsonSerializer.Serialize(
                new AudioReference("https://www.openslr.org/12", "CC BY 4.0", hash, samples.Count / 16000.0,
                    references.ToArray(), frames.ToArray()), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(name + ": " + (samples.Count / 16000.0).ToString("F1") + "s");
        }
    }
}
