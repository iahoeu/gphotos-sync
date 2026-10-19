using System.Drawing;
using System.Globalization;
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

    private static DateTime? TryReadExifDate(string filePath)
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
}
