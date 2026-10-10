using Microsoft.AspNetCore.Mvc;
using SoundChunksWeb.Models;
using SoundChunksWeb.Services;

namespace SoundChunksWeb.Controllers;

/// <summary>
/// Endpoints used by the surah pages (wwwroot/js/quraan-player.js):
///   GET api/quranaudio/files     which ayah files exist
///   GET api/quranaudio/audio     the audio for a range of ayat (single file or merged)
///   GET api/quranaudio/waveform  loudness peaks for the same range
///   GET api/quranaudio/ayah      streams one ayah mp3
///   GET api/quranaudio/merged/{token}  streams a merged mp3
/// </summary>
[ApiController]
[Route("api/[controller]")]

public class QuranAudioController : ControllerBase
{
    private readonly IQuranAudioService _service;
    private readonly ILogger<QuranAudioController> _logger;

    public QuranAudioController(IQuranAudioService service, ILogger<QuranAudioController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("files")]
    public IActionResult Files([FromQuery] string sheikhId, [FromQuery] string surahId)
    {
        try
        {
            return Ok(_service.GetFiles(sheikhId, surahId));
        }
        catch (QuranAudioException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing ayah files");
            return StatusCode(500, new { error = "حدث خطأ غير متوقع." });
        }
    }

    [HttpGet("audio")]
    public async Task<IActionResult> Audio(
        [FromQuery] string sheikhId, [FromQuery] string surahId, [FromQuery] int from, [FromQuery] int to)
    {
        try
        {
            return Ok(await _service.GetAudioAsync(sheikhId, surahId, from, to));
        }
        catch (QuranAudioException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error preparing ayah audio");
            return StatusCode(500, new { error = "حدث خطأ غير متوقع." });
        }
    }

    [HttpGet("waveform")]
    public async Task<IActionResult> Waveform(
        [FromQuery] string sheikhId, [FromQuery] string surahId, [FromQuery] int from, [FromQuery] int to)
    {
        try
        {
            return Ok(await _service.GetWaveformAsync(sheikhId, surahId, from, to));
        }
        catch (QuranAudioException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building ayah waveform");
            return StatusCode(500, new { error = "حدث خطأ غير متوقع." });
        }
    }

    [HttpGet("ayah")]
    public IActionResult Ayah([FromQuery] string sheikhId, [FromQuery] string surahId, [FromQuery] int n)
    {
        try
        {
            var path = _service.GetAyahFilePath(sheikhId, surahId, n);
            return PhysicalFile(path, "audio/mpeg", enableRangeProcessing: true);
        }
        catch (QuranAudioException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }

    [HttpGet("merged/{token}")]
    public IActionResult Merged(string token)
    {
        try
        {
            var path = _service.GetMergedFilePath(token);
            return PhysicalFile(path, "audio/mpeg", enableRangeProcessing: true);
        }
        catch (QuranAudioException ex)
        {
            return StatusCode(ex.StatusCode, new { error = ex.Message });
        }
    }
}
