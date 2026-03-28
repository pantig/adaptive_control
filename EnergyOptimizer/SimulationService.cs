namespace EnergyOptimizer;

public sealed class SimulationService(PolishCalendarService calendarService, ConsumptionProfileService consumptionProfileService)
{
    private const int PeriodCount = 24;
    private const double BatteryPowerPerCapacityRatio = 0.25;
    private const double UnservedPenaltyPlnPerMWh = 3000;

    private static readonly double[] WorkingDayPrices =
    {
        330, 315, 300, 295, 300, 335, 390, 450, 540, 500, 430, 380,
        340, 320, 330, 360, 420, 520, 610, 580, 510, 430, 380, 345
    };

    private static readonly double[] NonWorkingDayPrices =
    {
        300, 290, 280, 275, 280, 300, 330, 360, 400, 380, 340, 310,
        290, 280, 285, 310, 360, 430, 500, 480, 410, 350, 320, 300
    };

    private static readonly double[] LoadPattern =
    {
        0.78, 0.75, 0.73, 0.72, 0.74, 0.79, 0.87, 0.98, 1.06, 1.05, 1.01, 0.98,
        1.00, 0.99, 1.01, 1.05, 1.11, 1.18, 1.24, 1.22, 1.12, 1.00, 0.90, 0.84
    };

    private static readonly string[] Rules =
    [
        "PV zawsze pokrywa zapotrzebowanie jako pierwsze źródło energii.",
        "Nadwyżka z PV ładuje magazyn, a dopiero potem jest oddawana do sieci.",
        "Magazyn ładuje się z sieci tylko w tanich godzinach i tylko do poziomu potrzebnego na drogie godziny.",
        "Magazyn rozładowuje się w drogich godzinach albo wtedy, gdy trzeba pilnować limitu przyłącza.",
        "Ładowanie i rozładowanie są dzielone równomiernie między wszystkie magazyny."
    ];

    private static readonly string[] Assumptions =
    [
        "Symulacja pracuje na profilu RDN 1h. Profil cenowy jest demonstracyjny, ale zachowuje logikę godzin tanich i drogich.",
        "Historia zużycia z ostatnich 7 dni jest syntetyczna i służy tylko do pokazania wpływu kalendarza oraz aktualnego poboru.",
        "Maksymalna moc ładowania i rozładowania jednego magazynu wynosi 25% jego pojemności na godzinę.",
        "Minimalne rozładowanie magazynu jest ustawiane parametrem `MinimumSocPercent`."
    ];

    public SimulationResponse BuildDefaultScenario()
    {
        var tomorrow = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        var request = new ScenarioRequest(
            ScenarioDate: tomorrow,
            CurrentLoadMw: consumptionProfileService.GetSuggestedCurrentLoadMw(tomorrow));
        return Simulate(request);
    }

