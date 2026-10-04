using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public class AudioProbeService : IAudioProbeService
{
    private readonly AudioProcessingSettings _settings;
    private readonly ILogger<AudioProbeService> _logger;

    public AudioProbeService(
        IOptions<AudioProcessingSettings> settings,
        ILogger<AudioProbeService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<(bool Success, double Duration, string ErrorMessage)> GetAudioDurationAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return (false, 0, $"File does not exist: {filePath}");
            }

            // Use ffprobe to get duration
            var ffprobePath = GetFfprobePath();
            var arguments = $"-v error -show_entries format=duration -of json \"{filePath}\"";

            _logger.LogInformation("Executing ffprobe: {Ffprobe} {Arguments}", ffprobePath, arguments);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            
            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) => 
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) => 
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();

            if (process.ExitCode != 0)
            {
                _logger.LogError("ffprobe failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                return (false, 0, $"ffprobe failed: {error}");
            }

            // Parse JSON output
            var jsonDoc = JsonDocument.Parse(output);
            if (!jsonDoc.RootElement.TryGetProperty("format", out var formatElement))
            {
                return (false, 0, "Invalid ffprobe output: missing format");
            }

            if (!formatElement.TryGetProperty("duration", out var durationElement))
            {
                return (false, 0, "Invalid ffprobe output: missing duration");
            }

            // Handle both string and number formats
            double duration;
            if (durationElement.ValueKind == JsonValueKind.String)
            {
                if (!double.TryParse(durationElement.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration))
                {
                    return (false, 0, "Invalid ffprobe output: duration is not a valid number");
                }
            }
            else
            {
                duration = durationElement.GetDouble();
            }

            _logger.LogInformation("Audio duration: {Duration} seconds", duration);

            return (true, duration, string.Empty);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error parsing ffprobe output");
            return (false, 0, $"Error parsing ffprobe output: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting audio duration");
            return (false, 0, $"Error getting audio duration: {ex.Message}");
        }
    }

    private string GetFfprobePath()
    {
        // Use configured ffprobe path
        if (!string.IsNullOrEmpty(_settings.FfprobeExecutable))
        {
            return _settings.FfprobeExecutable;
        }

        // Try to derive from ffmpeg path
        var ffmpegPath = _settings.FfmpegExecutable;
        var directory = Path.GetDirectoryName(ffmpegPath);
        
        if (!string.IsNullOrEmpty(directory))
        {
            var ffprobePath = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(ffprobePath))
            {
                return ffprobePath;
            }
        }

        // Fallback to "ffprobe" in PATH
        return "ffprobe";
    }
}
