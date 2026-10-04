using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public interface IChunkManagerService
{
    Task<(bool Success, int ChunkNumber, string ErrorMessage)> CreateChunkAsync(
        string projectName,
        double currentPosition);
    
    Task<ProjectState> GetOrCreateProjectAsync(string sourceFileName, string sourceStoredName, string sourceFilePath);
}
