namespace SoundChunksWeb.Services;

public interface IAudioProbeService
{
    Task<(bool Success, double Duration, string ErrorMessage)> GetAudioDurationAsync(string filePath);
}
