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

        int headerLen = (int)Math.Min(64L, fi.Length);
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

        if (expectedVideo)
        {
            if (isImageHeader || (KnownImageExtensions.Contains(ext) && !KnownVideoExtensions.Contains(ext)))
            {
                return (false, $"Položka je video, ale stažený soubor '{fileName}' je pouze statický obrázek (náhled).");
            }
        }

        return (true, null);
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
