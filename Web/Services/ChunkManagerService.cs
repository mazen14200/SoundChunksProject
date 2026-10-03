using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public class ChunkManagerService : IChunkManagerService
{
    private readonly IProjectService _projectService;
    private readonly IPythonAudioCutterService _audioCutterService;
    private readonly ILogger<ChunkManagerService> _logger;
    private static readonly SemaphoreSlim _cutLock = new SemaphoreSlim(1, 1);

    public ChunkManagerService(
        IProjectService projectService,
        IPythonAudioCutterService audioCutterService,
        ILogger<ChunkManagerService> logger)
    {
        _projectService = projectService;
        _audioCutterService = audioCutterService;
        _logger = logger;
    }

    public async Task<ProjectState> GetOrCreateProjectAsync(string sourceFileName, double duration)
    {
        var projectName = _projectService.NormalizeProjectName(sourceFileName);
        var state = await _projectService.GetProjectStateAsync(projectName);

        if (state == null)
        {
            // Create new project
            state = new ProjectState
            {
                ProjectName = projectName,
                SourceFileName = sourceFileName,
                LastCutPosition = 0,
                Duration = duration,
                CreatedAt = DateTime.UtcNow
            };
            
            await _projectService.SaveProjectStateAsync(projectName, state);
            _logger.LogInformation("Created new project: {ProjectName}", projectName);
        }
        else
        {
            // Update duration if different (in case file was reloaded)
            state.Duration = duration;
            await _projectService.SaveProjectStateAsync(projectName, state);
            _logger.LogInformation("Loaded existing project: {ProjectName}, LastCutPosition: {LastCutPosition}", 
                projectName, state.LastCutPosition);
        }

        return state;
    }

    public async Task<(bool Success, int ChunkNumber, string ErrorMessage)> CreateChunkAsync(
        string projectName,
        string sourceFilePath,
        double currentPosition)
    {
        // Acquire lock to prevent concurrent cuts
        if (!await _cutLock.WaitAsync(TimeSpan.FromSeconds(30)))
        {
            return (false, 0, "Another cut operation is in progress. Please wait.");
        }

        try
        {
            // Get current project state
            var state = await _projectService.GetProjectStateAsync(projectName);
            if (state == null)
            {
                return (false, 0, "Project state not found. Please reload the audio file.");
            }

            // Validate position
            if (currentPosition <= state.LastCutPosition)
            {
                return (false, 0, 
                    $"Current position ({currentPosition:F2}s) must be greater than previous cut position ({state.LastCutPosition:F2}s)");
            }

            // Get next chunk number
            var projectPath = _projectService.GetProjectPath(projectName);
            var chunkNumber = await _projectService.GetNextChunkNumberAsync(projectPath);

            // Determine output paths
            var outputMp3Path = Path.Combine(projectPath, $"{chunkNumber}.mp3");
            var outputTxtPath = Path.Combine(projectPath, $"{chunkNumber}.txt");

            _logger.LogInformation("Creating chunk {ChunkNumber}: {Start:F2}s → {End:F2}s", 
                chunkNumber, state.LastCutPosition, currentPosition);

            // Execute Python to cut audio
            var (success, errorMessage) = await _audioCutterService.CutAudioAsync(
                sourceFilePath,
                outputMp3Path,
                state.LastCutPosition,
                currentPosition);

            if (!success)
            {
                _logger.LogError("Failed to create chunk {ChunkNumber}: {Error}", chunkNumber, errorMessage);
                return (false, 0, $"Failed to create audio chunk: {errorMessage}");
            }

            // Create empty TXT file
            try
            {
                await File.WriteAllTextAsync(outputTxtPath, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create TXT file for chunk {ChunkNumber}", chunkNumber);
                // Clean up MP3 if TXT creation fails
                if (File.Exists(outputMp3Path))
                {
                    File.Delete(outputMp3Path);
                }
                return (false, 0, $"Failed to create TXT file: {ex.Message}");
            }

            // Update project state ONLY after successful chunk creation
            state.LastCutPosition = currentPosition;
            await _projectService.SaveProjectStateAsync(projectName, state);

            _logger.LogInformation("Successfully created chunk {ChunkNumber} and updated state", chunkNumber);

            return (true, chunkNumber, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating chunk");
            return (false, 0, $"Error creating chunk: {ex.Message}");
        }
        finally
        {
            _cutLock.Release();
        }
    }
}
