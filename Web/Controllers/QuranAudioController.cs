using Microsoft.AspNetCore.Mvc;
using SoundChunksWeb.Models;
using SoundChunksWeb.Services;

namespace SoundChunksWeb.Controllers;

/// <summary>
/// Endpoints used by the surah pages (wwwroot/js/quraan-player.js):
///   GET api/quranaudio/files   the ayah mp3 files that exist for a sheikh + surah
///   GET api/quranaudio/ayah    downloads one ayah mp3
/// No audio processing happens on the server.
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving ayah file");
            return StatusCode(500, new { error = "حدث خطأ غير متوقع." });
        }
    }
}
