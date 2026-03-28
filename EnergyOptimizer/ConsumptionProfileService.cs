using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace EnergyOptimizer;

public sealed class ConsumptionProfileService(IHostEnvironment environment, ILogger<ConsumptionProfileService> logger)
{
    private const string SharedStringsPath = "xl/sharedStrings.xml";
    private const string SheetPath = "xl/worksheets/sheet1.xml";
    private static readonly XNamespace SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private readonly IReadOnlyDictionary<DateOnly, double[]> _dailyLoadsMw = LoadProfiles(environment, logger);

    public bool HasData => _dailyLoadsMw.Count > 0;

    public double GetSuggestedCurrentLoadMw(DateOnly scenarioDate)
    {
        if (!TryMapScenarioDate(scenarioDate, out var sourceDate))
        {
            return 0.12;
        }

        var hour = DateTime.Now.Hour;
        var sourceDay = _dailyLoadsMw[sourceDate];
        var suggested = sourceDay[Math.Clamp(hour, 0, 23)];
        if (suggested > 0.0001)
        {
            return Math.Round(suggested, 3);
        }

        return Math.Round(sourceDay.Where(value => value > 0).DefaultIfEmpty(0.12).Average(), 3);
    }

    public ProfileScenario? BuildScenario(DateOnly scenarioDate, double currentLoadMw)
    {
        if (!TryMapScenarioDate(scenarioDate, out var sourceDate))
        {
            return null;
        }

        var sourceDay = _dailyLoadsMw[sourceDate];
        var referenceHour = Math.Clamp(DateTime.Now.Hour, 0, 23);
        var suggestedCurrentLoad = sourceDay[referenceHour] > 0.0001
            ? sourceDay[referenceHour]
            : sourceDay.Where(value => value > 0).DefaultIfEmpty(0.12).Average();

        var scaleFactor = suggestedCurrentLoad > 0.0001
            ? Math.Clamp(currentLoadMw / suggestedCurrentLoad, 0.05, 8.00)
            : 1.0;

        var forecast = sourceDay
            .Select(value => Math.Round(value * scaleFactor, 3))
            .ToArray();

        var orderedDates = _dailyLoadsMw.Keys.OrderBy(date => date).ToArray();
        var sourceIndex = Array.IndexOf(orderedDates, sourceDate);
        var history = new List<ProfileHistoryDay>(7);

        for (var offset = 7; offset >= 1; offset--)
        {
            var index = sourceIndex - offset;
            if (index < 0)
            {
                index += orderedDates.Length;
            }

            var historyDate = orderedDates[index];
            history.Add(new ProfileHistoryDay(historyDate, _dailyLoadsMw[historyDate]));
        }

        var sources = string.Join(", ", Directory
            .GetFiles(Path.Combine(environment.ContentRootPath, "Data", "Profiles"), "*.xlsx")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileName));

