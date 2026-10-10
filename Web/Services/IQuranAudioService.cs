using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public interface IQuranAudioService
{
    /// <summary>Which ayah mp3 files exist for this sheikh and surah.</summary>
    QuranFilesResult GetFiles(string sheikhId, string surahId);

    /// <summary>Audio for ayat from..to (a single file, or one merged mp3 built on first use).</summary>
    Task<QuranAudioResult> GetAudioAsync(string sheikhId, string surahId, int from, int to);

    /// <summary>Loudness peaks of the audio returned by <see cref="GetAudioAsync"/> for the same range.</summary>
    Task<WaveformData> GetWaveformAsync(string sheikhId, string surahId, int from, int to);

    /// <summary>Physical path of one ayah file (throws <see cref="QuranAudioException"/> when missing).</summary>
    string GetAyahFilePath(string sheikhId, string surahId, int n);

    /// <summary>Physical path of a merged file from its token (throws <see cref="QuranAudioException"/> when missing).</summary>
    string GetMergedFilePath(string token);

    /// <summary>Removes old cache files that have not been used for CacheMaxAge.</summary>
    void TrimCache();
}
