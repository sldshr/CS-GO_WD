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
}
