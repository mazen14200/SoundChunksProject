using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public interface IChunkManagerService
{
    Task<(bool Success, int ChunkNumber, string ErrorMessage)> CreateChunkAsync(
        string projectName,
        string sourceFilePath,
        double currentPosition);
    
    Task<ProjectState> GetOrCreateProjectAsync(string sourceFileName, double duration);
}
