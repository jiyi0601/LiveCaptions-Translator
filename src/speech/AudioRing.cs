namespace LiveCaptionsTranslator.speech;

/// <summary>Fixed memory footprint; all positions are in the session's 16 kHz sample clock.</summary>
public sealed class AudioRing
{
    public const int Rate = 16000;
    private readonly float[] samples;
    public long Written { get; private set; }
    public AudioRing(int seconds = 30) => samples = new float[Rate * seconds];
    public void Add(float[] block)
    {
        foreach (float value in block) samples[Written++ % samples.Length] = value;
    }
    public float[] Read(long start, long end)
    {
        start = Math.Max(start, Math.Max(0, Written - samples.Length));
        end = Math.Min(end, Written);
        if (end < start) return Array.Empty<float>();
        var copy = new float[end - start];
        for (int i = 0; i < copy.Length; i++) copy[i] = samples[(start + i) % samples.Length];
        return copy;
    }
}
