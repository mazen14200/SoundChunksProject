using Microsoft.AspNetCore.Mvc;
using SoundChunksWeb.Services;
using SoundChunksWeb.ViewModels;
using System.Text.Json;

namespace SoundChunksWeb.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AudioController : ControllerBase
{
    private readonly IChunkManagerService _chunkManagerService;
    private readonly IProjectService _projectService;
    private readonly IWaveformService _waveformService;
    private readonly ILogger<AudioController> _logger;

    public AudioController(
        IChunkManagerService chunkManagerService,
        IProjectService projectService,
        IWaveformService waveformService,
        ILogger<AudioController> logger)
    {
        _chunkManagerService = chunkManagerService;
        _projectService = projectService;
        _waveformService = waveformService;
        _logger = logger;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadAudio(IFormFile file)
    {
        try
        {
            _logger.LogInformation("Upload request received");

            if (file == null || file.Length == 0)
            {
                _logger.LogWarning("No file uploaded or file is empty");
                return BadRequest(new { error = "No file uploaded" });
            }

            _logger.LogInformation("File: {FileName}, Size: {FileSize} bytes", file.FileName, file.Length);

            // Create uploads directory if it doesn't exist
            var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
            _logger.LogInformation("Uploads directory: {UploadsDir}", uploadsDir);

            if (!Directory.Exists(uploadsDir))
            {
                Directory.CreateDirectory(uploadsDir);
                _logger.LogInformation("Created uploads directory");
            }

            // Sanitize filename to prevent path traversal
            var sanitizedName = string.Join("_", file.FileName.Split(Path.GetInvalidFileNameChars()));
            var filePath = Path.Combine(uploadsDir, sanitizedName);
            _logger.LogInformation("Sanitized filename: {SanitizedName}, Full path: {FilePath}", sanitizedName, filePath);

            // Save uploaded file
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }
            _logger.LogInformation("File saved successfully");

            // Get or create project with actual duration
            var state = await _chunkManagerService.GetOrCreateProjectAsync(file.FileName, sanitizedName, filePath);

            var projectPath = _projectService.GetProjectPath(state.ProjectName);
            var nextChunkNumber = await _projectService.GetNextChunkNumberAsync(projectPath);

            var viewModel = new ProjectInfoViewModel
            {
                ProjectName = state.ProjectName,
                SourceFileName = state.SourceFileName,
                LastCutPosition = state.LastCutPosition,
                Duration = state.Duration,
                NextChunkNumber = nextChunkNumber
            };

            var response = new
            {
                project = viewModel
            };

            _logger.LogInformation("Upload successful, returning response");
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading audio file");
            return StatusCode(500, new { error = $"Error uploading file: {ex.Message}" });
        }
    }

    [HttpPost("cut")]
    public async Task<IActionResult> CutAudio([FromBody] CutRequest request)
    {
        try
        {
            if (request == null)
            {
                return BadRequest(new { error = "Request body is null" });
            }

            if (string.IsNullOrEmpty(request.ProjectName) ||
                request.CurrentPosition < 0)
            {
                return BadRequest(new { error = "Invalid request parameters" });
            }

            var (success, chunkNumber, errorMessage) = await _chunkManagerService.CreateChunkAsync(
                request.ProjectName,
                request.CurrentPosition);

            if (!success)
            {
                return BadRequest(new { error = errorMessage });
            }

            var projectPath = _projectService.GetProjectPath(request.ProjectName);
            var state = await _projectService.GetProjectStateAsync(request.ProjectName);
            var nextChunkNumber = await _projectService.GetNextChunkNumberAsync(projectPath);

            var response = new
            {
                chunkNumber,
                lastCutPosition = state?.LastCutPosition ?? 0,
                nextChunkNumber
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cutting audio");
            return StatusCode(500, new { error = $"Error cutting audio: {ex.Message}" });
        }
    }

    [HttpGet("waveform/{projectName}")]
    public async Task<IActionResult> GetWaveform(string projectName)
    {
        try
        {
            if (string.IsNullOrEmpty(projectName))
            {
                return BadRequest(new { error = "Project name is required" });
            }

            var (success, data, errorMessage) = await _waveformService.GetWaveformAsync(projectName);
            if (!success || data == null)
            {
                return BadRequest(new { error = errorMessage });
            }

            return Ok(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting waveform");
            return StatusCode(500, new { error = "Could not generate the waveform." });
        }
    }

    [HttpGet("project/{projectName}")]
    public async Task<IActionResult> GetProject(string projectName)
    {
        try
        {
            if (string.IsNullOrEmpty(projectName))
            {
                return BadRequest(new { error = "Project name is required" });
            }

            var state = await _projectService.GetProjectStateAsync(projectName);
            if (state == null)
            {
                return NotFound(new { error = "Project not found" });
            }

            var projectPath = _projectService.GetProjectPath(projectName);
            var nextChunkNumber = await _projectService.GetNextChunkNumberAsync(projectPath);

            var viewModel = new ProjectInfoViewModel
            {
                ProjectName = state.ProjectName,
                SourceFileName = state.SourceFileName,
                LastCutPosition = state.LastCutPosition,
                Duration = state.Duration,
                NextChunkNumber = nextChunkNumber
            };

            return Ok(viewModel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting project");
            return StatusCode(500, new { error = $"Error getting project: {ex.Message}" });
        }
    }
}

public class CutRequest
{
    public string ProjectName { get; set; } = string.Empty;
    public double CurrentPosition { get; set; }
}
