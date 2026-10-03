using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public interface IProjectService
{
    Task<ProjectState?> GetProjectStateAsync(string projectName);
    Task SaveProjectStateAsync(string projectName, ProjectState state);
    Task<int> GetNextChunkNumberAsync(string projectPath);
    string GetProjectPath(string projectName);
    string NormalizeProjectName(string fileName);
    bool ProjectExists(string projectName);
}