    public SimulationResponse Simulate(ScenarioRequest request)
    {
        var scenario = Normalize(request);
        var calendar = calendarService.Describe(scenario.ScenarioDate!.Value);
        var prices = BuildPriceProfile(scenario, calendar);
        var profileScenario = consumptionProfileService.BuildScenario(scenario.ScenarioDate.Value, scenario.CurrentLoadMw);
        var historyProfiles = profileScenario is null
            ? BuildHistoryProfiles(scenario)
            : BuildHistoryProfiles(profileScenario);
        var history = historyProfiles
            .Select(day => new HistoricalDay(
                day.Date,
                day.DayType,
                Math.Round(day.HourlyLoadMw.Average(), 3),
                Math.Round(day.HourlyLoadMw.Max(), 3)))
            .ToList();

        var loadForecast = profileScenario is null
            ? BuildLoadForecast(scenario, calendar, historyProfiles)
            : profileScenario.ForecastLoadMw.Select(value => Math.Round(value, 3)).ToArray();
        var pvForecast = BuildPvForecast(scenario);
        var hours = BuildSimulationHours(scenario, prices, loadForecast, pvForecast);

        var inputs = new InputSummary(
            TotalBatteryCapacityMWh: Math.Round(scenario.BatteryCount * scenario.BatteryCapacityMWh, 3),
            MinimumSocMWh: Math.Round((scenario.BatteryCount * scenario.BatteryCapacityMWh) * (scenario.MinimumSocPercent / 100.0), 3),
            MaxChargePowerMw: Math.Round(scenario.BatteryCount * scenario.BatteryCapacityMWh * BatteryPowerPerCapacityRatio, 3),
            MaxDischargePowerMw: Math.Round(scenario.BatteryCount * scenario.BatteryCapacityMWh * BatteryPowerPerCapacityRatio, 3),
            AverageRdnPricePlnPerMWh: Math.Round(prices.Average(price => price.RdnPricePlnPerMWh), 2),
            AverageEffectiveBuyPricePlnPerMWh: Math.Round(prices.Average(price => price.EffectiveBuyPricePlnPerMWh), 2),
            AverageSellPricePlnPerMWh: Math.Round(prices.Average(price => price.SellPricePlnPerMWh), 2),
            PriceSource: "Profil demonstracyjny RDN 1h",
            HistorySource: profileScenario is null
                ? "Syntetyczne dane z ostatnich 7 dni"
                : $"Raporty XLSX: {profileScenario.SourceDescription}. Dzien referencyjny: {profileScenario.SourceDate:yyyy-MM-dd}. Skala profilu: {profileScenario.ScaleFactor:0.###}x");

        var summary = BuildSummary(hours);

        return new SimulationResponse(
            scenario,
            calendar,
            inputs,
            history,
            hours,
            summary,
            Rules,
            BuildAssumptions(profileScenario is not null));
    }

    private static ScenarioRequest Normalize(ScenarioRequest request)
    {
        var scenarioDate = request.ScenarioDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        var batteryCount = Math.Clamp(request.BatteryCount, 1, 12);
        var batteryCapacityMWh = Math.Clamp(request.BatteryCapacityMWh, 0.25, 4.0);
        var totalBatteryCapacity = batteryCount * batteryCapacityMWh;
        var minimumSocPercent = Math.Clamp(request.MinimumSocPercent, 10, 40);
        var minimumSocMWh = totalBatteryCapacity * (minimumSocPercent / 100.0);
        var initialStoredEnergyMWh = Math.Clamp(request.InitialStoredEnergyMWh, minimumSocMWh, totalBatteryCapacity);

        return request with
        {
            ScenarioDate = scenarioDate,
            CurrentLoadMw = Math.Clamp(request.CurrentLoadMw, 0.01, 2.00),
            InitialStoredEnergyMWh = Math.Round(initialStoredEnergyMWh, 3),
            BatteryCount = batteryCount,
            BatteryCapacityMWh = batteryCapacityMWh,
            GridImportLimitMw = Math.Clamp(request.GridImportLimitMw, 0.10, 2.00),
            PvMaxMw = Math.Clamp(request.PvMaxMw, 0.00, 2.00),
            CloudinessPercent = Math.Clamp(request.CloudinessPercent, 0, 100),
            DistributionFeePlnPerMWh = Math.Clamp(request.DistributionFeePlnPerMWh, 0, 300),
            ExportPriceFactor = Math.Clamp(request.ExportPriceFactor, 0.20, 1.00),
            MinimumSocPercent = minimumSocPercent
        };
    }

