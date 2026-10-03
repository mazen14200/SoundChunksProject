using System.Diagnostics;
using Microsoft.Extensions.Options;
using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public class PythonAudioCutterService : IPythonAudioCutterService
{
    private readonly AudioProcessingSettings _settings;
    private readonly ILogger<PythonAudioCutterService> _logger;

    public PythonAudioCutterService(
        IOptions<AudioProcessingSettings> settings,
        ILogger<PythonAudioCutterService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<(bool Success, string ErrorMessage)> CutAudioAsync(
        string inputFilePath,
        string outputFilePath,
        double startTime,
        double endTime)
    {
        try
        {
            // Validate inputs
            if (!File.Exists(inputFilePath))
            {
                return (false, $"Input file does not exist: {inputFilePath}");
            }

            if (endTime <= startTime)
            {
                return (false, $"End time ({endTime}) must be greater than start time ({startTime})");
            }

            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // Build Python arguments
            var scriptPath = Path.GetFullPath(_settings.PythonScript);
            var arguments = $"\"{scriptPath}\" --input \"{inputFilePath}\" --output \"{outputFilePath}\" --start {startTime:F3} --end {endTime:F3}";

            _logger.LogInformation("Executing Python: {PythonExecutable} {Arguments}", _settings.PythonExecutable, arguments);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = _settings.PythonExecutable,
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
                    _logger.LogInformation("Python stdout: {Output}", e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) => 
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorBuilder.AppendLine(e.Data);
                    _logger.LogWarning("Python stderr: {Error}", e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();

            _logger.LogInformation("Python process exited with code: {ExitCode}", process.ExitCode);

            if (process.ExitCode != 0)
            {
                return (false, $"Python process failed with exit code {process.ExitCode}. Error: {error}");
            }

            // Verify output file was created
            if (!File.Exists(outputFilePath))
            {
                return (false, "Python process completed successfully but output file was not created");
            }

            // Verify output file has content
            var fileInfo = new FileInfo(outputFilePath);
            if (fileInfo.Length == 0)
            {
                File.Delete(outputFilePath);
                return (false, "Output file was created but is empty");
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing Python audio cutter");
            return (false, $"Error executing Python: {ex.Message}");
        }
    }
}
