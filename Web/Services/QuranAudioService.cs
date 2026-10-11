using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

/// <summary>
/// Lists and serves the per-ayah mp3 files stored under wwwroot/SoundSheikh/{sheikhId}/{surahFolder}/N.mp3.
///
/// The server does no audio processing at all. The browser downloads the files, keeps them in its own
/// storage, and does the joining, timing and waveform itself (wwwroot/js/quraan-player.js).
///
/// The surah folder is found by trying, in order: the full surah id ("59-Al-Hashr"),
/// the 000-padded number ("059"), then the plain number ("59").
/// </summary>
public class QuranAudioService : IQuranAudioService
{
    private static readonly Regex SurahNumberPattern = new(@"^(\d{1,3})", RegexOptions.Compiled);

    private readonly string _soundRoot;

    private sealed record AyahFile(int N, string FilePath);

    public QuranAudioService(IWebHostEnvironment env)
    {
        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        _soundRoot = Path.GetFullPath(Path.Combine(webRoot, "SoundSheikh"));
    }

    public QuranFilesResult GetFiles(string sheikhId, string surahId)
    {
        try
        {
            var folder = ResolveFolder(sheikhId, surahId);
            var result = new QuranFilesResult { Found = true };

            foreach (var file in ListAyat(folder))
            {
                var info = new FileInfo(file.FilePath);
                result.Ayat.Add(new QuranFileInfo
                {
                    N = file.N,
                    Url = "/api/quranaudio/ayah?sheikhId=" + Uri.EscapeDataString(sheikhId)
                          + "&surahId=" + Uri.EscapeDataString(surahId)
                          + "&n=" + file.N.ToString(CultureInfo.InvariantCulture),
                    Size = info.Length,
                    Version = info.Length.ToString(CultureInfo.InvariantCulture) + "-"
                              + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                });
            }

            return result;
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
}