    private List<PriceProfileHour> BuildPriceProfile(ScenarioRequest scenario, CalendarInsight calendar)
    {
        var baseProfile = calendar.IsNonWorkingDay ? NonWorkingDayPrices : WorkingDayPrices;
        var seasonalShift = scenario.ScenarioDate!.Value.Month is >= 11 or <= 2
            ? 35
            : scenario.ScenarioDate.Value.Month is >= 6 and <= 8
                ? -10
                : 0;

        var rawPrices = Enumerable.Range(0, PeriodCount)
            .Select(hour =>
            {
                var random = new Random(HashCode.Combine(scenario.ScenarioDate.Value.DayNumber, hour));
                var noise = (random.NextDouble() * 18) - 9;
                var rdn = Math.Clamp(baseProfile[hour] + seasonalShift + noise, 220, 900);
                return new
                {
                    Hour = hour,
                    Label = $"{hour:00}:00",
                    Rdn = Math.Round(rdn, 2),
                    Effective = Math.Round(rdn + scenario.DistributionFeePlnPerMWh, 2),
                    Sell = Math.Round(rdn * scenario.ExportPriceFactor, 2)
                };
            })
            .ToList();

        var effectivePrices = rawPrices.Select(price => price.Effective).ToArray();
        var cheapThreshold = Percentile(effectivePrices, 0.30);
        var expensiveThreshold = Percentile(effectivePrices, 0.70);

        return rawPrices
            .Select(price => new PriceProfileHour(
                price.Hour,
                price.Label,
                price.Rdn,
                price.Effective,
                price.Sell,
                price.Effective <= cheapThreshold,
                price.Effective >= expensiveThreshold))
            .ToList();
    }

    private List<HistoryProfile> BuildHistoryProfiles(ScenarioRequest scenario)
    {
        var history = new List<HistoryProfile>();

        for (var offset = 7; offset >= 1; offset--)
        {
            var date = scenario.ScenarioDate!.Value.AddDays(-offset);
            var calendar = calendarService.Describe(date);
            var dayType = calendar.IsNonWorkingDay ? "Dzień wolny" : "Dzień roboczy";
            var dayMultiplier = calendar.IsNonWorkingDay ? 0.90 : 1.02;
            var random = new Random(HashCode.Combine(date.DayNumber, (int)scenario.CurrentLoadMw * 100));
            var hourlyLoad = new double[PeriodCount];

            for (var hour = 0; hour < PeriodCount; hour++)
            {
                var noiseFactor = 1 + ((random.NextDouble() - 0.5) * 0.10);
                hourlyLoad[hour] = Math.Round(Math.Max(0.08, scenario.CurrentLoadMw * LoadPattern[hour] * dayMultiplier * noiseFactor), 3);
            }

            history.Add(new HistoryProfile(date, dayType, hourlyLoad));
        }

        return history;
    }

    private List<HistoryProfile> BuildHistoryProfiles(ProfileScenario profileScenario)
    {
        return profileScenario.HistoryDays
            .Select(day =>
            {
                var calendar = calendarService.Describe(day.Date);
                var dayType = calendar.IsNonWorkingDay ? "Dzien wolny" : "Dzien roboczy";
                return new HistoryProfile(
                    day.Date,
                    dayType,
                    day.HourlyLoadMw.Select(value => Math.Round(value, 3)).ToArray());
            })
            .ToList();
    }

    private static double[] BuildLoadForecast(
        ScenarioRequest scenario,
        CalendarInsight calendar,
        IReadOnlyList<HistoryProfile> historyProfiles)
    {
        var dayType = calendar.IsNonWorkingDay ? "Dzień wolny" : "Dzień roboczy";
        var matchingDays = historyProfiles.Where(day => day.DayType == dayType).ToList();
        if (matchingDays.Count == 0)
        {
            matchingDays = historyProfiles.ToList();
        }

        var yesterday = historyProfiles[^1];
        var forecast = new double[PeriodCount];
        for (var hour = 0; hour < PeriodCount; hour++)
        {
            var averageFromSimilarDays = matchingDays.Average(day => day.HourlyLoadMw[hour]);
            var blended = (averageFromSimilarDays * 0.70) + (yesterday.HourlyLoadMw[hour] * 0.30);
            forecast[hour] = Math.Round(blended, 3);
        }

        var anchorHour = 12;
        var anchor = Math.Max(0.10, forecast[anchorHour]);
        var scaleFactor = Math.Clamp(scenario.CurrentLoadMw / anchor, 0.75, 1.35);
        for (var hour = 0; hour < PeriodCount; hour++)
        {
            forecast[hour] = Math.Round(Math.Max(0.08, forecast[hour] * scaleFactor), 3);
        }

        return forecast;
    }

