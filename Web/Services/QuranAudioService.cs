using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

/// <summary>
/// Serves the per-ayah mp3 files stored under wwwroot/SoundSheikh/{sheikhId}/{surahFolder}/N.mp3.
///
/// - The surah folder is found by trying, in order: the full surah id ("59-Al-Hashr"), the 000-padded
///   number ("059"), then the plain number ("59").
/// - A range of ayat is merged by FFmpeg into ONE mp3 cached under App_Data/QuranCache, so the browser gets a
///   normal seekable file. The cache key includes every source file's size and timestamp, so changing a source
///   file produces a new merged file automatically.
/// - Ayah boundaries inside the merged file are the exact decoded length of each source file.
/// - Waveform peaks are computed once per audio file and cached.
/// </summary>
public class QuranAudioService : IQuranAudioService
{
    private const int PeaksPerSecond = 25;
    private const int AnalysisSampleRate = 8000;
    private const int SamplesPerPeak = AnalysisSampleRate / PeaksPerSecond; // 320 samples = 40 ms
    private const int CacheVersion = 1;
    private const int MaxRange = 1000;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(2);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, double> DurationCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex TokenPattern = new("^[0-9a-f]{40}$", RegexOptions.Compiled);
    private static readonly Regex SurahNumberPattern = new(@"^(\d{1,3})", RegexOptions.Compiled);

    private readonly AudioProcessingSettings _settings;
    private readonly ILogger<QuranAudioService> _logger;
    private readonly SemaphoreSlim _probeGate = new(4, 4);
    private readonly string _soundRoot;
    private readonly string _mergedDir;
    private readonly string _peaksDir;

    private sealed record AyahFile(int N, string FilePath);

    public QuranAudioService(
        IWebHostEnvironment env,
        IOptions<AudioProcessingSettings> settings,
        ILogger<QuranAudioService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _soundRoot = Path.GetFullPath(Path.Combine(webRoot, "SoundSheikh"));

        var cacheRoot = Path.Combine(env.ContentRootPath, "App_Data", "QuranCache");
        _mergedDir = Path.Combine(cacheRoot, "merged");
        _peaksDir = Path.Combine(cacheRoot, "peaks");
    }

    // =====================================================================
    // public API
    // =====================================================================

    public QuranFilesResult GetFiles(string sheikhId, string surahId)
    {
        try
        {
            var folder = ResolveFolder(sheikhId, surahId);
            var ayat = ListAyat(folder).Select(a => a.N).ToList();
            return new QuranFilesResult { Found = true, Ayat = ayat };
        }
        catch (QuranAudioException ex) when (ex.StatusCode == 404)
        {
            return new QuranFilesResult { Found = false, Message = ex.Message };
        }
    }

    public string GetAyahFilePath(string sheikhId, string surahId, int n)
    {
        var folder = ResolveFolder(sheikhId, surahId);
        var file = ListAyat(folder).FirstOrDefault(a => a.N == n);
        if (file == null)
        {
            throw new QuranAudioException(404, "لا يوجد ملف صوت لهذه الآية.");
        }

        return file.FilePath;
    }

    public string GetMergedFilePath(string token)
    {
        if (string.IsNullOrEmpty(token) || !TokenPattern.IsMatch(token))
        {
            throw new QuranAudioException(404, "الملف غير موجود.");
        }

        var path = Path.Combine(_mergedDir, token + ".mp3");
        if (!File.Exists(path))
        {
            throw new QuranAudioException(404, "الملف غير موجود. أعد تحميل الصفحة.");
        }

        return path;
    }

    public async Task<QuranAudioResult> GetAudioAsync(string sheikhId, string surahId, int from, int to)
    {
        var (result, _) = await ResolveAudioAsync(sheikhId, surahId, from, to);
        return result;
    }

    public async Task<WaveformData> GetWaveformAsync(string sheikhId, string surahId, int from, int to)
    {
        var (audio, physicalPath) = await ResolveAudioAsync(sheikhId, surahId, from, to);

        var info = new FileInfo(physicalPath);
        var key = Sha1Hex($"peaks-v{CacheVersion}|{physicalPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        var cachePath = Path.Combine(_peaksDir, key + ".json");

        var cached = TryReadPeaksCache(cachePath);
        if (cached != null)
        {
            cached.Duration = audio.Duration;
            return cached;
        }

        var gate = Locks.GetOrAdd("peaks:" + key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            cached = TryReadPeaksCache(cachePath);
            if (cached != null)
            {
                cached.Duration = audio.Duration;
                return cached;
            }

            var peaks = await ExtractPeaksAsync(physicalPath);
            var data = new WaveformData
            {
                PeaksPerSecond = PeaksPerSecond,
                Duration = audio.Duration,
                Peaks = Convert.ToBase64String(peaks)
            };

            WriteJsonAtomically(cachePath, data);
            TrimCache();
            return data;
        }
        finally
        {
            gate.Release();
        }
    }

    // =====================================================================
    // folder + file discovery
    // =====================================================================

    private static bool IsSafeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120) return false;
        if (value != value.Trim()) return false;
        if (value == "." || value == "..") return false;
        if (value.Contains("..", StringComparison.Ordinal)) return false;
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (value.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;
        return true;
    }

