namespace SoundChunksWeb.Models;

/// <summary>Which ayah files exist for a surah and a sheikh.</summary>
public class QuranFilesResult
{
    public bool Found { get; set; }
    public string Message { get; set; } = string.Empty;
    /// <summary>Ayah numbers that have an mp3 file, ascending.</summary>
    public List<int> Ayat { get; set; } = new();
}

/// <summary>Where one ayah sits inside the audio that is sent to the player.</summary>
public class QuranSegment
{
    public int N { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
}

/// <summary>
/// The audio for a range of ayat: either the single ayah file itself,
/// or one merged mp3 built (and cached) by the server.
/// </summary>
public class QuranAudioResult
{
    /// <summary>"single" or "merged".</summary>
    public string Kind { get; set; } = "single";
    public string Url { get; set; } = string.Empty;
    public double Duration { get; set; }
    public List<QuranSegment> Segments { get; set; } = new();
    /// <summary>Ayat inside the requested range that have no mp3 file (capped at 50).</summary>
    public List<int> Missing { get; set; } = new();
}

/// <summary>Thrown by the service; the controller turns it into an HTTP status + message.</summary>
public class QuranAudioException : Exception
{
    public int StatusCode { get; }

    public QuranAudioException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