    private static double[] BuildPvForecast(ScenarioRequest scenario)
    {
        var date = scenario.ScenarioDate!.Value;
        var dayOfYear = date.DayOfYear;
        var seasonFactor = Math.Clamp(0.55 + (0.35 * Math.Sin((2 * Math.PI * (dayOfYear - 80)) / 365.0)), 0.20, 1.00);
        var daylightHours = 8.5 + (seasonFactor * 5.5);
        var sunrise = 12.0 - (daylightHours / 2.0);
        var sunset = 12.0 + (daylightHours / 2.0);
        var weatherFactor = Math.Clamp(1 - ((scenario.CloudinessPercent / 100.0) * 0.75), 0.12, 1.00);
        var result = new double[PeriodCount];

        for (var hour = 0; hour < PeriodCount; hour++)
        {
            var center = hour + 0.5;
            if (center <= sunrise || center >= sunset)
            {
                result[hour] = 0;
                continue;
            }

            var position = (center - sunrise) / daylightHours;
            var bell = Math.Pow(Math.Sin(Math.PI * position), 1.4);
            result[hour] = Math.Round(scenario.PvMaxMw * seasonFactor * weatherFactor * bell, 3);
        }

        return result;
    }

    private static IReadOnlyList<SimulationHour> BuildSimulationHours(
        ScenarioRequest scenario,
        IReadOnlyList<PriceProfileHour> prices,
        IReadOnlyList<double> loadForecast,
        IReadOnlyList<double> pvForecast)
    {
        var totalBatteryCapacity = scenario.BatteryCount * scenario.BatteryCapacityMWh;
        var minimumSoc = totalBatteryCapacity * (scenario.MinimumSocPercent / 100.0);
        var maxChargePower = scenario.BatteryCount * scenario.BatteryCapacityMWh * BatteryPowerPerCapacityRatio;
        var maxDischargePower = maxChargePower;
        var soc = scenario.InitialStoredEnergyMWh;
        var netLoad = Enumerable.Range(0, PeriodCount)
            .Select(hour => Math.Max(0, loadForecast[hour] - pvForecast[hour]))
            .ToArray();

        var hours = new List<SimulationHour>(PeriodCount);

        for (var hour = 0; hour < PeriodCount; hour++)
        {
            var load = loadForecast[hour];
            var pv = pvForecast[hour];
            var price = prices[hour];
            var startingSoc = soc;

            var baselineGrid = Math.Min(scenario.GridImportLimitMw, Math.Max(0, load - pv));
            var baselineExport = Math.Max(0, pv - load);
            var baselineUnserved = Math.Max(0, load - pv - scenario.GridImportLimitMw);
            var baselineCost = (baselineGrid * price.EffectiveBuyPricePlnPerMWh)
                - (baselineExport * price.SellPricePlnPerMWh)
                + (baselineUnserved * UnservedPenaltyPlnPerMWh);

            var remainingLoad = Math.Max(0, load - pv);
            var charge = 0.0;
            var discharge = 0.0;
            var gridImport = 0.0;
            var export = 0.0;
            var unserved = 0.0;
            var pvCharge = 0.0;
            var gridCharge = 0.0;
            var actions = new List<string>();

            if (load > pv)
            {
                var shouldProtectGridLimit = remainingLoad > scenario.GridImportLimitMw;
                if (price.ExpensiveHour || shouldProtectGridLimit)
                {
                    var desiredDischarge = price.ExpensiveHour
                        ? remainingLoad
                        : remainingLoad - scenario.GridImportLimitMw;

                    var availableDischarge = Math.Min(maxDischargePower, Math.Max(0, soc - minimumSoc));
                    discharge = Math.Min(Math.Max(0, desiredDischarge), availableDischarge);

                    if (discharge > 0)
                    {
                        remainingLoad -= discharge;
                        soc -= discharge;
                        actions.Add(price.ExpensiveHour ? "rozładowanie magazynu" : "ochrona limitu przyłącza");
                    }
                }

                gridImport = Math.Min(scenario.GridImportLimitMw, remainingLoad);
                remainingLoad -= gridImport;

                if (remainingLoad > 0)
                {
                    unserved = remainingLoad;
                    actions.Add("przekroczony limit przyłącza");
                }
            }
            else
            {
                var surplusPv = pv - load;
                pvCharge = Math.Min(Math.Min(maxChargePower, totalBatteryCapacity - soc), surplusPv);
                charge = pvCharge;
                if (pvCharge > 0)
                {
                    soc += pvCharge;
                    actions.Add("ładowanie z PV");
                }

                export = Math.Max(0, surplusPv - pvCharge);
                if (export > 0)
                {
                    actions.Add("oddanie nadwyżki do sieci");
                }
            }

            var targetSoc = ComputeTargetSoc(hour, prices, netLoad, minimumSoc, totalBatteryCapacity, maxDischargePower);
            var batteryHasRoom = soc < totalBatteryCapacity - 0.0001;
            var canGridCharge = price.CheapHour && batteryHasRoom && soc < targetSoc - 0.0001;
            if (canGridCharge)
            {
                var availableChargePower = Math.Max(0, maxChargePower - charge);
                var gridHeadroom = Math.Max(0, scenario.GridImportLimitMw - gridImport);
                var energyNeeded = Math.Max(0, targetSoc - soc);
                var extraCharge = Math.Min(Math.Min(availableChargePower, gridHeadroom), Math.Min(totalBatteryCapacity - soc, energyNeeded));

                if (extraCharge > 0)
                {
                    gridCharge = extraCharge;
                    charge += extraCharge;
                    gridImport += extraCharge;
                    soc += extraCharge;
                    actions.Add("doładowanie z sieci");
                }
            }

            var optimizedCost = (gridImport * price.EffectiveBuyPricePlnPerMWh)
                - (export * price.SellPricePlnPerMWh)
                + (unserved * UnservedPenaltyPlnPerMWh);

            var decisionReason = BuildDecisionReason(
                price,
                load,
                pv,
                scenario.GridImportLimitMw,
                totalBatteryCapacity,
                minimumSoc,
                startingSoc,
                soc,
                targetSoc,
                pvCharge,
                gridCharge,
                discharge,
                export,
                gridImport,
                unserved);

            var chargePerBattery = charge / scenario.BatteryCount;
            var dischargePerBattery = discharge / scenario.BatteryCount;

            hours.Add(new SimulationHour(
                Hour: hour,
                Label: price.Label,
                RdnPricePlnPerMWh: price.RdnPricePlnPerMWh,
                EffectiveBuyPricePlnPerMWh: price.EffectiveBuyPricePlnPerMWh,
                SellPricePlnPerMWh: price.SellPricePlnPerMWh,
                ForecastLoadMw: load,
                ForecastPvMw: pv,
                GridImportMw: Math.Round(gridImport, 3),
                BatteryChargeMw: Math.Round(charge, 3),
                BatteryDischargeMw: Math.Round(discharge, 3),
                ChargePerBatteryMw: Math.Round(chargePerBattery, 3),
                DischargePerBatteryMw: Math.Round(dischargePerBattery, 3),
                ExportMw: Math.Round(export, 3),
                SocMWh: Math.Round(soc, 3),
                BaselineCostPln: Math.Round(baselineCost, 2),
                OptimizedCostPln: Math.Round(optimizedCost, 2),
                DeltaPln: Math.Round(baselineCost - optimizedCost, 2),
                Action: actions.Count > 0 ? string.Join(", ", actions.Distinct()) : "bez działania",
                DecisionReason: decisionReason,
                CheapHour: price.CheapHour,
                ExpensiveHour: price.ExpensiveHour,
                GridLimitHit: gridImport >= scenario.GridImportLimitMw - 0.0001 || unserved > 0,
                UnservedMw: Math.Round(unserved, 3)));
        }

        return hours;
    }

