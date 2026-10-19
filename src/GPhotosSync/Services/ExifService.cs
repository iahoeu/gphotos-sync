using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GPhotosSync.Data;

namespace GPhotosSync.Services;

public static class ExifService
{
    private const int PropertyTagExifDTOrig = 0x9003; // DateTimeOriginal
    private const int PropertyTagDateTime = 0x0132;   // DateTime

    private static readonly Regex FilenameDateRegex = new(
        @"(?<year>20\d{2})[-_]?(?<month>0[1-9]|1[0-2])[-_]?(?<day>0[1-9]|[12]\d|3[01])[-_T]?(?<hour>[01]\d|2[0-3])?(?<min>[0-5]\d)?(?<sec>[0-5]\d)?",
        RegexOptions.Compiled);

    private static readonly HashSet<string> KnownVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".webm", ".mts", ".m2ts", ".mpg", ".mpeg", ".wmv"
    };

    private static readonly HashSet<string> KnownImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".gif", ".bmp", ".tif", ".tiff", ".dng", ".raw", ".cr2", ".nef", ".arw"
    };

    public static DateTime? ApplyCaptureTimestampToFile(string filePath, DateTime? fallbackDate = null)
    {
        if (!File.Exists(filePath)) return fallbackDate;

        DateTime? captureDate = TryReadExifDate(filePath)
                                ?? TryParseDateFromFileName(Path.GetFileNameWithoutExtension(filePath))
                                ?? fallbackDate;

        if (captureDate.HasValue && captureDate.Value.Year >= 1995 && captureDate.Value <= DateTime.Now.AddDays(1))
        {
            try
            {
                File.SetCreationTime(filePath, captureDate.Value);
                File.SetLastWriteTime(filePath, captureDate.Value);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Nelze nastavit časové razítko souboru '{Path.GetFileName(filePath)}': {ex.Message}");
            }
        }

        return captureDate;
    }

    public static DateTime? TryReadExifDate(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".tif" or ".tiff"))
            return null;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var img = Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);

            int[] tags = [PropertyTagExifDTOrig, PropertyTagDateTime];
            foreach (var tag in tags)
            {
                if (!img.PropertyIdList.Contains(tag)) continue;
                var prop = img.GetPropertyItem(tag);
                if (prop?.Value == null) continue;

                var str = Encoding.ASCII.GetString(prop.Value).Trim('\0', ' ');
                if (DateTime.TryParseExact(str, "yyyy:MM:dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                {
                    return dt;
                }
            }
        }
        catch
        {
            // Ignore non-standard EXIF headers
        }

        return null;
    }

    public static DateTime? TryParseDateFromFileName(string fileName)
    {
        var match = FilenameDateRegex.Match(fileName);
        if (!match.Success) return null;

        try
        {
            int year = int.Parse(match.Groups["year"].Value);
            int month = int.Parse(match.Groups["month"].Value);
            int day = int.Parse(match.Groups["day"].Value);
            int hour = match.Groups["hour"].Success ? int.Parse(match.Groups["hour"].Value) : 12;
            int min = match.Groups["min"].Success ? int.Parse(match.Groups["min"].Value) : 0;
            int sec = match.Groups["sec"].Success ? int.Parse(match.Groups["sec"].Value) : 0;

            return new DateTime(year, month, day, hour, min, sec, DateTimeKind.Local);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Vypočítá kryptografický otisk (SHA-256) z celého obsahu souboru (P0-1 / Bod 1).
    /// </summary>
    public static string ComputeFastFileHash(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return string.Empty;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1024 * 128);
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(fs);
            return Convert.ToHexString(hashBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// P1-11 & Bod 11: Ověří, že stažený soubor není prázdný, není chybovou HTML stránkou a v případě videa
    /// nejde o statický náhledový obrázek (např. JPEG/PNG/WebP/GIF). Bez přetečení pro soubory > 2 GiB.
    /// </summary>
    public static (bool IsValid, string? ErrorMessage) ValidateDownloadedMediaFile(
        string filePath,
        string fileName,
        bool expectedVideo)
    {
        if (!File.Exists(filePath))
        {
            return (false, "Stažený soubor neexistuje na disku.");
        }

        var fi = new FileInfo(filePath);
        if (fi.Length <= 0)
        {
            return (false, "Stažený soubor je prázdný (0 B).");
        }

        int headerLen = (int)Math.Min(512L, fi.Length);
        byte[] header = new byte[headerLen];
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = fs.Read(header, 0, header.Length);
            if (read < header.Length)
            {
                Array.Resize(ref header, read);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Nelze přečíst hlavičku staženého souboru: {ex.Message}");
        }

        // Check for HTML error page disguised as media download
        var asciiPrefix = Encoding.ASCII.GetString(header).TrimStart();
        if (asciiPrefix.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
            asciiPrefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Namísto mediálního souboru byla stažena HTML stránka.");
        }

        bool isImageHeader = IsStaticImageHeader(header);
        var ext = Path.GetExtension(fileName);

        // Ověření obsahu: textová odpověď (JSON, HTML, prostý text) místo média se nesmí uložit jako stažená fotka.
        // Neznámý binární formát (vzácné RAW, starší kontejnery) se přijme s varováním, aby se stahování
        // takové položky nezablokovalo napořád.
        if (!IsKnownMediaHeader(header))
        {
            if (LooksLikeText(header))
            {
                return (false, $"Namísto fotky nebo videa byl stažen textový soubor (pravděpodobně chybová odpověď) – '{fileName}'.");
            }
            Logger.Warn($"Stažený soubor '{fileName}' má nerozpoznanou hlavičku formátu – přijímám ho jako binární médium.");
        }

        if (expectedVideo)
        {
            if (isImageHeader || (KnownImageExtensions.Contains(ext) && !KnownVideoExtensions.Contains(ext)))
            {
                return (false, $"Položka je video, ale stažený soubor '{fileName}' je pouze statický obrázek (náhled).");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Hlavička vypadá jako text (JSON, HTML, XML, prostý text): žádné nulové bajty a téměř jen tisknutelné znaky.
    /// Hlavičky fotek a videí obsahují nulové a řídicí bajty.
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> h)
    {
        if (h.Length == 0)
            return true;

        int printable = 0;
        foreach (var b in h)
        {
            if (b == 0x00)
                return false;
            if (b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E) || b >= 0x80)
                printable++;
        }
        return printable >= h.Length * 0.95;
    }

    public static bool IsKnownMediaHeader(ReadOnlySpan<byte> h)
    {
        if (IsStaticImageHeader(h))
            return true;

        static bool AtSpan(ReadOnlySpan<byte> span, int offset, string ascii)
        {
            if (span.Length < offset + ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
                if (span[offset + i] != (byte)ascii[i]) return false;
            return true;
        }

        // TIFF a RAW založené na TIFF (DNG, CR2, NEF, ARW): "II*\0" / "MM\0*"; Olympus ORF "IIRO"/"IIRS"; Panasonic RW2 "IIU\0"
        if (h.Length >= 4 && h[0] == 0x49 && h[1] == 0x49 &&
            ((h[2] == 0x2A && h[3] == 0x00) || (h[2] == (byte)'R' && (h[3] == (byte)'O' || h[3] == (byte)'S')) || (h[2] == (byte)'U' && h[3] == 0x00)))
            return true;
        if (h.Length >= 4 && h[0] == 0x4D && h[1] == 0x4D && h[2] == 0x00 && h[3] == 0x2A)
            return true;
        // BMP ("BM" + rezervované nulové bajty 6–9)
        if (h.Length >= 10 && AtSpan(h, 0, "BM") && h[6] == 0 && h[7] == 0 && h[8] == 0 && h[9] == 0)
            return true;
        // ISO BMFF (HEIC/HEIF/AVIF, MP4, MOV, 3GP, CR3) a starší QuickTime atomy
        if (AtSpan(h, 4, "ftyp") || AtSpan(h, 4, "moov") || AtSpan(h, 4, "mdat") || AtSpan(h, 4, "wide") || AtSpan(h, 4, "free") ||
            AtSpan(h, 4, "skip") || AtSpan(h, 4, "pnot") || AtSpan(h, 4, "junk") || AtSpan(h, 4, "uuid") || AtSpan(h, 4, "styp"))
            return true;
        // MPEG-1/2 video (elementary stream)
        if (h.Length >= 4 && h[0] == 0x00 && h[1] == 0x00 && h[2] == 0x01 && h[3] == 0xB3)
            return true;
        // JPEG XL (codestream FF 0A nebo kontejner "JXL ")
        if ((h.Length >= 2 && h[0] == 0xFF && h[1] == 0x0A) || AtSpan(h, 4, "JXL "))
            return true;
        // AVI (RIFF....AVI )
        if (AtSpan(h, 0, "RIFF") && AtSpan(h, 8, "AVI "))
            return true;
        // Matroska / WebM
        if (h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3)
            return true;
        // MPEG-TS (MTS): synchronizační bajt 0x47 každých 188 B; M2TS: každých 192 B s posunem 4
        if ((h.Length >= 189 && h[0] == 0x47 && h[188] == 0x47) ||
            (h.Length >= 197 && h[4] == 0x47 && h[196] == 0x47))
            return true;
        // MPEG-PS
        if (h.Length >= 4 && h[0] == 0x00 && h[1] == 0x00 && h[2] == 0x01 && h[3] == 0xBA)
            return true;
        // ASF / WMV
        if (h.Length >= 4 && h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75)
            return true;
        // Fujifilm RAF
        if (AtSpan(h, 0, "FUJIFILM"))
            return true;

        return false;
    }

    public static bool IsStaticImageHeader(ReadOnlySpan<byte> header)
    {
        // JPEG: FF D8 FF
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return true;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (header.Length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return true;

        // GIF: GIF87a or GIF89a
        if (header.Length >= 6 &&
            header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'8')
            return true;

        // WEBP: RIFF....WEBP
        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return true;

        return false;
    }
}
