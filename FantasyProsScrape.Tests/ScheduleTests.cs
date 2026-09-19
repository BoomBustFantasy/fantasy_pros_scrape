using Quartz;

namespace FantasyProsScrape.Tests;

/// <summary>
/// Confirms the two <see cref="Jobs.FantasyProsRanksJob"/> cron schedules registered in Program.cs
/// fire at the expected America/Chicago instants, computed directly with Quartz's
/// <see cref="CronExpression"/> - no scheduler, no DI, no job execution. See
/// docs/plans/fantasy-pros-ranks-scraper.md, "Program.cs", for the two expressions:
///   TUE-SAT: "0 0 * ? * TUE-SAT"    (top of every hour, Tuesday through Saturday, all day)
///   SUN:     "0 0 0-11 ? * SUN"     (top of every hour, Sunday 00:00 through 11:00)
/// Together the two never fire on Monday or Sunday afternoon, which is exactly the window
/// FantasyPros' week never straddles (it rolls the week after Monday night).
/// </summary>
public class ScheduleTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    private const string TueSatCron = "0 0 * ? * TUE-SAT";
    private const string SunCron = "0 0 0-11 ? * SUN";

    private static CronExpression Build(string expression) => new(expression) { TimeZone = Chicago };

    /// <summary>-06:00 (CST) or -05:00 (CDT) as of the given Chicago-local instant.</summary>
    private static DateTimeOffset Ct(int year, int month, int day, int hour, int minute = 0) =>
        new DateTimeOffset(new DateTime(year, month, day, hour, minute, 0), Chicago.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0)));

    [Fact]
    public void TueSat_SkipsSundayAndMonday_NextFireIsTuesdayMidnight()
    {
        // Saturday night, 2026-09-19 23:30 CT (a normal, non-DST week).
        var cron = Build(TueSatCron);
        var saturdayNight = Ct(2026, 9, 19, 23, 30);

        var next = cron.GetTimeAfter(saturdayNight);

        Assert.NotNull(next);
        Assert.Equal(Ct(2026, 9, 22, 0, 0), next!.Value);
    }

    [Fact]
    public void TueSat_FiresEveryHourTuesdayThroughSaturday_NeverOnSundayOrMonday()
    {
        var cron = Build(TueSatCron);
        var t = Ct(2026, 9, 22, 0, 0).AddSeconds(-1); // just before Tuesday 00:00

        for (var i = 0; i < 5 * 24 + 1; i++) // Tue+Wed+Thu+Fri+Sat (120 fires) plus one more to roll into the next Tuesday
        {
            var next = cron.GetTimeAfter(t);
            Assert.NotNull(next);

            var local = TimeZoneInfo.ConvertTime(next!.Value, Chicago);
            Assert.NotEqual(DayOfWeek.Sunday, local.DayOfWeek);
            Assert.NotEqual(DayOfWeek.Monday, local.DayOfWeek);

            t = next.Value;
        }

        // The 121st fire after Tuesday 00:00 rolls past Saturday 23:00 into the following Tuesday,
        // confirming Sunday and Monday were skipped in full, not just at the boundary.
        var rollover = TimeZoneInfo.ConvertTime(t, Chicago);
        Assert.Equal(DayOfWeek.Tuesday, rollover.DayOfWeek);
    }

    [Fact]
    public void Sun_FiresHourlyFromMidnightThrough11AM_ThenSkipsToNextSunday()
    {
        // The ticket's specific boundary: Sunday 11:00 is the last fire of the day.
        var cron = Build(SunCron);
        var t = Ct(2026, 9, 19, 23, 30); // Saturday night before an ordinary Sunday

        var fireTimes = new List<DateTimeOffset>();
        for (var i = 0; i < 12; i++)
        {
            var next = cron.GetTimeAfter(t);
            Assert.NotNull(next);
            fireTimes.Add(next!.Value);
            t = next.Value;
        }

        // Exactly 00:00..11:00 on 2026-09-20, hourly.
        for (var hour = 0; hour <= 11; hour++)
            Assert.Contains(Ct(2026, 9, 20, hour), fireTimes);

        // The 13th fire must NOT be Sunday 12:00 - it must jump straight to next Sunday 00:00.
        var thirteenth = cron.GetTimeAfter(t);
        Assert.NotNull(thirteenth);
        Assert.Equal(Ct(2026, 9, 27, 0, 0), thirteenth!.Value);
    }

    [Fact]
    public void Sun_SpringForwardDst_SkipsTheNonexistentTwoAmHour()
    {
        // 2026-03-08 is the US spring-forward date: 01:59:59 CST jumps straight to 03:00:00 CDT,
        // so local 02:00-02:59 never occurs that day.
        var cron = Build(SunCron);
        var t = Ct(2026, 3, 7, 23, 30); // Saturday night CST (-06:00)

        var fireTimes = new List<DateTimeOffset>();
        for (var i = 0; i < 11; i++) // 00,01,03,04,05,06,07,08,09,10,11 -> 11 fires, 02:00 is skipped
        {
            var next = cron.GetTimeAfter(t);
            Assert.NotNull(next);
            fireTimes.Add(next!.Value);
            t = next.Value;
        }

        Assert.Equal(Ct(2026, 3, 8, 0, 0), fireTimes[0]); // still -06:00 (CST)
        Assert.Equal(Ct(2026, 3, 8, 1, 0), fireTimes[1]); // still -06:00 (CST)
        Assert.Equal(Ct(2026, 3, 8, 3, 0), fireTimes[2]); // jumps straight to -05:00 (CDT); no 02:00 fire
        Assert.Equal(Ct(2026, 3, 8, 11, 0), fireTimes[^1]);

        // No fire ever lands on the nonexistent local 02:00-02:59 window.
        Assert.DoesNotContain(fireTimes, f => TimeZoneInfo.ConvertTime(f, Chicago).Hour == 2 && TimeZoneInfo.ConvertTime(f, Chicago).Day == 8);

        // The next fire after 11:00 is next Sunday, not 12:00 the same day.
        var next12 = cron.GetTimeAfter(t);
        Assert.NotNull(next12);
        Assert.Equal(Ct(2026, 3, 15, 0, 0), next12!.Value);
    }

    [Fact]
    public void Sun_FallBackDst_DoesNotDoubleFireTheRepeatedOneAmHour()
    {
        // 2026-11-01 is the US fall-back date: local 01:00-01:59 CDT happens, then clocks fall back
        // to 01:00 CST and 01:00-01:59 happens again. The cron must fire the top of that hour once,
        // not twice, and still reach exactly 12 fires (00:00..11:00) for the day.
        var cron = Build(SunCron);
        var t = Ct(2026, 10, 31, 23, 30); // Saturday night CDT (-05:00)

        var fireTimes = new List<DateTimeOffset>();
        for (var i = 0; i < 12; i++)
        {
            var next = cron.GetTimeAfter(t);
            Assert.NotNull(next);
            fireTimes.Add(next!.Value);
            t = next.Value;
        }

        // 01:00 on 2026-11-01 is ambiguous (it occurs twice); TimeZoneInfo.GetUtcOffset resolves an
        // ambiguous Unspecified DateTime to the standard-time offset by default, which is the wrong
        // (second) occurrence here, so that one instant is asserted directly by its UTC value rather
        // than through the Ct() helper.
        Assert.Equal(Ct(2026, 11, 1, 0, 0), fireTimes[0]); // -05:00 (CDT)
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 0, 0, TimeSpan.FromHours(-5)), fireTimes[1]); // -05:00 (CDT), the FIRST 01:00
        Assert.Equal(Ct(2026, 11, 1, 2, 0), fireTimes[2]); // -06:00 (CST) - no second 01:00 fire in between
        Assert.Equal(Ct(2026, 11, 1, 11, 0), fireTimes[^1]);

        // Only one fire has an hour-of-day of 1 on 2026-11-01: the repeated local hour is not double-fired.
        Assert.Single(fireTimes, f => TimeZoneInfo.ConvertTime(f, Chicago) is { Day: 1, Hour: 1 });

        var thirteenth = cron.GetTimeAfter(t);
        Assert.NotNull(thirteenth);
        Assert.Equal(Ct(2026, 11, 8, 0, 0), thirteenth!.Value);
    }

    [Fact]
    public void TueSat_UnaffectedByTheSundayDstTransition()
    {
        // DST always flips on a Sunday, which TUE-SAT never runs on; confirm the following Tuesday
        // is still plain hourly spacing with no skipped or doubled fire.
        var cron = Build(TueSatCron);
        var t = Ct(2026, 3, 10, 0, 0).AddSeconds(-1); // just before the Tuesday after spring-forward Sunday

        var first = cron.GetTimeAfter(t);
        var second = cron.GetTimeAfter(first!.Value);
        var third = cron.GetTimeAfter(second!.Value);

        Assert.Equal(Ct(2026, 3, 10, 0, 0), first!.Value);
        Assert.Equal(Ct(2026, 3, 10, 1, 0), second!.Value);
        Assert.Equal(Ct(2026, 3, 10, 2, 0), third!.Value);
    }
}
