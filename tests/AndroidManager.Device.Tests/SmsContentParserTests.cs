using AndroidManager.Messages.Services;

namespace AndroidManager.Device.Tests;

public class SmsContentParserTests
{
    [Fact]
    public void ParseRowFields_PreservesCommasInBody()
    {
        const string payload =
            "_id=12, address=+905551112233, body=Merhaba, nasılsın?, date=1710000000000, type=1, read=1, thread_id=3";

        var row = SmsService.ParseRowFields(payload);

        Assert.Equal("12", row["_id"]);
        Assert.Equal("+905551112233", row["address"]);
        Assert.Equal("Merhaba, nasılsın?", row["body"]);
        Assert.Equal("1710000000000", row["date"]);
        Assert.Equal("1", row["type"]);
    }

    [Fact]
    public void ParseContentRows_StripsRowIndex()
    {
        const string raw = """
            Row: 0 _id=1, address=555, body=Hi, date=100, type=2, read=1, thread_id=1
            Row: 1 _id=2, address=555, body=Yo, date=200, type=1, read=0, thread_id=1
            """;

        var rows = SmsService.ParseContentRows(raw);
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["_id"]);
        Assert.Equal("Hi", rows[0]["body"]);
        Assert.Equal("Yo", rows[1]["body"]);
    }
}