    private static SimulationSummary BuildSummary(IReadOnlyList<SimulationHour> hours)
    {
        var optimizedCost = hours.Sum(hour => hour.OptimizedCostPln);
        var baselineCost = hours.Sum(hour => hour.BaselineCostPln);
        var savings = baselineCost - optimizedCost;
        var savingsPercent = baselineCost > 0 ? (savings / baselineCost) * 100 : 0;
        var peakGrid = hours.Max(hour => hour.GridImportMw);
        var warningHours = hours.Count(hour => hour.UnservedMw > 0);

        var note = warningHours > 0
            ? "W części godzin sam magazyn i limit przyłącza nie wystarczają do pełnego pokrycia zapotrzebowania."
            : savings >= 0
                ? "Magazyn ładuje się w tańszych godzinach i ogranicza zakup energii w drogich godzinach."
                : "Przy tych parametrach magazyn nie daje oszczędności kosztowej, ale nadal ogranicza wahania poboru z sieci.";

        return new SimulationSummary(
            OptimizedCostPln: Math.Round(optimizedCost, 2),
            BaselineCostPln: Math.Round(baselineCost, 2),
            SavingsPln: Math.Round(savings, 2),
            SavingsPercent: Math.Round(savingsPercent, 1),
            FinalSocMWh: Math.Round(hours[^1].SocMWh, 3),
            PeakGridImportMw: Math.Round(peakGrid, 3),
            TotalGridEnergyMWh: Math.Round(hours.Sum(hour => hour.GridImportMw), 3),
            TotalChargedEnergyMWh: Math.Round(hours.Sum(hour => hour.BatteryChargeMw), 3),
            TotalDischargedEnergyMWh: Math.Round(hours.Sum(hour => hour.BatteryDischargeMw), 3),
            TotalExportedEnergyMWh: Math.Round(hours.Sum(hour => hour.ExportMw), 3),
            GridLimitHours: hours.Count(hour => hour.GridLimitHit),
            WarningHours: warningHours,
            Note: note);
    }