    private static int ParseSurahNumber(string surahId)
    {
        var match = SurahNumberPattern.Match(surahId);
        if (!match.Success || !int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            throw new QuranAudioException(400, "معرّف السورة غير صالح.");
        }

        return number;
    }

    private static bool IsInside(string parent, string child)
    {
        var parentWithSeparator = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveFolder(string sheikhId, string surahId)
    {
        if (!IsSafeName(sheikhId) || !IsSafeName(surahId))
        {
            throw new QuranAudioException(400, "معرّف القارئ أو السورة غير صالح.");
        }

        var number = ParseSurahNumber(surahId);

        var sheikhDir = Path.GetFullPath(Path.Combine(_soundRoot, sheikhId));
        if (!IsInside(_soundRoot, sheikhDir) || !Directory.Exists(sheikhDir))
        {
            throw new QuranAudioException(404, "لم يتم العثور على مجلد القارئ.");
        }

        var candidates = new[]
        {
            surahId,
            number.ToString("000", CultureInfo.InvariantCulture),
            number.ToString(CultureInfo.InvariantCulture)
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in candidates)
        {
            var dir = Path.GetFullPath(Path.Combine(sheikhDir, name));
            if (!IsInside(sheikhDir, dir) || !Directory.Exists(dir)) continue;
            if (ListAyat(dir).Count > 0) return dir;
        }

        throw new QuranAudioException(404, "لا توجد ملفات صوت لهذه السورة عند هذا القارئ.");
    }

    // N.mp3 files in numeric order ("1.mp3", "2.mp3", ...; "001.mp3" is accepted too).
    private static List<AyahFile> ListAyat(string folder)
    {
        var byNumber = new SortedDictionary<int, string>();

        foreach (var file in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(Path.GetExtension(file), ".mp3", StringComparison.OrdinalIgnoreCase)) continue;

            var name = Path.GetFileNameWithoutExtension(file);
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0) continue;

            if (!byNumber.ContainsKey(n))
            {
                byNumber[n] = file;
            }
        }

