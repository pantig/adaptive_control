using System.Globalization;

namespace EnergyOptimizer;

public sealed class PolishCalendarService
{
    private static readonly Dictionary<string, string> FixedHolidayNames = new()
    {
        ["01-01"] = "Nowy Rok",
        ["01-06"] = "Trzech Króli",
        ["05-01"] = "Święto Pracy",
        ["05-03"] = "Święto Konstytucji 3 Maja",
        ["08-15"] = "Wniebowzięcie NMP",
        ["11-01"] = "Wszystkich Świętych",
        ["11-11"] = "Narodowe Święto Niepodległości",
        ["12-25"] = "Boże Narodzenie",
        ["12-26"] = "Drugi dzień Bożego Narodzenia"
    };

    public CalendarInsight Describe(DateOnly date)
    {
        var holidayName = GetHolidayName(date);
        var isHoliday = holidayName.Length > 0;
        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var isNonWorkingDay = isHoliday || isWeekend;

        var label = isHoliday
            ? $"Święto: {holidayName}"
            : isWeekend
                ? CultureInfo.GetCultureInfo("pl-PL").DateTimeFormat.GetDayName(date.DayOfWeek)
                : "Dzień roboczy";

        return new CalendarInsight(
            date,
            isWeekend,
            isHoliday,
            isNonWorkingDay,
            label,
            holidayName);
    }

    private static string GetHolidayName(DateOnly date)
    {
        if (FixedHolidayNames.TryGetValue(date.ToString("MM-dd", CultureInfo.InvariantCulture), out var fixedName))
        {
            return fixedName;
        }

        var easterSunday = CalculateEasterSunday(date.Year);
        if (date == easterSunday)
        {
            return "Wielkanoc";
        }

        if (date == easterSunday.AddDays(1))
        {
            return "Poniedziałek Wielkanocny";
        }

        if (date == easterSunday.AddDays(49))
        {
            return "Zesłanie Ducha Świętego";
        }

        if (date == easterSunday.AddDays(60))
        {
            return "Boże Ciało";
        }

        return string.Empty;
    }

    private static DateOnly CalculateEasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }
}
