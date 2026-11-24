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

    private static string Number(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
}
