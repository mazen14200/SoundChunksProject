using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public interface IQuranAudioService
{
    /// <summary>The ayah mp3 files that exist for this sheikh and surah, with their download addresses.</summary>
    QuranFilesResult GetFiles(string sheikhId, string surahId);

    /// <summary>Physical path of one ayah file (throws <see cref="QuranAudioException"/> when missing).</summary>
    string GetAyahFilePath(string sheikhId, string surahId, int n);


}
