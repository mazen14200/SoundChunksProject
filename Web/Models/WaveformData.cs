namespace SoundChunksWeb.Models;

/// <summary>
/// Compact loudness profile of a source audio file, used by the browser to draw the waveform.
/// </summary>
public class WaveformData
{
    /// <summary>How many peak values describe one second of audio.</summary>
    public int PeaksPerSecond { get; set; }

    /// <summary>Audio duration in seconds.</summary>
    public double Duration { get; set; }

    /// <summary>
    /// Base64 of one byte per peak. Each byte is sqrt(peakAmplitude) scaled to 0..255,
    /// so quiet speech stays visible while silence stays near 0.
    /// </summary>
    public string Peaks { get; set; } = string.Empty;
}
