using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

/// <summary>
/// Builds a small loudness profile (peaks) for the waveform display.
/// The audio is decoded by FFmpeg to 8 kHz mono 16-bit PCM and streamed through,
/// so memory use stays flat even for multi-hour recordings.
/// The result is cached in the project folder (_cache/waveform.json).
/// </summary>
public class WaveformService : IWaveformService
{
    public const int PeaksPerSecond = 25;
    private const int AnalysisSampleRate = 8000;
    private const int SamplesPerPeak = AnalysisSampleRate / PeaksPerSecond; // 320 samples = 40 ms
    private const int CacheVersion = 1;
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly AudioProcessingSettings _settings;
    private readonly IProjectService _projectService;
    private readonly ILogger<WaveformService> _logger;

    public WaveformService(
        IOptions<AudioProcessingSettings> settings,
        IProjectService projectService,
        ILogger<WaveformService> logger)
    {
        _settings = settings.Value;
        _projectService = projectService;
        _logger = logger;
    }

    public async Task<(bool Success, WaveformData? Data, string ErrorMessage)> GetWaveformAsync(string projectName)
    {
        try
        {
            var state = await _projectService.GetProjectStateAsync(projectName);
            if (state == null)
            {
                return (false, null, "Project not found. Please reload the audio file.");
            }

            // Same location ChunkManagerService uses for the uploaded source file.
            var sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "uploads", state.SourceStoredName);
            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists)
            {
                return (false, null, "Source audio file not found. Please re-upload the file.");
            }

            var projectPath = _projectService.GetProjectPath(projectName);
            var cachePath = Path.Combine(projectPath, "_cache", "waveform.json");

            var gate = ProjectLocks.GetOrAdd(projectPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                var cached = await TryReadCacheAsync(cachePath, sourceInfo.Length, state.Duration);
                if (cached != null)
                {
                    _logger.LogInformation("Waveform served from cache for project {ProjectName}", projectName);
                    return (true, cached, string.Empty);
                }

                var (ok, peaks, error) = await ExtractPeaksAsync(sourcePath);
                if (!ok || peaks == null)
                {
                    return (false, null, error);
                }

                var data = new WaveformData
                {
                    PeaksPerSecond = PeaksPerSecond,
                    Duration = state.Duration > 0 ? state.Duration : (double)peaks.Length / PeaksPerSecond,
                    Peaks = Convert.ToBase64String(peaks)
                };

                await TryWriteCacheAsync(cachePath, sourceInfo.Length, state.Duration, data);
                return (true, data, string.Empty);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building waveform for project {ProjectName}", projectName);
            return (false, null, "Could not generate the waveform.");
        }
    }

    private async Task<(bool Success, byte[]? Peaks, string ErrorMessage)> ExtractPeaksAsync(string sourcePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.FfmpegExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-i", $"file:{sourcePath}",
            "-vn", "-map", "0:a:0",
            "-ac", "1",
            "-ar", AnalysisSampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le",
            "pipe:1"
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeout = new CancellationTokenSource(ExtractionTimeout);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start FFmpeg ({Ffmpeg}) for waveform", _settings.FfmpegExecutable);
            return (false, null, "FFmpeg is not available on the server.");
        }

        using var killOnTimeout = timeout.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // process already gone
            }
        });

        var stderrTask = process.StandardError.ReadToEndAsync();
        var peaks = new List<byte>(capacity: 64 * 1024);

        try
        {
            var stream = process.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
            int pendingLowByte = -1;   // first byte of a 16-bit sample split across reads
            int samplesInBucket = 0;
            int bucketMax = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    if (pendingLowByte < 0)
                    {
                        pendingLowByte = buffer[i];
                        continue;
                    }

                    short sample = (short)(pendingLowByte | (buffer[i] << 8));
                    pendingLowByte = -1;

                    int abs = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
                    if (abs > bucketMax) bucketMax = abs;

                    if (++samplesInBucket == SamplesPerPeak)
                    {
                        peaks.Add(EncodePeak(bucketMax));
                        bucketMax = 0;
                        samplesInBucket = 0;
                    }
                }
            }

            if (samplesInBucket > 0)
            {
                peaks.Add(EncodePeak(bucketMax));
            }

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Waveform extraction timed out after {Minutes} minutes", ExtractionTimeout.TotalMinutes);
            return (false, null, "Waveform generation took too long.");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // process already gone
            }
        }

        var stderr = await stderrTask;
        if (process.ExitCode != 0 || peaks.Count == 0)
        {
            _logger.LogError("FFmpeg waveform extraction failed (exit {ExitCode}): {Error}", process.ExitCode, stderr);
            return (false, null, "Could not decode the audio to build the waveform.");
        }

        return (true, peaks.ToArray(), string.Empty);
    }

    // sqrt compresses the dynamic range: quiet speech becomes visible, silence stays near zero.
    private static byte EncodePeak(int maxAbs)
    {
        var value = Math.Round(255.0 * Math.Sqrt(maxAbs / 32768.0));
        return (byte)Math.Clamp(value, 0, 255);
    }

    private async Task<WaveformData?> TryReadCacheAsync(string cachePath, long sourceLength, double sourceDuration)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;

            var json = await File.ReadAllTextAsync(cachePath);
            var entry = JsonSerializer.Deserialize<WaveformCacheEntry>(json);

            if (entry?.Data == null
                || entry.Version != CacheVersion
                || entry.SourceLength != sourceLength
                || Math.Abs(entry.SourceDuration - sourceDuration) > 0.001
                || entry.Data.PeaksPerSecond != PeaksPerSecond
                || string.IsNullOrEmpty(entry.Data.Peaks))
            {
                return null;
            }

            return entry.Data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable waveform cache {CachePath}", cachePath);
            return null;
        }
    }

    private async Task TryWriteCacheAsync(string cachePath, long sourceLength, double sourceDuration, WaveformData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

            var entry = new WaveformCacheEntry
            {
                Version = CacheVersion,
                SourceLength = sourceLength,
                SourceDuration = sourceDuration,
                Data = data
            };

            var tmpPath = cachePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(entry));
            File.Move(tmpPath, cachePath, true);
        }
        catch (Exception ex)
        {
            // The cache is an optimisation only; never fail the request because of it.
            _logger.LogWarning(ex, "Could not write waveform cache {CachePath}", cachePath);
        }
    }

    internal sealed class WaveformCacheEntry
    {
        public int Version { get; set; }
        public long SourceLength { get; set; }
        public double SourceDuration { get; set; }
        public WaveformData? Data { get; set; }
    }
}
