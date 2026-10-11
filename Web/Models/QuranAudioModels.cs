namespace SoundChunksWeb.Models;

/// <summary>One ayah mp3 that the browser can download.</summary>
public class QuranFileInfo
{
    public int N { get; set; }

    /// <summary>Download address of the file.</summary>
    public string Url { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>Changes whenever the file changes; the browser uses it to know its saved copy is still valid.</summary>
    public string Version { get; set; } = string.Empty;
}

/// <summary>Which ayah files exist for a surah and a sheikh.</summary>
public class QuranFilesResult
{
    public bool Found { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<QuranFileInfo> Ayat { get; set; } = new();
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
