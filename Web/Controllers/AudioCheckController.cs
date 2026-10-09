using Microsoft.AspNetCore.Mvc;

namespace SoundChunksWeb.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AudioCheckController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    public AudioCheckController(IWebHostEnvironment env)
    {
        _env = env;
    }

    [HttpGet("exists")]
    public IActionResult CheckAudioExists([FromQuery] string sheikhId, [FromQuery] string surahId)
    {
        if (string.IsNullOrEmpty(sheikhId) || string.IsNullOrEmpty(surahId))
        {
            return BadRequest(new { exists = false, message = "Missing parameters" });
        }

        // Extract number from surah ID (e.g., "1-Al-Fatihah" -> "1")
        var surahNumber = ExtractSurahNumber(surahId);

        if (string.IsNullOrEmpty(surahNumber))
        {
            return Ok(new { exists = false, message = "Invalid surah ID" });
        }

        // Remove leading zeros
        surahNumber = surahNumber.TrimStart('0');
        if (string.IsNullOrEmpty(surahNumber))
        {
            surahNumber = "0";
        }

        var surahNumber000 = int.Parse(surahNumber).ToString("000");

        // Construct file path
        var audioFilesPath1 = Path.Combine(_env.WebRootPath, "SoundSheikh", sheikhId, surahNumber000);
        var audioFilesPath2 = Path.Combine(_env.WebRootPath, "SoundSheikh", sheikhId, surahNumber);
        var audioFilesPath3 = Path.Combine(_env.WebRootPath, "SoundSheikh", sheikhId, surahId);

        if (!Directory.Exists(audioFilesPath1) && !Directory.Exists(audioFilesPath2) && !Directory.Exists(audioFilesPath3))
        {
            return Ok(new { exists = false, message = "Sheikh folder not found" });
        }

        ////// Check for audio files with the surah number
        ////var audioExtensions = new[] { ".mp3", ".wav", ".m4a", ".ogg", ".aac" };
        var fileExists = false;
        ////string? foundFile = null;

        ////foreach (var ext in audioExtensions)
        ////{
        ////    var filePath = Path.Combine(audioFilesPath, $"{"1"}{ext}");
        ////    if (System.IO.File.Exists(filePath))
        ////    {
        ////        fileExists = true;
        ////        foundFile = Path.GetFileName(filePath);
        ////        break;
        ////    }
        ////}
        fileExists = true;
        return Ok(new { exists = fileExists, message = fileExists ? "Audio file found" : "Audio file not found", file = "foundFile" });
    }

    private string ExtractSurahNumber(string surahId)
    {
        // Extract the number part from the surah ID
        // Examples: "1-Al-Fatihah" -> "1", "001-Al-Fatihah" -> "001"
        var match = System.Text.RegularExpressions.Regex.Match(surahId, @"^(\d+)");
        return match.Success ? match.Value : string.Empty;
    }
}
