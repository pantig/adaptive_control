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
        const double Epsilon = 0.0001;
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
            var targetSoc = ComputeTargetSoc(hour, prices, netLoad, minimumSoc, totalBatteryCapacity, maxDischargePower);
            var decisionContext = BuildDecisionContext(
                hour,
                prices,
                loadForecast,
                pvForecast,
                startingSoc,
                minimumSoc,
                totalBatteryCapacity,
                maxChargePower,
                maxDischargePower,
                targetSoc,
                scenario.GridImportLimitMw,
                scenario.DistributionFeePlnPerMWh);

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
            var batteryExport = 0.0;
            var pvExport = 0.0;
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

                    if (discharge > Epsilon)
                    {
                        remainingLoad -= discharge;
                        soc -= discharge;
                        actions.Add(price.ExpensiveHour ? "rozładowanie magazynu" : "ochrona limitu przyłącza");
                    }
                }

                gridImport = Math.Min(scenario.GridImportLimitMw, remainingLoad);
                remainingLoad -= gridImport;

                if (remainingLoad > Epsilon)
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
                if (pvCharge > Epsilon)
                {
                    soc += pvCharge;
                    actions.Add("ładowanie z PV");
                }

                pvExport = Math.Max(0, surplusPv - pvCharge);
                export = pvExport;
                if (pvExport > Epsilon)
                {
                    actions.Add("oddanie nadwyżki do sieci");
                }
            }

            var exportFloorSoc = Math.Max(decisionContext.TargetSocMWh, decisionContext.ExportReserveSocMWh);
            var availableExportEnergy = Math.Max(0, soc - exportFloorSoc);
            var availableExportPower = Math.Max(0, maxDischargePower - discharge);
            var profitableBatteryExport = charge < Epsilon
                && gridImport < Epsilon
                && unserved < Epsilon
                && availableExportEnergy > Epsilon
                && availableExportPower > Epsilon
                && decisionContext.ArbitrageOpportunity;

            if (profitableBatteryExport)
            {
                batteryExport = Math.Min(availableExportPower, availableExportEnergy);
                discharge += batteryExport;
                export += batteryExport;
                soc -= batteryExport;
                actions.Add("sprzedaz z magazynu do sieci");
            }

            var batteryHasRoom = soc < totalBatteryCapacity - Epsilon;
            var canGridCharge = discharge < Epsilon && price.CheapHour && batteryHasRoom && soc < targetSoc - Epsilon;
            if (canGridCharge)
            {
                var availableChargePower = Math.Max(0, maxChargePower - charge);
                var gridHeadroom = Math.Max(0, scenario.GridImportLimitMw - gridImport);
                var energyNeeded = Math.Max(0, targetSoc - soc);
                var extraCharge = Math.Min(Math.Min(availableChargePower, gridHeadroom), Math.Min(totalBatteryCapacity - soc, energyNeeded));

                if (extraCharge > Epsilon)
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
                decisionContext,
                load,
                pv,
                scenario.GridImportLimitMw,
                pvCharge,
                gridCharge,
                discharge,
                batteryExport,
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
                BatteryExportMw: Math.Round(batteryExport, 3),
                PvExportMw: Math.Round(pvExport, 3),
                SocMWh: Math.Round(soc, 3),
                BaselineCostPln: Math.Round(baselineCost, 2),
                OptimizedCostPln: Math.Round(optimizedCost, 2),
                DeltaPln: Math.Round(baselineCost - optimizedCost, 2),
                Action: actions.Count > 0 ? string.Join(", ", actions.Distinct()) : "bez działania",
                DecisionReason: decisionReason,
                DecisionContext: decisionContext,
                CheapHour: price.CheapHour,
                ExpensiveHour: price.ExpensiveHour,
                GridLimitHit: gridImport >= scenario.GridImportLimitMw - Epsilon || unserved > 0,
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
        DecisionContext decisionContext,
        double load,
        double pv,
        double gridImportLimit,
        double pvCharge,
        double gridCharge,
        double discharge,
        double batteryExport,
        double export,
        double gridImport,
        double unserved)
    {
        const double Epsilon = 0.0001;
        var netDemand = Math.Max(0, load - pv);
        var futureSummary = decisionContext.FutureSignals.Count > 0
            ? string.Join(", ", decisionContext.FutureSignals.Select(signal => $"{signal.Label}: {signal.EffectiveBuyPricePlnPerMWh:0} PLN/MWh"))
            : "brak kolejnych godzin";

        if (unserved > Epsilon)
        {
            return $"Forecast poboru {load:0.000} MW i PV {pv:0.000} MW daje {netDemand:0.000} MW zapotrzebowania przy limicie {gridImportLimit:0.000} MW. Dostepny SOC ponad minimum to {decisionContext.AvailableSocMWh:0.000} MWh, ale nadal brakuje mocy.";
        }

        if (gridCharge > Epsilon)
        {
            return $"Zakup teraz kosztuje {decisionContext.EffectiveBuyPricePlnPerMWh:0} PLN/MWh (energia {decisionContext.EnergyPricePlnPerMWh:0} + dystrybucja {decisionContext.DistributionFeePlnPerMWh:0}). Przyszle ceny dochodza do {decisionContext.FuturePeakBuyPricePlnPerMWh:0} PLN/MWh [{futureSummary}], a do celu SOC {decisionContext.TargetSocMWh:0.000} MWh brakuje {decisionContext.EnergyNeededToTargetMWh:0.000} MWh, czyli ok. {decisionContext.HoursToReachTarget:0.0} h ladowania.";
        }

        if (batteryExport > Epsilon)
        {
            if (decisionContext.FutureSignals.Count == 0)
            {
                return $"To koniec horyzontu doby, wiec system sprzedaje {batteryExport:0.000} MW z magazynu. Po zachowaniu rezerwy {decisionContext.ExportReserveSocMWh:0.000} MWh zostaje {decisionContext.ExportableEnergyMWh:0.000} MWh energii, ktora warto zmonetyzowac po {decisionContext.SellPricePlnPerMWh:0} PLN/MWh.";
            }

            return $"Cena sprzedazy {decisionContext.SellPricePlnPerMWh:0} PLN/MWh jest wyzsza od najtanszego przyszlego zakupu {decisionContext.FutureLowestBuyPricePlnPerMWh:0} PLN/MWh o {decisionContext.SellVsFutureBuySpreadPlnPerMWh:0} PLN/MWh. Magazyn ma {decisionContext.ExportableEnergyMWh:0.000} MWh ponad rezerwe {decisionContext.ExportReserveSocMWh:0.000} MWh i odtworzy ten zapas w ok. {decisionContext.HoursToRestoreExportableEnergy:0.0} h, dlatego system eksportuje energie do sieci.";
        }

        if (discharge > Epsilon && netDemand > gridImportLimit)
        {
            return $"Forecast poboru {load:0.000} MW i PV {pv:0.000} MW przekracza limit przylacza {gridImportLimit:0.000} MW. System wykorzystuje dostepny SOC {decisionContext.AvailableSocMWh:0.000} MWh, aby ograniczyc import z sieci.";
        }

        if (discharge > Epsilon)
        {
            return $"To droga godzina: zakup kosztuje {decisionContext.EffectiveBuyPricePlnPerMWh:0} PLN/MWh, a w kolejnych godzinach ceny wygladaja tak: [{futureSummary}]. Magazyn ma dostepne {decisionContext.AvailableSocMWh:0.000} MWh ponad minimum, dlatego system korzysta z niego zamiast kupowac energie z sieci.";
        }

        if (pvCharge > Epsilon && export > Epsilon)
        {
            return $"PV pokrywa obiekt i daje nadwyzke. Magazyn ma jeszcze {decisionContext.RemainingCapacityMWh:0.000} MWh wolnego miejsca, dlatego laduje sie z PV, a reszta energii trafia do sieci po {decisionContext.SellPricePlnPerMWh:0} PLN/MWh.";
        }

        if (pvCharge > Epsilon)
        {
            return $"PV pokrywa obiekt i zostawia nadwyzke, dlatego system laduje magazyn. Docelowy poziom SOC to {decisionContext.TargetSocMWh:0.000} MWh, a przyszle ceny dochodza do {decisionContext.FuturePeakBuyPricePlnPerMWh:0} PLN/MWh.";
        }

        if (export > Epsilon)
        {
            return $"PV przekracza chwilowe zuzycie, a magazyn ma juz wystarczajacy zapas lub za malo wolnego miejsca ({decisionContext.RemainingCapacityMWh:0.000} MWh), wiec nadwyzka trafia do sieci.";
        }

        if (netDemand > Epsilon && decisionContext.AvailableSocMWh <= Epsilon)
        {
            return $"Magazyn jest przy minimalnym SOC {decisionContext.MinimumSocMWh:0.000} MWh, dlatego system zostawia rezerwe i pokrywa zapotrzebowanie z sieci.";
        }

        if (netDemand > Epsilon && price.CheapHour && decisionContext.EnergyNeededToTargetMWh <= Epsilon)
        {
            return $"To tania godzina, ale docelowy poziom SOC {decisionContext.TargetSocMWh:0.000} MWh jest juz osiagniety. Kolejne ceny to [{futureSummary}], wiec dodatkowe ladowanie nie jest potrzebne.";
        }

        if (netDemand > Epsilon && gridImport >= Math.Min(netDemand, gridImportLimit) - Epsilon)
        {
            return $"Po uwzglednieniu PV zakup z sieci jest tutaj wystarczajacy. Zakup kosztuje {decisionContext.EffectiveBuyPricePlnPerMWh:0} PLN/MWh, a magazyn zachowuje rezerwe {decisionContext.TargetSocMWh:0.000} MWh na kolejne godziny.";
        }

        return $"Bilans energii jest stabilny. Zakup kosztuje {decisionContext.EffectiveBuyPricePlnPerMWh:0} PLN/MWh, przyszly szczyt to {decisionContext.FuturePeakBuyPricePlnPerMWh:0} PLN/MWh, a kolejne ceny to [{futureSummary}], dlatego system utrzymuje biezacy stan.";
    }

    private static DecisionContext BuildDecisionContext(
        int currentHour,
        IReadOnlyList<PriceProfileHour> prices,
        IReadOnlyList<double> loadForecast,
        IReadOnlyList<double> pvForecast,
        double startingSoc,
        double minimumSoc,
        double totalBatteryCapacity,
        double maxChargePower,
        double maxDischargePower,
        double targetSoc,
        double gridImportLimit,
        double distributionFeePlnPerMWh)
    {
        var currentPrice = prices[currentHour];
        var futureHours = Enumerable.Range(currentHour + 1, Math.Max(0, PeriodCount - currentHour - 1)).ToArray();
        var futureSignals = futureHours
            .Take(3)
            .Select(hour =>
            {
                var netDemand = Math.Max(0, loadForecast[hour] - pvForecast[hour]);
                return new FutureSignal(
                    Hour: hour,
                    Label: prices[hour].Label,
                    EffectiveBuyPricePlnPerMWh: Math.Round(prices[hour].EffectiveBuyPricePlnPerMWh, 2),
                    ForecastLoadMw: Math.Round(loadForecast[hour], 3),
                    ForecastPvMw: Math.Round(pvForecast[hour], 3),
                    NetDemandMw: Math.Round(netDemand, 3),
                    CheapHour: prices[hour].CheapHour,
                    ExpensiveHour: prices[hour].ExpensiveHour);
            })
            .ToList();

        var futurePeakBuy = futureHours.Length > 0
            ? futureHours.Max(hour => prices[hour].EffectiveBuyPricePlnPerMWh)
            : currentPrice.EffectiveBuyPricePlnPerMWh;

        var futurePeakSell = futureHours.Length > 0
            ? futureHours.Max(hour => prices[hour].SellPricePlnPerMWh)
            : currentPrice.SellPricePlnPerMWh;

        var futureLowestBuy = futureHours.Length > 0
            ? futureHours.Min(hour => prices[hour].EffectiveBuyPricePlnPerMWh)
            : currentPrice.EffectiveBuyPricePlnPerMWh;

        var averageNextThreeBuy = futureSignals.Count > 0
            ? futureSignals.Average(signal => signal.EffectiveBuyPricePlnPerMWh)
            : currentPrice.EffectiveBuyPricePlnPerMWh;

        var averageNextThreeLoad = futureSignals.Count > 0
            ? futureSignals.Average(signal => signal.ForecastLoadMw)
            : loadForecast[currentHour];

        var averageNextThreePv = futureSignals.Count > 0
            ? futureSignals.Average(signal => signal.ForecastPvMw)
            : pvForecast[currentHour];

        var currentBuy = currentPrice.EffectiveBuyPricePlnPerMWh;
        var spreadThreshold = Math.Max(35, currentBuy * 0.08);
        int? firstCriticalHour = null;

        foreach (var hour in futureHours)
        {
            var netDemand = Math.Max(0, loadForecast[hour] - pvForecast[hour]);
            var chargeHeadroom = Math.Max(0, gridImportLimit - netDemand);
            if (netDemand > 0.0001 &&
                (prices[hour].EffectiveBuyPricePlnPerMWh >= currentBuy + spreadThreshold ||
                 netDemand > maxDischargePower ||
                 chargeHeadroom < maxChargePower * 0.35))
            {
                firstCriticalHour = hour;
                break;
            }
        }

        var lastChargeHour = firstCriticalHour is null ? PeriodCount - 1 : firstCriticalHour.Value - 1;
        var chargeSlotsBeforeNeed = lastChargeHour > currentHour
            ? Enumerable.Range(currentHour + 1, lastChargeHour - currentHour)
                .Count(hour =>
                {
                    var netDemand = Math.Max(0, loadForecast[hour] - pvForecast[hour]);
                    return Math.Max(0, gridImportLimit - netDemand) >= Math.Max(0.05, maxChargePower * 0.35);
                })
            : 0;

        var energyNeededToTarget = Math.Max(0, targetSoc - startingSoc);
        var hoursToReachTarget = maxChargePower > 0.0001 ? energyNeededToTarget / maxChargePower : 0;
        var exportReserveSoc = ComputeExportReserveSoc(
            currentHour,
            prices,
            loadForecast,
            pvForecast,
            minimumSoc,
            totalBatteryCapacity,
            maxDischargePower,
            gridImportLimit);
        var exportFloorSoc = Math.Max(targetSoc, exportReserveSoc);
        var exportableEnergy = Math.Max(0, startingSoc - exportFloorSoc);
        var sellVsFutureBuySpread = futureHours.Length > 0
            ? currentPrice.SellPricePlnPerMWh - futureLowestBuy
            : currentPrice.SellPricePlnPerMWh;
        var hoursToRestoreExportableEnergy = maxChargePower > 0.0001 ? exportableEnergy / maxChargePower : 0;
        var canRestoreAfterExport = firstCriticalHour is null
            || chargeSlotsBeforeNeed >= (int)Math.Ceiling(hoursToRestoreExportableEnergy - 0.0001);
        var arbitrageOpportunity = exportableEnergy > 0.0001
            && (futureHours.Length == 0 || sellVsFutureBuySpread >= 20)
            && canRestoreAfterExport;

        return new DecisionContext(
            StartingSocMWh: Math.Round(startingSoc, 3),
            AvailableSocMWh: Math.Round(Math.Max(0, startingSoc - minimumSoc), 3),
            MinimumSocMWh: Math.Round(minimumSoc, 3),
            RemainingCapacityMWh: Math.Round(Math.Max(0, totalBatteryCapacity - startingSoc), 3),
            MaxChargePowerMw: Math.Round(maxChargePower, 3),
            MaxDischargePowerMw: Math.Round(maxDischargePower, 3),
            TargetSocMWh: Math.Round(targetSoc, 3),
            EnergyNeededToTargetMWh: Math.Round(energyNeededToTarget, 3),
            HoursToReachTarget: Math.Round(hoursToReachTarget, 2),
            EnergyPricePlnPerMWh: Math.Round(currentPrice.RdnPricePlnPerMWh, 2),
            DistributionFeePlnPerMWh: Math.Round(distributionFeePlnPerMWh, 2),
            EffectiveBuyPricePlnPerMWh: Math.Round(currentPrice.EffectiveBuyPricePlnPerMWh, 2),
            SellPricePlnPerMWh: Math.Round(currentPrice.SellPricePlnPerMWh, 2),
            FuturePeakBuyPricePlnPerMWh: Math.Round(futurePeakBuy, 2),
            FuturePeakSellPricePlnPerMWh: Math.Round(futurePeakSell, 2),
            FutureLowestBuyPricePlnPerMWh: Math.Round(futureLowestBuy, 2),
            AverageNextThreeBuyPricePlnPerMWh: Math.Round(averageNextThreeBuy, 2),
            AverageNextThreeLoadMw: Math.Round(averageNextThreeLoad, 3),
            AverageNextThreePvMw: Math.Round(averageNextThreePv, 3),
            ChargeSlotsBeforeNeed: chargeSlotsBeforeNeed,
            FirstCriticalHourLabel: firstCriticalHour is null ? "brak" : prices[firstCriticalHour.Value].Label,
            ExportReserveSocMWh: Math.Round(exportReserveSoc, 3),
            ExportableEnergyMWh: Math.Round(exportableEnergy, 3),
            SellVsFutureBuySpreadPlnPerMWh: Math.Round(sellVsFutureBuySpread, 2),
            HoursToRestoreExportableEnergy: Math.Round(hoursToRestoreExportableEnergy, 2),
            ArbitrageOpportunity: arbitrageOpportunity,
            CanRestoreAfterExport: canRestoreAfterExport,
            FutureSignals: futureSignals);
    }

    private static double ComputeTargetSoc(
        int currentHour,
        IReadOnlyList<PriceProfileHour> prices,
        IReadOnlyList<double> netLoad,
        double minimumSoc,
        double totalBatteryCapacity,
        double maxDischargePower)
    {
        var currentBuy = prices[currentHour].EffectiveBuyPricePlnPerMWh;
        var spreadThreshold = Math.Max(35, currentBuy * 0.08);
        var futureReserve = Enumerable.Range(currentHour + 1, Math.Max(0, PeriodCount - currentHour - 1))
            .Take(8)
            .Where(hour =>
                (prices[hour].EffectiveBuyPricePlnPerMWh >= currentBuy + spreadThreshold || prices[hour].ExpensiveHour) &&
                netLoad[hour] > 0.0001)
            .Select(hour => Math.Min(maxDischargePower, netLoad[hour]))
            .Take(4)
            .Sum();

        return Math.Min(totalBatteryCapacity, minimumSoc + (futureReserve * 0.85));
    }

    private static double ComputeExportReserveSoc(
        int currentHour,
        IReadOnlyList<PriceProfileHour> prices,
        IReadOnlyList<double> loadForecast,
        IReadOnlyList<double> pvForecast,
        double minimumSoc,
        double totalBatteryCapacity,
        double maxDischargePower,
        double gridImportLimit)
    {
        var reserve = 0.0;
        var consideredHours = 0;

        for (var hour = currentHour + 1; hour < PeriodCount && consideredHours < 4; hour++)
        {
            var netDemand = Math.Max(0, loadForecast[hour] - pvForecast[hour]);
            reserve += Math.Min(maxDischargePower, netDemand);
            consideredHours++;

            var chargeHeadroom = Math.Max(0, gridImportLimit - netDemand);
            if (prices[hour].CheapHour && chargeHeadroom >= Math.Max(0.05, maxDischargePower * 0.60))
            {
                break;
            }
        }

        return Math.Min(totalBatteryCapacity, minimumSoc + (reserve * 0.85));
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
