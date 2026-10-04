using System.Globalization;
using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public class ChunkManagerService : IChunkManagerService
{
    private readonly IProjectService _projectService;
    private readonly IPythonAudioCutterService _audioCutterService;
    private readonly IAudioProbeService _audioProbeService;
    private readonly ILogger<ChunkManagerService> _logger;
    private static readonly SemaphoreSlim _cutLock = new SemaphoreSlim(1, 1);
    private const double DurationTolerance = 0.5; // 0.5 seconds tolerance for end of file

    public ChunkManagerService(
        IProjectService projectService,
        IPythonAudioCutterService audioCutterService,
        IAudioProbeService audioProbeService,
        ILogger<ChunkManagerService> logger)
    {
        _projectService = projectService;
        _audioCutterService = audioCutterService;
        _audioProbeService = audioProbeService;
        _logger = logger;
    }

    public async Task<ProjectState> GetOrCreateProjectAsync(string sourceFileName, string sourceStoredName, string sourceFilePath)
    {
        var projectName = _projectService.NormalizeProjectName(sourceFileName);
        var state = await _projectService.GetProjectStateAsync(projectName);

        // Get actual duration using ffprobe
        var (success, duration, errorMessage) = await _audioProbeService.GetAudioDurationAsync(sourceFilePath);
        if (!success)
        {
            _logger.LogError("Failed to get audio duration: {Error}", errorMessage);
            throw new InvalidOperationException($"Failed to get audio duration: {errorMessage}");
        }

        if (state == null)
        {
            // Create new project
            state = new ProjectState
            {
                ProjectName = projectName,
                SourceFileName = sourceFileName,
                SourceStoredName = sourceStoredName,
                LastCutPosition = 0,
                Duration = duration,
                CreatedAt = DateTime.UtcNow
            };
            
            await _projectService.SaveProjectStateAsync(projectName, state);
            _logger.LogInformation("Created new project: {ProjectName}, Duration: {Duration}s", projectName, duration);
        }
        else
        {
            // Update duration and source stored name
            state.Duration = duration;
            state.SourceStoredName = sourceStoredName;
            await _projectService.SaveProjectStateAsync(projectName, state);
            _logger.LogInformation("Loaded existing project: {ProjectName}, LastCutPosition: {LastCutPosition}s, Duration: {Duration}s", 
                projectName, state.LastCutPosition, duration);
        }

        return state;
    }

    public async Task<(bool Success, int ChunkNumber, string ErrorMessage)> CreateChunkAsync(
        string projectName,
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

            // Validate position against last cut
            if (currentPosition <= state.LastCutPosition)
            {
                return (false, 0, 
                    $"Current position ({currentPosition:F2}s) must be greater than previous cut position ({state.LastCutPosition:F2}s)");
            }

            // Validate and clamp position against duration
            if (currentPosition > state.Duration + DurationTolerance)
            {
                return (false, 0, 
                    $"Current position ({currentPosition:F2}s) exceeds audio duration ({state.Duration:F2}s)");
            }

            // Clamp to duration if within tolerance
            if (currentPosition > state.Duration)
            {
                currentPosition = state.Duration;
                _logger.LogInformation("Clamped current position to duration: {Duration}s", state.Duration);
            }

            // Calculate source file path from state
            var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
            var sourceFilePath = Path.Combine(uploadsDir, state.SourceStoredName);

            if (!File.Exists(sourceFilePath))
            {
                return (false, 0, "Source audio file not found. Please re-upload the file.");
            }

            // Get next chunk number
            var projectPath = _projectService.GetProjectPath(projectName);
            var chunkNumber = await _projectService.GetNextChunkNumberAsync(projectPath);

            // Determine output paths
            var outputMp3Path = Path.Combine(projectPath, $"{chunkNumber}.mp3");
            var outputTxtPath = Path.Combine(projectPath, $"{chunkNumber}.txt");
            var pendingCutPath = Path.Combine(projectPath, "pending-cut.json");

            _logger.LogInformation("Creating chunk {ChunkNumber}: {Start:F2}s → {End:F2}s", 
                chunkNumber, state.LastCutPosition, currentPosition);

            // Write pending-cut.json for recovery
            var pendingCut = new
            {
                chunkNumber,
                start = state.LastCutPosition,
                end = currentPosition,
                timestamp = DateTime.UtcNow
            };
            var pendingJson = System.Text.Json.JsonSerializer.Serialize(pendingCut);
            await File.WriteAllTextAsync(pendingCutPath, pendingJson);

            try
            {
                // Execute Python to cut audio
                var (success, errorMessage) = await _audioCutterService.CutAudioAsync(
                    sourceFilePath,
                    outputMp3Path,
                    state.LastCutPosition,
                    currentPosition);

                if (!success)
                {
                    _logger.LogError("Failed to create chunk {ChunkNumber}: {Error}", chunkNumber, errorMessage);
                    throw new InvalidOperationException($"Failed to create audio chunk: {errorMessage}");
                }

                _logger.LogInformation("MP3 created successfully: {OutputPath}", outputMp3Path);

                // Create empty TXT file (fail if exists)
                try
                {
                    using (var stream = new FileStream(outputTxtPath, FileMode.CreateNew))
                    {
                        // File is created empty
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "TXT file already exists for chunk {ChunkNumber}", chunkNumber);
                    throw new InvalidOperationException($"TXT file already exists for chunk {chunkNumber}");
                }

                // Update project state ONLY after successful chunk creation
                state.LastCutPosition = currentPosition;
                await _projectService.SaveProjectStateAsync(projectName, state);

                _logger.LogInformation("Successfully created chunk {ChunkNumber} and updated state", chunkNumber);

                return (true, chunkNumber, string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during chunk creation, rolling back");
                
                // Rollback: delete partial files
                if (File.Exists(outputMp3Path))
                {
                    File.Delete(outputMp3Path);
                }
                if (File.Exists(outputTxtPath))
                {
                    File.Delete(outputTxtPath);
                }

                return (false, 0, $"Error creating chunk: {ex.Message}");
            }
            finally
            {
                // Always delete pending-cut.json
                if (File.Exists(pendingCutPath))
                {
                    File.Delete(pendingCutPath);
                }
            }
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
