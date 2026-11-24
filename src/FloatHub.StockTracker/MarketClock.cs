using System;

namespace FloatHub.StockTracker;

public static class MarketClock
{
    private static readonly TimeZoneInfo China = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    public static TimeZoneInfo Zone(string region) => region == "us" ? Eastern : China;

    // During breaks and holidays, compare against the most recent expected session,
    // so Friday's closing print stays current over a normal weekend.
    public static DateTimeOffset ExpectedQuoteTime(string region, DateTimeOffset now)
    {
        var zone = Zone(region);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        bool us = region == "us", hk = region == "hk";
        for (int back = 0; back < 20; back++)
        {
            var date = local.Date.AddDays(-back);
            DateTimeOffset At(int minute)
            {
                var instant = date.AddMinutes(minute);
                return new DateTimeOffset(instant, zone.GetUtcOffset(instant));
            }
            string morning = Session(region, At(600));
            if (morning == "交易日历待更新") return now;
            if (morning != "交易时段" || now < At(570)) continue;
            bool shortDay = Session(region, At(840)) != "交易时段";
            int close = us ? (shortDay ? 780 : 960) : hk ? (shortDay ? 720 : 960) : 900;
            if (now >= At(close)) return At(close);
            int lunch = hk ? 720 : 690;
            if (!us && now >= At(lunch) && now < At(780)) return At(lunch);
            return now;
        }
        return now;
    }

    // Trading calendars from SSE, HKEX and NYSE. Dates use the market's local time.
    public static string Session(string region, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Zone(region));
        var day = local.Month * 100 + local.Day;
        int year = local.Year, minute = local.Hour * 60 + local.Minute;
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return "周末休市";
        bool hk = region == "hk", us = region == "us";
        if (year != 2026 && !(year == 2027 && (hk || us))) return "交易日历待更新";
        bool holiday = (us, hk, year) switch
        {
            (true, _, 2026) => day is 101 or 119 or 216 or 403 or 525 or 619 or 703 or 907 or 1126 or 1225,
            (true, _, 2027) => day is 101 or 118 or 215 or 326 or 531 or 618 or 705 or 906 or 1125 or 1224,
            (_, true, 2026) => day is 101 or >= 217 and <= 219 or 403 or 406 or 407 or 501 or 525 or 619 or 701 or 1001 or 1019 or 1225,
            (_, true, 2027) => day is 101 or 208 or 209 or 326 or 329 or 405 or 513 or 609 or 701 or 916 or 1001 or 1008 or 1227,
            _ => day is 101 or 102 or >= 215 and <= 223 or 406 or >= 501 and <= 505 or 619 or 925 or >= 1001 and <= 1007
        };
        if (holiday) return "节假日休市";
        if (us)
        {
            bool half = (year == 2026 && day is 1127 or 1224) || (year == 2027 && day == 1126);
            int close = half ? 780 : 960;
            if (minute >= 570 && minute < close) return "交易时段";
            if (minute >= 240 && minute < 570) return "盘前时段";
            if (minute >= close && minute < (half ? 1020 : 1200)) return "盘后时段";
            return "休市";
        }
        bool halfHK = hk && ((year == 2026 && day is 216 or 1224 or 1231) || (year == 2027 && day is 205 or 1224 or 1231));
        if (minute >= 570 && minute < (hk ? 720 : 690)) return "交易时段";
        if (halfHK && minute >= 720 && minute < 731) return "收市竞价时段";
        if (!halfHK && minute >= (hk ? 720 : 690) && minute < 780) return "午间休市";
        if (!halfHK && minute >= 780 && minute < (hk ? 960 : 900)) return "交易时段";
        if (hk && !halfHK && minute >= 960 && minute < 971) return "收市竞价时段";
        return "休市";
    }
}
