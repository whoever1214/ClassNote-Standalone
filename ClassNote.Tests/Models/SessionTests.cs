using ClassNote.Models;
using Xunit;

namespace ClassNote.Tests.Models;

public class SessionTests
{
    [Theory]
    [InlineData(2026, 8, 31, "周一")]
    [InlineData(2026, 9, 1, "周二")]
    [InlineData(2026, 9, 2, "周三")]
    [InlineData(2026, 9, 3, "周四")]
    [InlineData(2026, 9, 4, "周五")]
    [InlineData(2026, 9, 5, "周六")]
    [InlineData(2026, 9, 6, "周日")]
    [InlineData(2026, 8, 30, "周日")]
    public void WeekdayTextOf_MapsEveryDayOfWeek(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, Session.WeekdayTextOf(new DateTime(year, month, day, 23, 34, 0)));
    }

    [Fact]
    public void WeekdayText_ReadsFromStartTime()
    {
        // 记录列表里的时间戳就是 StartTime，星期几必须跟它一致
        var session = new Session { StartTime = new DateTime(2026, 8, 30, 23, 34, 0) };
        Assert.Equal("周日", session.WeekdayText);
    }
}
