using Xunit;

namespace NightlyDawn.Core.Tests;

public class UnsupportedFilterFieldExceptionTests
{
    [Fact]
    public void CarriesTheFieldName_AndNamesItInTheMessage()
    {
        var ex = new UnsupportedFilterFieldException("user.nip05");

        Assert.Equal("user.nip05", ex.Field);
        Assert.Contains("user.nip05", ex.Message);
    }

    [Fact]
    public void IsNotAFilterParseException()
    {
        // The input parsed correctly -- the grammar accepts this field -- so calling it a parse error
        // would be false to both the caller and anyone reading the message (plan §1 item B).
        Assert.IsNotType<FilterParseException>(new UnsupportedFilterFieldException("reactions"));
    }
}