        return new ProfileScenario(
            SourceDate: sourceDate,
            SuggestedCurrentLoadMw: Math.Round(suggestedCurrentLoad, 3),
            ScaleFactor: Math.Round(scaleFactor, 3),
            ForecastLoadMw: forecast,
            HistoryDays: history,
            SourceDescription: sources);
    }

    private bool TryMapScenarioDate(DateOnly scenarioDate, out DateOnly sourceDate)
    {
        sourceDate = default;
        if (_dailyLoadsMw.Count == 0)
        {
            return false;
        }

        var referenceYear = _dailyLoadsMw.Keys.Min(date => date.Year);
        var day = Math.Min(scenarioDate.Day, DateTime.DaysInMonth(referenceYear, scenarioDate.Month));
        var candidate = new DateOnly(referenceYear, scenarioDate.Month, day);

        if (_dailyLoadsMw.ContainsKey(candidate))
        {
            sourceDate = candidate;
            return true;
        }

        var bestMatch = _dailyLoadsMw.Keys
            .OrderBy(date => Math.Abs(date.DayNumber - candidate.DayNumber))
            .First();

        sourceDate = bestMatch;
        return true;
    }

    private static IReadOnlyDictionary<DateOnly, double[]> LoadProfiles(IHostEnvironment environment, ILogger logger)
    {
        var profileDir = Path.Combine(environment.ContentRootPath, "Data", "Profiles");
        if (!Directory.Exists(profileDir))
        {
            logger.LogWarning("Consumption profile directory not found: {Directory}", profileDir);
            return new Dictionary<DateOnly, double[]>();
        }

        var files = Directory
            .GetFiles(profileDir, "*.xlsx")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            logger.LogWarning("No XLSX profile files found in {Directory}", profileDir);
            return new Dictionary<DateOnly, double[]>();
        }

        var dailyLoads = new Dictionary<DateOnly, double[]>();

        foreach (var file in files)
        {
            try
            {
                foreach (var point in ReadProfileFile(file))
                {
                    if (!dailyLoads.TryGetValue(point.Date, out var hours))
                    {
                        hours = new double[24];
                        dailyLoads[point.Date] = hours;
                    }

                    hours[point.Hour] += point.LoadMw;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to read consumption profile file {File}", file);
            }
        }

        return dailyLoads
            .Where(entry => entry.Value.Any(value => value > 0.0001))
            .ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Select(value => Math.Round(value, 3)).ToArray());
    }

    private static IEnumerable<ProfilePoint> ReadProfileFile(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetEntry = archive.GetEntry(SheetPath) ?? throw new InvalidOperationException($"Missing worksheet: {SheetPath}");

        using var stream = sheetEntry.Open();
        var document = XDocument.Load(stream);
        var rows = document
            .Descendants(SpreadsheetNs + "row")
            .Skip(11);

        foreach (var row in rows)
        {
            var cells = row
                .Elements(SpreadsheetNs + "c")
                .ToDictionary(GetColumnName, cell => cell, StringComparer.OrdinalIgnoreCase);

            var timestampText = GetCellValue(cells, "H", sharedStrings);
            var loadText = GetCellValue(cells, "I", sharedStrings);

            if (!DateTime.TryParseExact(timestampText, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var intervalEnd))
            {
                continue;
            }

            if (!double.TryParse(loadText, NumberStyles.Float, CultureInfo.InvariantCulture, out var energyKWh))
            {
                continue;
            }

            var mappedDate = intervalEnd.Hour == 0
                ? DateOnly.FromDateTime(intervalEnd.AddDays(-1))
                : DateOnly.FromDateTime(intervalEnd);

            var mappedHour = intervalEnd.Hour == 0 ? 23 : intervalEnd.Hour - 1;
            yield return new ProfilePoint(mappedDate, mappedHour, energyKWh / 1000.0);
        }
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry(SharedStringsPath);
        if (entry is null)
        {
            return Array.Empty<string>();
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream);

        return document
            .Descendants(SpreadsheetNs + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNs + "t").Select(text => text.Value)))
            .ToArray();
    }

    private static string GetCellValue(IReadOnlyDictionary<string, XElement> cells, string column, IReadOnlyList<string> sharedStrings)
    {
        if (!cells.TryGetValue(column, out var cell))
        {
            return string.Empty;
        }

        var type = (string?)cell.Attribute("t");
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
        {
            var indexText = cell.Element(SpreadsheetNs + "v")?.Value;
            return int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                   index >= 0 &&
                   index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }

        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(cell.Descendants(SpreadsheetNs + "t").Select(text => text.Value));
        }

        return cell.Element(SpreadsheetNs + "v")?.Value ?? string.Empty;
    }

    private static string GetColumnName(XElement cell)
    {
        var reference = (string?)cell.Attribute("r") ?? string.Empty;
        return new string(reference.TakeWhile(char.IsLetter).ToArray());
    }

    private sealed record ProfilePoint(DateOnly Date, int Hour, double LoadMw);
}

public sealed record ProfileScenario(
    DateOnly SourceDate,
    double SuggestedCurrentLoadMw,
    double ScaleFactor,
    IReadOnlyList<double> ForecastLoadMw,
    IReadOnlyList<ProfileHistoryDay> HistoryDays,
    string SourceDescription);

public sealed record ProfileHistoryDay(
    DateOnly Date,
    IReadOnlyList<double> HourlyLoadMw);
