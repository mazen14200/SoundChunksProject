namespace SoundChunksWeb.Services;

public interface IPythonAudioCutterService
{
    Task<(bool Success, string ErrorMessage)> CutAudioAsync(
        string inputFilePath,
        string outputFilePath,
        double startTime,
        double endTime);
}