        return byNumber.Select(kv => new AyahFile(kv.Key, kv.Value)).ToList();
    }

    // =====================================================================
    // audio for a range (single file or merged file)
    // =====================================================================

    private async Task<(QuranAudioResult Result, string PhysicalPath)> ResolveAudioAsync(
        string sheikhId, string surahId, int from, int to)
    {
        if (from < 1 || to < from)
        {
            throw new QuranAudioException(400, "مدى الآيات غير صالح.");
        }

        if (to - from >= MaxRange)
        {
            throw new QuranAudioException(400, "مدى الآيات كبير جدًا.");
        }

        var folder = ResolveFolder(sheikhId, surahId);
        var files = ListAyat(folder).Where(a => a.N >= from && a.N <= to).ToList();
        if (files.Count == 0)
        {
            throw new QuranAudioException(404, "لا توجد ملفات صوت في هذا المدى.");
        }

        var have = new HashSet<int>(files.Select(f => f.N));
        var missing = new List<int>();
        for (var n = from; n <= to && missing.Count < 50; n++)
        {
            if (!have.Contains(n)) missing.Add(n);
        }

        // one file: play it as it is, no merging
        if (files.Count == 1)
        {
            var only = files[0];
            var duration = await MeasureDurationAsync(only.FilePath);

            var single = new QuranAudioResult
            {
                Kind = "single",
                Url = "/api/quranaudio/ayah?sheikhId=" + Uri.EscapeDataString(sheikhId)
                      + "&surahId=" + Uri.EscapeDataString(surahId)
                      + "&n=" + only.N.ToString(CultureInfo.InvariantCulture),
                Duration = duration,
                Segments = new List<QuranSegment> { new() { N = only.N, Start = 0, End = Math.Round(duration, 3) } },
                Missing = missing
            };
            return (single, only.FilePath);
        }

        var token = ComputeToken(sheikhId, folder, files);
        var mergedPath = Path.Combine(_mergedDir, token + ".mp3");
        var manifestPath = Path.Combine(_mergedDir, token + ".json");

        var merged = TryReadManifest(manifestPath, mergedPath);
        if (merged == null)
        {
            var gate = Locks.GetOrAdd("merge:" + token, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                merged = TryReadManifest(manifestPath, mergedPath);
                if (merged == null)
                {
                    merged = await BuildMergedAsync(files, token, mergedPath, manifestPath);
                    TrimCache();
                }
            }
            finally
            {
                gate.Release();
            }
        }

        merged.Kind = "merged";
        merged.Url = "/api/quranaudio/merged/" + token;
        merged.Missing = missing;
        return (merged, mergedPath);
    }

    private static string ComputeToken(string sheikhId, string folder, List<AyahFile> files)
    {
        var sb = new StringBuilder();
        sb.Append("merged-v").Append(CacheVersion).Append('|')
          .Append(sheikhId.ToLowerInvariant()).Append('|')
          .Append(Path.GetFileName(folder).ToLowerInvariant());

        foreach (var f in files)
        {
            var info = new FileInfo(f.FilePath);
            sb.Append('|').Append(f.N).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
        }

        return Sha1Hex(sb.ToString());
    }

    private static string Sha1Hex(string value)
    {
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private QuranAudioResult? TryReadManifest(string manifestPath, string mergedPath)
    {
        try
        {
            if (!File.Exists(manifestPath) || !File.Exists(mergedPath)) return null;

            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            var result = JsonSerializer.Deserialize<QuranAudioResult>(json);
            if (result == null || result.Segments.Count == 0 || result.Duration <= 0) return null;

            File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow); // "recently used" marker for TrimCache
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable merged manifest {Path}", manifestPath);
            return null;
        }
    }

    private async Task<QuranAudioResult> BuildMergedAsync(
        List<AyahFile> files, string token, string mergedPath, string manifestPath)
    {
        Directory.CreateDirectory(_mergedDir);
        _logger.LogInformation("Merging {Count} ayah files into {Path}", files.Count, mergedPath);

        // 1) exact length of every file, so ayah boundaries match the merged audio
        var durations = await Task.WhenAll(files.Select(f => MeasureDurationAsync(f.FilePath)));

        var segments = new List<QuranSegment>();
        double cursor = 0;
        for (var i = 0; i < files.Count; i++)
        {
            segments.Add(new QuranSegment
            {
                N = files[i].N,
                Start = Math.Round(cursor, 3),
                End = Math.Round(cursor + durations[i], 3)
            });
            cursor += durations[i];
        }

        // 2) concat list + one re-encode (handles files with different sample rate/channels)
        var listPath = Path.Combine(_mergedDir, token + ".list.txt");
        var partPath = mergedPath + ".part";

        try
        {
            var lines = files.Select(f => "file '" + f.FilePath.Replace('\\', '/').Replace("'", "'\\''") + "'");
            await File.WriteAllLinesAsync(listPath, lines, new UTF8Encoding(false));

            var args = new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "concat", "-safe", "0", "-i", listPath,
                "-vn", "-map", "0:a:0", "-map_metadata", "-1",
                "-ar", "44100", "-ac", "2",
                "-c:a", "libmp3lame", "-b:a", "128k",
                "-f", "mp3", partPath
            };

            var (exitCode, _, stderr) = await RunToolAsync(_settings.FfmpegExecutable, args, ToolTimeout, null);
            if (exitCode != 0 || !File.Exists(partPath) || new FileInfo(partPath).Length == 0)
            {
                _logger.LogError("FFmpeg merge failed (exit {ExitCode}): {Error}", exitCode, stderr);
                throw new QuranAudioException(500, "تعذّر دمج ملفات الصوت.");
            }

            File.Move(partPath, mergedPath, true);
        }
        finally
        {
            TryDelete(listPath);
            TryDelete(partPath);
        }

        // 3) the manifest is written last: its existence means the merged file is complete
        var result = new QuranAudioResult
        {
            Kind = "merged",
            Duration = Math.Round(cursor, 3),
            Segments = segments
        };
        WriteJsonAtomically(manifestPath, result);
        return result;
    }

    // =====================================================================
    // FFmpeg helpers
    // =====================================================================

    private async Task<double> MeasureDurationAsync(string path)
    {
        var info = new FileInfo(path);
        var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (DurationCache.TryGetValue(key, out var known)) return known;

        await _probeGate.WaitAsync();
        try
        {
            // decode to 8 kHz mono PCM and count the bytes: the exact length the merged file will have
            var args = new[]
            {
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-i", "file:" + path,
                "-vn", "-map", "0:a:0", "-ac", "1",
                "-ar", AnalysisSampleRate.ToString(CultureInfo.InvariantCulture),
                "-f", "s16le", "pipe:1"
            };

            var (exitCode, bytes, stderr) = await RunToolAsync(_settings.FfmpegExecutable, args, ToolTimeout, null);
            if (exitCode != 0 || bytes < 2)
            {
                _logger.LogError("Could not measure {Path} (exit {ExitCode}): {Error}", path, exitCode, stderr);
                throw new QuranAudioException(500, "تعذّر قراءة الملف الصوتي " + Path.GetFileName(path) + ".");
            }

            var seconds = bytes / 2.0 / AnalysisSampleRate;
            DurationCache[key] = seconds;
            return seconds;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<byte[]> ExtractPeaksAsync(string path)
    {
        var args = new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-i", "file:" + path,
            "-vn", "-map", "0:a:0", "-ac", "1",
            "-ar", AnalysisSampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le", "pipe:1"
        };

        var peaks = new List<byte>(64 * 1024);
        var pendingLowByte = -1;   // first byte of a 16-bit sample that was split across two reads
        var samplesInBucket = 0;
        var bucketMax = 0;

        void Feed(byte[] buffer, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (pendingLowByte < 0)
                {
                    pendingLowByte = buffer[i];
                    continue;
                }

                var sample = (short)(pendingLowByte | (buffer[i] << 8));
                pendingLowByte = -1;

                var abs = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
                if (abs > bucketMax) bucketMax = abs;

                if (++samplesInBucket == SamplesPerPeak)
                {
                    peaks.Add(EncodePeak(bucketMax));
                    bucketMax = 0;
                    samplesInBucket = 0;
                }
            }
        }

        var (exitCode, _, stderr) = await RunToolAsync(_settings.FfmpegExecutable, args, ToolTimeout, Feed);

        if (samplesInBucket > 0)
        {
            peaks.Add(EncodePeak(bucketMax));
        }

        if (exitCode != 0 || peaks.Count == 0)
        {
            _logger.LogError("Waveform extraction failed for {Path} (exit {ExitCode}): {Error}", path, exitCode, stderr);
            throw new QuranAudioException(500, "تعذّر بناء شكل الموجة.");
        }

        return peaks.ToArray();
    }

    // sqrt keeps quiet recitation visible while silence stays near zero
    private static byte EncodePeak(int maxAbs)
    {
        var value = Math.Round(255.0 * Math.Sqrt(maxAbs / 32768.0));
        return (byte)Math.Clamp(value, 0, 255);
    }

    private async Task<(int ExitCode, long StdoutBytes, string Stderr)> RunToolAsync(
        string executable, IEnumerable<string> arguments, TimeSpan timeout, Action<byte[], int>? onStdout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        using var timeoutSource = new CancellationTokenSource(timeout);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start {Executable}", executable);
            throw new QuranAudioException(500, "FFmpeg غير متاح على الخادم.");
        }

        using var killOnTimeout = timeoutSource.Token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
        });

        var stderrTask = process.StandardError.ReadToEndAsync();
        long total = 0;

        try
        {
            var stream = process.StandardOutput.BaseStream;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), timeoutSource.Token)) > 0)
            {
                total += read;
                onStdout?.Invoke(buffer, read);
            }

            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("{Executable} timed out after {Minutes} minutes", executable, timeout.TotalMinutes);
            throw new QuranAudioException(500, "استغرقت معالجة الصوت وقتًا أطول من المسموح.");
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
        }

        var stderr = await stderrTask;
        return (process.ExitCode, total, stderr);
    }

    // =====================================================================
    // cache helpers
    // =====================================================================

    private WaveformData? TryReadPeaksCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;

            var data = JsonSerializer.Deserialize<WaveformData>(File.ReadAllText(cachePath, Encoding.UTF8));
            if (data == null || data.PeaksPerSecond != PeaksPerSecond || string.IsNullOrEmpty(data.Peaks)) return null;

            File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring unreadable peaks cache {Path}", cachePath);
            return null;
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    // removes merged files and peaks that have not been used for CacheMaxAge
    public void TrimCache()
    {
        try
        {
            var limit = DateTime.UtcNow - CacheMaxAge;

            if (Directory.Exists(_mergedDir))
            {
                foreach (var manifest in Directory.EnumerateFiles(_mergedDir, "*.json"))
                {
                    if (File.GetLastWriteTimeUtc(manifest) >= limit) continue;
                    TryDelete(Path.ChangeExtension(manifest, ".mp3"));
                    TryDelete(manifest);
                }
            }

            if (Directory.Exists(_peaksDir))
            {
                foreach (var file in Directory.EnumerateFiles(_peaksDir, "*.json"))
                {
                    if (File.GetLastWriteTimeUtc(file) < limit) TryDelete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache cleanup failed");
        }
    }
}
