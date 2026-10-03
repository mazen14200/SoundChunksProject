using System.Text.Json;
using Microsoft.Extensions.Options;
using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public class ProjectService : IProjectService
{
    private readonly AudioProcessingSettings _settings;
    private readonly string _outputRoot;

    public ProjectService(IOptions<AudioProcessingSettings> settings)
    {
        _settings = settings.Value;
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
            // Log error but don't throw - allow project to be recreated
            Console.WriteLine($"Error reading project state: {ex.Message}");
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
        state.LastModified = DateTime.UtcNow;
        
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(stateFilePath, json);
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
