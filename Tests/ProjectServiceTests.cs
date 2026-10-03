using Xunit;
using Microsoft.Extensions.Options;
using SoundChunksWeb.Models;
using SoundChunksWeb.Services;
using System.IO;

namespace SoundChunksTests;

public class ProjectServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly AudioProcessingSettings _settings;
    private readonly ProjectService _projectService;

    public ProjectServiceTests()
    {
        // Create a temporary test directory
        _testRoot = Path.Combine(Path.GetTempPath(), $"SoundChunksTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testRoot);

        _settings = new AudioProcessingSettings
        {
            OutputRoot = _testRoot,
            PythonExecutable = "python",
            PythonScript = "../Python/audio_cutter.py",
            FfmpegExecutable = "ffmpeg"
        };

        var options = Options.Create(_settings);
        _projectService = new ProjectService(options);
    }

    public void Dispose()
    {
        // Clean up test directory
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task NewProject_StartsAtPositionZero()
    {
        // Arrange
        var projectName = "test_project";
        var state = new ProjectState
        {
            ProjectName = projectName,
            SourceFileName = "test.mp3",
            LastCutPosition = 0,
            Duration = 100
        };

        // Act
        await _projectService.SaveProjectStateAsync(projectName, state);
        var loadedState = await _projectService.GetProjectStateAsync(projectName);

        // Assert
        Assert.NotNull(loadedState);
        Assert.Equal(0, loadedState.LastCutPosition);
    }

    [Fact]
    public async Task FirstChunk_IsNumberedOne()
    {
        // Arrange
        var projectName = "test_project";
        var projectPath = _projectService.GetProjectPath(projectName);

        // Act
        var nextChunkNumber = await _projectService.GetNextChunkNumberAsync(projectPath);

        // Assert
        Assert.Equal(1, nextChunkNumber);
    }

    [Fact]
    public async Task SequentialChunkNumbering_WorksCorrectly()
    {
        // Arrange
        var projectName = "test_project";
        var projectPath = _projectService.GetProjectPath(projectName);
        Directory.CreateDirectory(projectPath);

        // Create existing chunks
        await File.WriteAllTextAsync(Path.Combine(projectPath, "1.mp3"), "fake");
        await File.WriteAllTextAsync(Path.Combine(projectPath, "2.mp3"), "fake");
        await File.WriteAllTextAsync(Path.Combine(projectPath, "3.mp3"), "fake");

        // Act
        var nextChunkNumber = await _projectService.GetNextChunkNumberAsync(projectPath);

        // Assert
        Assert.Equal(4, nextChunkNumber);
    }

    [Fact]
    public async Task ExistingChunks_AreDetected()
    {
        // Arrange
        var projectName = "test_project";
        var projectPath = _projectService.GetProjectPath(projectName);
        Directory.CreateDirectory(projectPath);

        await File.WriteAllTextAsync(Path.Combine(projectPath, "1.mp3"), "fake");
        await File.WriteAllTextAsync(Path.Combine(projectPath, "2.mp3"), "fake");

        // Act
        var nextChunkNumber = await _projectService.GetNextChunkNumberAsync(projectPath);

        // Assert
        Assert.Equal(3, nextChunkNumber);
    }

    [Fact]
    public async Task ExistingProjectState_IsLoaded()
    {
        // Arrange
        var projectName = "test_project";
        var state = new ProjectState
        {
            ProjectName = projectName,
            SourceFileName = "test.mp3",
            LastCutPosition = 45.5,
            Duration = 100
        };

        await _projectService.SaveProjectStateAsync(projectName, state);

        // Act
        var loadedState = await _projectService.GetProjectStateAsync(projectName);

        // Assert
        Assert.NotNull(loadedState);
        Assert.Equal(45.5, loadedState.LastCutPosition);
        Assert.Equal(100, loadedState.Duration);
    }

    [Fact]
    public void ProjectExists_ReturnsTrueForExistingProject()
    {
        // Arrange
        var projectName = "test_project";
        var projectPath = _projectService.GetProjectPath(projectName);
        Directory.CreateDirectory(projectPath);

        // Act
        var exists = _projectService.ProjectExists(projectName);

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public void ProjectExists_ReturnsFalseForNonExistingProject()
    {
        // Arrange
        var projectName = "nonexistent_project";

        // Act
        var exists = _projectService.ProjectExists(projectName);

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void NormalizeProjectName_RemovesExtension()
    {
        // Arrange
        var fileName = "My English Lesson.m4a";

        // Act
        var normalized = _projectService.NormalizeProjectName(fileName);

        // Assert
        Assert.Equal("My English Lesson", normalized);
    }

    [Fact]
    public void NormalizeProjectName_HandlesInvalidCharacters()
    {
        // Arrange
        var fileName = "Test<>:File?.mp3";

        // Act
        var normalized = _projectService.NormalizeProjectName(fileName);

        // Assert
        Assert.DoesNotContain("<", normalized);
        Assert.DoesNotContain(">", normalized);
        Assert.DoesNotContain(":", normalized);
    }

    [Fact]
    public void GetProjectPath_PreventsPathTraversal()
    {
        // Arrange
        var maliciousName = "../../../etc/passwd";

        // Act
        var projectPath = _projectService.GetProjectPath(maliciousName);

        // Assert
        Assert.DoesNotContain("..", projectPath);
        Assert.StartsWith(_testRoot, projectPath);
    }
}