    private static IReadOnlyList<string> BuildAssumptions(bool usesProfileData)
    {
        if (usesProfileData)
        {
            return
            [
                "Symulacja pracuje na profilu RDN 1h. Profil cenowy jest demonstracyjny, ale zachowuje logike godzin tanich i drogich.",
                "Profil zuzycia i historia pochodza z zalaczonych raportow XLSX. Dla dat spoza roku pomiarowego wybierany jest odpowiadajacy dzien kalendarzowy z profilu.",
                "Parametr aktualnego poboru sluzy jako korekta skali dla calego profilu godzinowego w wybranym dniu.",
                "Maksymalna moc ladowania i rozladowania jednego magazynu wynosi 25% jego pojemnosci na godzine."
            ];
        }

        return
        [
            "Symulacja pracuje na profilu RDN 1h. Profil cenowy jest demonstracyjny, ale zachowuje logike godzin tanich i drogich.",
            "Jesli brak plikow XLSX, historia zuzycia z ostatnich 7 dni jest budowana syntetycznie tylko na potrzeby demonstracji.",
            "Maksymalna moc ladowania i rozladowania jednego magazynu wynosi 25% jego pojemnosci na godzine.",
            "Minimalne rozladowanie magazynu jest ustawiane parametrem `MinimumSocPercent`."
        ];
    }

