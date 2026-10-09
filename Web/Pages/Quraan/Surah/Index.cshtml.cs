using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SoundChunksWeb.Pages.Quraan.Surah;

public class IndexModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? SheikhId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sheikh { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? SheikhImage { get; set; }

    public string SurahName { get; set; } = string.Empty;
    public string AudioFilePath { get; set; } = string.Empty;

    public void OnGet()
    {
        // Parse surah ID to get name
        SurahName = GetSurahName(Id);

        // Decode URL parameters
        if (!string.IsNullOrEmpty(SheikhId))
        {
            SheikhId = System.Net.WebUtility.UrlDecode(SheikhId);
        }

        if (!string.IsNullOrEmpty(Sheikh))
        {
            Sheikh = System.Net.WebUtility.UrlDecode(Sheikh);
        }

        if (!string.IsNullOrEmpty(SheikhImage))
        {
            SheikhImage = System.Net.WebUtility.UrlDecode(SheikhImage);
        }

        // Construct audio file path if sheikh is selected
        if (!string.IsNullOrEmpty(SheikhId) && !string.IsNullOrEmpty(Id))
        {
            AudioFilePath = GetAudioFilePath(SheikhId, Id);
        }
    }

    private string GetAudioFilePath(string sheikhId, string surahId)
    {
        // Extract surah number
        var surahNumber = ExtractSurahNumber(surahId);
        if (string.IsNullOrEmpty(surahNumber))
        {
            return string.Empty;
        }

        // Remove leading zeros
        surahNumber = surahNumber.TrimStart('0');
        if (string.IsNullOrEmpty(surahNumber))
        {
            surahNumber = "0";
        }

        // Try different audio extensions
        var audioExtensions = new[] { ".mp3", ".wav", ".m4a", ".ogg", ".aac" };
        var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var audioFilesPath = Path.Combine(webRoot, "SoundSheikh", sheikhId);

        if (!Directory.Exists(audioFilesPath))
        {
            return string.Empty;
        }

        foreach (var ext in audioExtensions)
        {
            var filePath = Path.Combine(audioFilesPath, $"{surahNumber}{ext}");
            if (System.IO.File.Exists(filePath))
            {
                return $"/SoundSheikh/{sheikhId}/{surahNumber}{ext}";
            }
        }

        return string.Empty;
    }

    private string ExtractSurahNumber(string surahId)
    {
        // Extract the number part from the surah ID
        var match = System.Text.RegularExpressions.Regex.Match(surahId, @"^(\d+)");
        return match.Success ? match.Value : string.Empty;
    }

