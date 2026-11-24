using System.Globalization;

namespace CsgoWorkshop.Core;

public static class Format
{
    public static string Size(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;

        return bytes switch
        {
            >= (long)gb => Number(bytes / gb) + " ГБ",
            >= (long)mb => Number(bytes / mb) + " МБ",
            >= (long)kb => Number(bytes / kb) + " КБ",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " Б",
        };
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond))
            return "";

        const double kb = 1024;
        const double mb = kb * 1024;

        return bytesPerSecond switch
        {
            >= mb => Number(bytesPerSecond / mb) + " МБ/с",
            >= kb => Math.Round(bytesPerSecond / kb).ToString(CultureInfo.InvariantCulture) + " КБ/с",
            _ => Math.Max(1, Math.Round(bytesPerSecond)).ToString(CultureInfo.InvariantCulture) + " Б/с",
        };
    }

    public static string Duration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        var totalMinutes = (int)elapsed.TotalMinutes;
        return $"{totalMinutes}:{elapsed.Seconds:00}";
    }

    private static string Number(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
}
