using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public class ProjectService : IProjectService
{
    private readonly AudioProcessingSettings _settings;
    private readonly string _outputRoot;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(IOptions<AudioProcessingSettings> settings, ILogger<ProjectService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        _outputRoot = Path.GetFullPath(_settings.OutputRoot);
        
        // Ensure output root directory exists
        if (!Directory.Exists(_outputRoot))
        {
            Directory.CreateDirectory(_outputRoot);
        }
    }

    public string GetProjectPath(string projectName)
    {
        // Sanitize project name to prevent path traversal
        var sanitizedName = string.Join("_", projectName.Split(Path.GetInvalidFileNameChars()));
        // Replace path traversal sequences
        sanitizedName = sanitizedName.Replace("..", "_");
        return Path.Combine(_outputRoot, sanitizedName);
    }

    public string NormalizeProjectName(string fileName)
    {
        // Remove extension and normalize
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var normalized = string.Join("_", nameWithoutExtension.Split(Path.GetInvalidFileNameChars()));
        // Replace path traversal sequences
        return normalized.Replace("..", "_");
    }

    public bool ProjectExists(string projectName)
    {
        var projectPath = GetProjectPath(projectName);
        return Directory.Exists(projectPath);
    }

    public async Task<ProjectState?> GetProjectStateAsync(string projectName)
    {
        var projectPath = GetProjectPath(projectName);
        var stateFilePath = Path.Combine(projectPath, "project-state.json");
        var stateFilePathBak = stateFilePath + ".bak";

        if (!File.Exists(stateFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(stateFilePath);
            return JsonSerializer.Deserialize<ProjectState>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading project state from main file, trying backup");
            
            // Try to load from backup
            if (File.Exists(stateFilePathBak))
            {
                try
                {
                    var bakJson = await File.ReadAllTextAsync(stateFilePathBak);
                    var state = JsonSerializer.Deserialize<ProjectState>(bakJson);
                    _logger.LogInformation("Successfully loaded project state from backup");
                    return state;
                }
                catch (Exception bakEx)
                {
                    _logger.LogError(bakEx, "Error reading project state from backup file");
                }
            }
            
            return null;
        }
    }

    public async Task SaveProjectStateAsync(string projectName, ProjectState state)
    {
        var projectPath = GetProjectPath(projectName);
        
        // Ensure project directory exists
        if (!Directory.Exists(projectPath))
        {
            Directory.CreateDirectory(projectPath);
        }

        var stateFilePath = Path.Combine(projectPath, "project-state.json");
        var stateFilePathTmp = stateFilePath + ".tmp";
        var stateFilePathBak = stateFilePath + ".bak";
        
        state.LastModified = DateTime.UtcNow;
        
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // Atomic write: write to temp file, then replace
        await File.WriteAllTextAsync(stateFilePathTmp, json);
        
        // Create backup of existing file
        if (File.Exists(stateFilePath))
        {
            try
            {
                File.Copy(stateFilePath, stateFilePathBak, true);
            }
            catch
            {
                // Ignore backup errors
            }
        }
        
        // Replace atomic
        File.Move(stateFilePathTmp, stateFilePath, true);
    }

    public async Task<int> GetNextChunkNumberAsync(string projectPath)
    {
        if (!Directory.Exists(projectPath))
        {
            return 1;
        }

        var files = Directory.GetFiles(projectPath, "*.mp3");
        if (files.Length == 0)
        {
            return 1;
        }

        int maxNumber = 0;
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(fileName, out int number))
            {
                maxNumber = Math.Max(maxNumber, number);
            }
        }

        return maxNumber + 1;
    }
}
