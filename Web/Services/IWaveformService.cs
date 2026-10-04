using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public interface IWaveformService
{
    Task<(bool Success, WaveformData? Data, string ErrorMessage)> GetWaveformAsync(string projectName);
}