    private string GetSurahName(string id)
    {
        // Map surah IDs to Arabic names - add all 114 surahs as needed
        var surahNames = new Dictionary<string, string>
        {
            { "1-Al-Fatihah", "سورة الفاتحة" },
            { "2-Al-Baqarah", "سورة البقرة" },
            { "3-Ali-Imran", "سورة آل عمران" },
            { "4-An-Nisa", "سورة النساء" },
            { "5-Al-Maidah", "سورة المائدة" },
            { "6-Al-Anam", "سورة الأنعام" },
            { "7-Al-Araf", "سورة الأعراف" },
            { "8-Al-Anfal", "سورة الأنفال" },
            { "9-At-Tawbah", "سورة التوبة" },
            { "10-Yunus", "سورة يونس" },
            { "11-Hud", "سورة هود" },
            { "12-Yusuf", "سورة يوسف" },
            { "13-Ar-Rad", "سورة الرعد" },
            { "14-Ibrahim", "سورة إبراهيم" },
            { "15-Al-Hijr", "سورة الحجر" },
            { "16-An-Nahl", "سورة النحل" },
            { "17-Al-Isra", "سورة الإسراء" },
            { "18-Al-Kahf", "سورة الكهف" },
            { "19-Maryam", "سورة مريم" },
            { "20-Ta-Ha", "سورة طه" },
            { "21-Al-Anbya", "سورة الأنبياء" },
            { "22-Al-Hajj", "سورة الحج" },
            { "23-Al-Muminun", "سورة المؤمنون" },
            { "24-An-Nur", "سورة النور" },
            { "25-Al-Furqan", "سورة الفرقان" },
            { "26-Ash-Shuara", "سورة الشعراء" },
            { "27-An-Naml", "سورة النمل" },
            { "28-Al-Qasas", "سورة القصص" },
            { "29-Al-Ankabut", "سورة العنكبوت" },
            { "30-Ar-Rum", "سورة الروم" },
            { "31-Luqman", "سورة لقمان" },
            { "32-As-Sajdah", "سورة السجدة" },
            { "33-Al-Ahzab", "سورة الأحزاب" },
            { "34-Saba", "سورة سبأ" },
            { "35-Fatir", "سورة فاطر" },
            { "36-Ya-Sin", "سورة يس" },
            { "37-As-Saffat", "سورة الصافات" },
            { "38-Sad", "سورة ص" },
            { "39-Az-Zumar", "سورة الزمر" },
            { "40-Ghafir", "سورة غافر" },
            { "41-Fussilat", "سورة فصلت" },
            { "42-Ash-Shura", "سورة الشورى" },
            { "43-Az-Zukhruf", "سورة الزخرف" },
            { "44-Ad-Dukhan", "سورة الدخان" },
            { "45-Al-Jathiyah", "سورة الجاثية" },
            { "46-Al-Ahqaf", "سورة الأحقاف" },
            { "47-Muhammad", "سورة محمد" },
            { "48-Al-Fath", "سورة الفتح" },
            { "49-Al-Hujurat", "سورة الحجرات" },
            { "50-Qaf", "سورة ق" },
            { "51-Adh-Dhariyat", "سورة الذاريات" },
            { "52-At-Tur", "سورة الطور" },
            { "53-An-Najm", "سورة النجم" },
            { "54-Al-Qamar", "سورة القمر" },
            { "55-Ar-Rahman", "سورة الرحمن" },
            { "56-Al-Waqiah", "سورة الواقعة" },
            { "57-Al-Hadid", "سورة الحديد" },
            { "58-Al-Mujadila", "سورة المجادلة" },
            { "59-Al-Hashr", "سورة الحشر" },
            { "60-Al-Mumtahanah", "سورة الممتحنة" },
            { "61-As-Saff", "سورة الصف" },
            { "62-Al-Jumuah", "سورة الجمعة" },
            { "63-Al-Munafiqun", "سورة المنافقون" },
            { "64-At-Taghabun", "سورة التغابن" },
            { "65-At-Talaq", "سورة الطلاق" },
            { "66-At-Tahrim", "سورة التحريم" },
            { "67-Al-Mulk", "سورة الملك" },
            { "68-Al-Qalam", "سورة القلم" },
            { "69-Al-Haqqah", "سورة الحاقة" },
            { "70-Al-Maarij", "سورة المعارج" },
            { "71-Nuh", "سورة نوح" },
            { "72-Al-Jinn", "سورة الجن" },
            { "73-Al-Muzzammil", "سورة المزمل" },
            { "74-Al-Muddaththir", "سورة المدثر" },
            { "75-Al-Qiyamah", "سورة القيامة" },
            { "76-Al-Insan", "سورة الإنسان" },
            { "77-Al-Mursalat", "سورة المرسلات" },
            { "78-An-Naba", "سورة النبأ" },
            { "79-An-Naziat", "سورة النازعات" },
            { "80-Abasa", "سورة عبس" },
            { "81-At-Takwir", "سورة التكوير" },
            { "82-Al-Infitar", "سورة الانفطار" },
            { "83-Al-Mutaffifin", "سورة المطففين" },
            { "84-Al-Inshiqaq", "سورة الانشقاق" },
            { "85-Al-Buruj", "سورة البروج" },
            { "86-At-Tariq", "سورة الطارق" },
            { "87-Al-Ala", "سورة الأعلى" },
            { "88-Al-Ghashiyah", "سورة الغاشية" },
            { "89-Al-Fajr", "سورة الفجر" },
            { "90-Al-Balad", "سورة البلد" },
            { "91-Ash-Shams", "سورة الشمس" },
            { "92-Al-Layl", "سورة الليل" },
            { "93-Ad-Duha", "سورة الضحى" },
            { "94-Ash-Sharh", "سورة الشرح" },
            { "95-At-Tin", "سورة التين" },
            { "96-Al-Alaq", "سورة العلق" },
            { "97-Al-Qadr", "سورة القدر" },
            { "98-Al-Bayyinah", "سورة البينة" },
            { "99-Az-Zalzalah", "سورة الزلزلة" },
            { "100-Al-Adiyat", "سورة العاديات" },
            { "101-Al-Qariah", "سورة القارعة" },
            { "102-At-Takathur", "سورة التكاثر" },
            { "103-Al-Asr", "سورة العصر" },
            { "104-Al-Humazah", "سورة الهمزة" },
            { "105-Al-Fil", "سورة الفيل" },
            { "106-Quraysh", "سورة قريش" },
            { "107-Al-Maun", "سورة الماعون" },
            { "108-Al-Kawthar", "سورة الكوثر" },
            { "109-Al-Kafirun", "سورة الكافرون" },
            { "110-An-Nasr", "سورة النصر" },
            { "111-Al-Masad", "سورة المسد" },
            { "112-Al-Ikhlas", "سورة الإخلاص" },
            { "113-Al-Falaq", "سورة الفلق" },
            { "114-An-Nas", "سورة الناس" }
        };

        return surahNames.TryGetValue(id, out var name) ? name : id;
    }
}