    private static string BuildDecisionReason(
        PriceProfileHour price,
        double load,
        double pv,
        double gridImportLimit,
        double totalBatteryCapacity,
        double minimumSoc,
        double startingSoc,
        double endingSoc,
        double targetSoc,
        double pvCharge,
        double gridCharge,
        double discharge,
        double export,
        double gridImport,
        double unserved)
    {
        const double Epsilon = 0.0001;
        var netDemand = Math.Max(0, load - pv);

        if (unserved > Epsilon)
        {
            return "Po wykorzystaniu PV, magazynu i limitu przylacza nadal brakuje mocy, wiec czesc zapotrzebowania pozostaje niepokryta.";
        }

        if (discharge > Epsilon && price.ExpensiveHour)
        {
            return "To droga godzina, dlatego system korzysta z energii w magazynie, aby ograniczyc zakup z sieci.";
        }

        if (discharge > Epsilon)
        {
            return "Zuzycie po PV zbliza sie do limitu przylacza, dlatego magazyn scina import z sieci.";
        }

        if (gridCharge > Epsilon)
        {
            return "To tania godzina, a magazyn trzeba przygotowac na drozszy okres, dlatego system doladowuje go z sieci.";
        }

        if (pvCharge > Epsilon && export > Epsilon)
        {
            return "PV daje nadwyzke. Magazyn laduje sie do bezpiecznego poziomu, a reszta energii trafia do sieci.";
        }

        if (pvCharge > Epsilon)
        {
            return "PV pokrywa obiekt i zostawia nadwyzke, dlatego system laduje magazyn zamiast kupowac energie z sieci.";
        }

        if (export > Epsilon)
        {
            return "PV przekracza chwilowe zuzycie, a magazyn jest juz pelny lub ma wystarczajacy zapas, wiec nadwyzka trafia do sieci.";
        }

        if (netDemand > Epsilon && startingSoc <= minimumSoc + Epsilon)
        {
            return "Magazyn jest przy minimalnym SOC, dlatego system zostawia rezerwe i pokrywa zapotrzebowanie z sieci.";
        }

        if (netDemand > Epsilon && price.CheapHour && endingSoc >= Math.Min(totalBatteryCapacity, targetSoc) - Epsilon)
        {
            return "To tania godzina, ale magazyn ma juz zapas potrzebny na kolejne godziny, wiec dodatkowe ladowanie nie jest potrzebne.";
        }

        if (netDemand > Epsilon && gridImport >= Math.Min(netDemand, gridImportLimit) - Epsilon)
        {
            return "Po uwzglednieniu PV zakup z sieci jest tutaj wystarczajacy, bo ta godzina nie wymaga dodatkowej pracy magazynu.";
        }

        return "Bilans energii jest stabilny, dlatego system utrzymuje biezacy stan bez dodatkowej reakcji.";
    }

    private static double ComputeTargetSoc(
        int currentHour,
        IReadOnlyList<PriceProfileHour> prices,
        IReadOnlyList<double> netLoad,
        double minimumSoc,
        double totalBatteryCapacity,
        double maxDischargePower)
    {
        var futureReserve = Enumerable.Range(currentHour + 1, Math.Max(0, PeriodCount - currentHour - 1))
            .Take(6)
            .Where(hour => prices[hour].ExpensiveHour || netLoad[hour] > maxDischargePower)
            .Select(hour => Math.Min(maxDischargePower, netLoad[hour]))
            .Take(3)
            .Sum();

        return Math.Min(totalBatteryCapacity, minimumSoc + (futureReserve * 0.75));
    }

    private static double Percentile(IReadOnlyList<double> source, double percentile)
    {
        var sorted = source.OrderBy(value => value).ToArray();
        var index = Math.Clamp((int)Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    private sealed record PriceProfileHour(
        int Hour,
        string Label,
        double RdnPricePlnPerMWh,
        double EffectiveBuyPricePlnPerMWh,
        double SellPricePlnPerMWh,
        bool CheapHour,
        bool ExpensiveHour);

    private sealed record HistoryProfile(
        DateOnly Date,
        string DayType,
        double[] HourlyLoadMw);
}
