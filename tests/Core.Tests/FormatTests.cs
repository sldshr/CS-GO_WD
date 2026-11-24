using CsgoWorkshop.Core;

namespace Core.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1023, "1023 Б")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(12_897_484, "12,3 МБ")]
    [InlineData(3_221_225_472, "3,0 ГБ")]
    public void FormatsSizes(long bytes, string expected) => Assert.Equal(expected, Format.Size(bytes));

    [Theory]
    [InlineData(0, "")]
    [InlineData(-10, "")]
    [InlineData(512, "512 Б/с")]
    [InlineData(204_800, "200 КБ/с")]
    [InlineData(5_767_168, "5,5 МБ/с")]
    public void FormatsSpeeds(double bps, string expected) => Assert.Equal(expected, Format.Speed(bps));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(65, "1:05")]
    [InlineData(754, "12:34")]
    public void FormatsDurations(int seconds, string expected) =>
        Assert.Equal(expected, Format.Duration(TimeSpan.FromSeconds(seconds)));
}
