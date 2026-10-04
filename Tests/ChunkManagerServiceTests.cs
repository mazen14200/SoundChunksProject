using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SoundChunksWeb.Models;
using SoundChunksWeb.Services;

namespace SoundChunksTests;

public class ChunkManagerServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly AudioProcessingSettings _settings;
    private readonly Mock<IProjectService> _mockProjectService;
    private readonly Mock<IPythonAudioCutterService> _mockAudioCutterService;
    private readonly Mock<IAudioProbeService> _mockAudioProbeService;
    private readonly Mock<ILogger<ChunkManagerService>> _mockLogger;
    private readonly ChunkManagerService _chunkManagerService;

    public ChunkManagerServiceTests()
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

        _mockProjectService = new Mock<IProjectService>();
        _mockAudioCutterService = new Mock<IPythonAudioCutterService>();
        _mockAudioProbeService = new Mock<IAudioProbeService>();
        _mockLogger = new Mock<ILogger<ChunkManagerService>>();

        _chunkManagerService = new ChunkManagerService(
            _mockProjectService.Object,
            _mockAudioCutterService.Object,
            _mockAudioProbeService.Object,
            _mockLogger.Object);
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
    public async Task GetOrCreateProject_CreatesNewProjectWhenNotExists()
    {
        // Arrange
        var fileName = "test.mp3";
        var storedName = "test.mp3";
        var filePath = "C:\\uploads\\test.mp3";
        var duration = 100.0;
        _mockProjectService.Setup(s => s.GetProjectStateAsync(It.IsAny<string>()))
            .ReturnsAsync((ProjectState?)null);
        _mockProjectService.Setup(s => s.NormalizeProjectName(fileName))
            .Returns("test");
        _mockAudioProbeService.Setup(s => s.GetAudioDurationAsync(It.IsAny<string>()))
            .ReturnsAsync((true, duration, string.Empty));

        // Act
        var state = await _chunkManagerService.GetOrCreateProjectAsync(fileName, storedName, filePath);

        // Assert
        Assert.NotNull(state);
        Assert.Equal(0, state.LastCutPosition);
        Assert.Equal(duration, state.Duration);
        Assert.Equal(storedName, state.SourceStoredName);
        _mockProjectService.Verify(s => s.SaveProjectStateAsync(It.IsAny<string>(), It.IsAny<ProjectState>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateProject_LoadsExistingProject()
    {
        // Arrange
        var fileName = "test.mp3";
        var storedName = "test.mp3";
        var filePath = "C:\\uploads\\test.mp3";
        var duration = 100.0;
        var existingState = new ProjectState
        {
            ProjectName = "test",
            SourceFileName = fileName,
            SourceStoredName = "old.mp3",
            LastCutPosition = 45.5,
            Duration = 50.0
        };

        _mockProjectService.Setup(s => s.GetProjectStateAsync(It.IsAny<string>()))
            .ReturnsAsync(existingState);
        _mockProjectService.Setup(s => s.NormalizeProjectName(fileName))
            .Returns("test");
        _mockAudioProbeService.Setup(s => s.GetAudioDurationAsync(It.IsAny<string>()))
            .ReturnsAsync((true, duration, string.Empty));

        // Act
        var state = await _chunkManagerService.GetOrCreateProjectAsync(fileName, storedName, filePath);

        // Assert
        Assert.NotNull(state);
        Assert.Equal(45.5, state.LastCutPosition);
        Assert.Equal(duration, state.Duration); // Duration should be updated
        Assert.Equal(storedName, state.SourceStoredName); // Should be updated
    }

    [Fact]
    public async Task CreateChunk_RejectsInvalidTimestamp()
    {
        // Arrange
        var projectName = "test";
        var currentPosition = 10.0;
        var state = new ProjectState
        {
            ProjectName = projectName,
            SourceFileName = "test.mp3",
            SourceStoredName = "test.mp3",
            LastCutPosition = 20.0, // Current position is before last cut
            Duration = 100.0
        };

        _mockProjectService.Setup(s => s.GetProjectStateAsync(projectName))
            .ReturnsAsync(state);

        // Act
        var (success, chunkNumber, errorMessage) = await _chunkManagerService.CreateChunkAsync(
            projectName, currentPosition);

        // Assert
        Assert.False(success);
        Assert.Contains("must be greater than", errorMessage);
    }

    [Fact]
    public async Task CreateChunk_RejectsCurrentPositionBeforePreviousCut()
    {
        // Arrange
        var projectName = "test";
        var currentPosition = 25.0;
        var state = new ProjectState
        {
            ProjectName = projectName,
            SourceFileName = "test.mp3",
            SourceStoredName = "test.mp3",
            LastCutPosition = 25.0, // Equal positions
            Duration = 100.0
        };

        _mockProjectService.Setup(s => s.GetProjectStateAsync(projectName))
            .ReturnsAsync(state);

        // Act
        var (success, chunkNumber, errorMessage) = await _chunkManagerService.CreateChunkAsync(
            projectName, currentPosition);

        // Assert
        Assert.False(success);
    }

    [Fact]
    public async Task CreateChunk_DoesNotAdvanceStateOnPythonFailure()
    {
        // Arrange
        var projectName = "test";
        var currentPosition = 50.0;
        var initialCutPosition = 30.0;
        var state = new ProjectState
        {
            ProjectName = projectName,
            SourceFileName = "test.mp3",
            SourceStoredName = "test.mp3",
            LastCutPosition = initialCutPosition,
            Duration = 100.0
        };

        _mockProjectService.Setup(s => s.GetProjectStateAsync(projectName))
            .ReturnsAsync(state);
        _mockProjectService.Setup(s => s.GetProjectPath(projectName))
            .Returns(_testRoot);
        _mockProjectService.Setup(s => s.GetNextChunkNumberAsync(It.IsAny<string>()))
            .ReturnsAsync(1);
        _mockAudioCutterService.Setup(s => s.CutAudioAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>()))
            .ReturnsAsync((false, "FFmpeg failed"));

        // Act
        var (success, chunkNumber, errorMessage) = await _chunkManagerService.CreateChunkAsync(
            projectName, currentPosition);

        // Assert
        Assert.False(success);
        _mockProjectService.Verify(s => s.SaveProjectStateAsync(It.IsAny<string>(), It.IsAny<ProjectState>()), Times.Never);
    }

    [Fact]
    public async Task CreateChunk_AdvancesStateOnSuccess()
    {
        // Arrange
        var projectName = "test";
        var currentPosition = 50.0;
        var initialCutPosition = 30.0;
        var state = new ProjectState
        {
            ProjectName = projectName,
            SourceFileName = "test.mp3",
            SourceStoredName = "test.mp3",
            LastCutPosition = initialCutPosition,
            Duration = 100.0
        };

        var projectPath = Path.Combine(_testRoot, projectName);
        Directory.CreateDirectory(projectPath);

        _mockProjectService.Setup(s => s.GetProjectStateAsync(projectName))
            .ReturnsAsync(state);
        _mockProjectService.Setup(s => s.GetProjectPath(projectName))
            .Returns(projectPath);
        _mockProjectService.Setup(s => s.GetNextChunkNumberAsync(projectPath))
            .ReturnsAsync(1);
        _mockAudioCutterService.Setup(s => s.CutAudioAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>()))
            .Callback<string, string, double, double>((input, output, start, end) => {
                File.WriteAllText(output, "fake mp3");
            })
            .ReturnsAsync((true, string.Empty));

        // Act
        var (success, chunkNumber, errorMessage) = await _chunkManagerService.CreateChunkAsync(
            projectName, currentPosition);

        // Assert
        Assert.True(success);
        Assert.Equal(1, chunkNumber);
        _mockProjectService.Verify(s => s.SaveProjectStateAsync(It.IsAny<string>(), 
            It.Is<ProjectState>(ps => ps.LastCutPosition == currentPosition)), Times.Once);
    }

    [Fact]
    public async Task CreateChunk_CreatesEmptyTxtFile()
    {
        // Arrange
        var projectName = "test";
        var currentPosition = 50.0;
        var state = new ProjectState
        {
            ProjectName = projectName,
            SourceFileName = "test.mp3",
            SourceStoredName = "test.mp3",
            LastCutPosition = 30.0,
            Duration = 100.0
        };

        var projectPath = Path.Combine(_testRoot, projectName);
        Directory.CreateDirectory(projectPath);

        _mockProjectService.Setup(s => s.GetProjectStateAsync(projectName))
            .ReturnsAsync(state);
        _mockProjectService.Setup(s => s.GetProjectPath(projectName))
            .Returns(projectPath);
        _mockProjectService.Setup(s => s.GetNextChunkNumberAsync(projectPath))
            .ReturnsAsync(1);
        _mockAudioCutterService.Setup(s => s.CutAudioAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>()))
            .Callback<string, string, double, double>((input, output, start, end) => {
                File.WriteAllText(output, "fake mp3");
            })
            .ReturnsAsync((true, string.Empty));

        // Act
        var (success, chunkNumber, errorMessage) = await _chunkManagerService.CreateChunkAsync(
            projectName, currentPosition);

        // Assert
        Assert.True(success);
        var txtPath = Path.Combine(projectPath, "1.txt");
        Assert.True(File.Exists(txtPath));
        var txtContent = await File.ReadAllTextAsync(txtPath);
        Assert.Equal(string.Empty, txtContent);
    }
}
