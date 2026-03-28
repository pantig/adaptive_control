namespace EnergyOptimizer;

public sealed record ScenarioRequest(
    DateOnly? ScenarioDate = null,
    double CurrentLoadMw = 0.58,
    double InitialStoredEnergyMWh = 0.30,
    int BatteryCount = 1,
    double BatteryCapacityMWh = 0.50,
    double GridImportLimitMw = 0.30,
    double PvMaxMw = 0.50,
    double CloudinessPercent = 35,
    double DistributionFeePlnPerMWh = 110,
    double ExportPriceFactor = 0.70,
    double MinimumSocPercent = 20);

public sealed record SimulationResponse(
    ScenarioRequest Scenario,
    CalendarInsight Calendar,
    InputSummary Inputs,
    IReadOnlyList<HistoricalDay> History,
    IReadOnlyList<SimulationHour> Hours,
    SimulationSummary Summary,
    IReadOnlyList<string> Rules,
    IReadOnlyList<string> Assumptions);

public sealed record CalendarInsight(
    DateOnly Date,
    bool IsWeekend,
    bool IsHoliday,
    bool IsNonWorkingDay,
    string Label,
    string HolidayName);

public sealed record InputSummary(
    double TotalBatteryCapacityMWh,
    double MinimumSocMWh,
    double MaxChargePowerMw,
    double MaxDischargePowerMw,
    double AverageRdnPricePlnPerMWh,
    double AverageEffectiveBuyPricePlnPerMWh,
    double AverageSellPricePlnPerMWh,
    string PriceSource,
    string HistorySource);

public sealed record HistoricalDay(
    DateOnly Date,
    string DayType,
    double AverageLoadMw,
    double PeakLoadMw);

public sealed record FutureSignal(
    int Hour,
    string Label,
    double EffectiveBuyPricePlnPerMWh,
    double ForecastLoadMw,
    double ForecastPvMw,
    double NetDemandMw,
    bool CheapHour,
    bool ExpensiveHour);

public sealed record DecisionContext(
    double StartingSocMWh,
    double AvailableSocMWh,
    double MinimumSocMWh,
    double RemainingCapacityMWh,
    double MaxChargePowerMw,
    double MaxDischargePowerMw,
    double TargetSocMWh,
    double EnergyNeededToTargetMWh,
    double HoursToReachTarget,
    double EnergyPricePlnPerMWh,
    double DistributionFeePlnPerMWh,
    double EffectiveBuyPricePlnPerMWh,
    double SellPricePlnPerMWh,
    double FuturePeakBuyPricePlnPerMWh,
    double FuturePeakSellPricePlnPerMWh,
    double FutureLowestBuyPricePlnPerMWh,
    double AverageNextThreeBuyPricePlnPerMWh,
    double AverageNextThreeLoadMw,
    double AverageNextThreePvMw,
    int ChargeSlotsBeforeNeed,
    string FirstCriticalHourLabel,
    double ExportReserveSocMWh,
    double ExportableEnergyMWh,
    double SellVsFutureBuySpreadPlnPerMWh,
    double HoursToRestoreExportableEnergy,
    bool ArbitrageOpportunity,
    bool CanRestoreAfterExport,
    IReadOnlyList<FutureSignal> FutureSignals);

public sealed record SimulationHour(
    int Hour,
    string Label,
    double RdnPricePlnPerMWh,
    double EffectiveBuyPricePlnPerMWh,
    double SellPricePlnPerMWh,
    double ForecastLoadMw,
    double ForecastPvMw,
    double GridImportMw,
    double BatteryChargeMw,
    double BatteryDischargeMw,
    double ChargePerBatteryMw,
    double DischargePerBatteryMw,
    double ExportMw,
    double BatteryExportMw,
    double PvExportMw,
    double SocMWh,
    double BaselineCostPln,
    double OptimizedCostPln,
    double DeltaPln,
    string Action,
    string DecisionReason,
    DecisionContext DecisionContext,
    bool CheapHour,
    bool ExpensiveHour,
    bool GridLimitHit,
    double UnservedMw);

public sealed record SimulationSummary(
    double OptimizedCostPln,
    double BaselineCostPln,
    double SavingsPln,
    double SavingsPercent,
    double FinalSocMWh,
    double PeakGridImportMw,
    double TotalGridEnergyMWh,
    double TotalChargedEnergyMWh,
    double TotalDischargedEnergyMWh,
    double TotalExportedEnergyMWh,
    int GridLimitHours,
    int WarningHours,
    string Note);
