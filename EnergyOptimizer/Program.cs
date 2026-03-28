using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// --- Dane TGE RDN (symulacja profilu godzinowego) ---
double[] cenyEE  = {450,480,500,520,550,600,650,700,750,800,780,720,680,650,700,750,800,850,900,880,820,750,680,600};
double[] cenyGaz = {280,290,300,310,320,330,340,350,360,370,360,350,340,330,340,350,360,370,380,370,360,350,340,320};

// --- Endpoint algorytmu ---
app.MapGet("/api/algorytm", (double p1, double p2, double p3, double p4, int godzina = 12) =>
{
    var h = new HeurystykaSterowania();
    var wynik = h.Oblicz(p1, p2, p3, p4, cenyGaz[Math.Clamp(godzina - 1, 0, 23)]);
    return Results.Ok(wynik);
});

// --- Endpoint cen TGE ---
app.MapGet("/api/ceny", (int godz) =>
{
    int idx = Math.Clamp(godz - 1, 0, 23);
    double ee  = cenyEE[idx];
    double gaz = cenyGaz[idx];
    return Results.Ok(new
    {
        CenaEE       = ee,
        CenaGaz      = gaz,
        WszystkieEE  = cenyEE,
        WszystkieGaz = cenyGaz,
        Porownanie   = ee > gaz * 1.2 ? "Użyj gazu / magazynu" : "Sieć wystarczy"
    });
});

app.MapFallbackToFile("index.html");
app.Run();

// ============================================================
// Klasa heurystyki
// ============================================================
public class HeurystykaSterowania
{
    public record Wynik(
        double P_gas,
        double P_pv,
        double P_magazyn,
        double P_siec,
        double Koszt,
        double SOC_po,
        string Zalecenie,
        string Tryb
    );

    public Wynik Oblicz(double p1, double p2, double p3, double p4,
                        double cena_gaz_MWh = 330,
                        double P_pv = 0.5, double P_gas_max = 0.5,
                        double P_charge_max = 1.0, double SOC_total = 3.0,
                        double eta = 0.8)
    {
        double SOC_min    = 0.2 * SOC_total; // 20% = 0.6 MWh
        double P_demand   = Math.Max(p2, p3);
        double P_deficit  = Math.Max(0, P_demand - P_pv);
        double P_load_cap = Math.Min(p1 / eta, P_charge_max); // ile możemy wcisnąć do magazynu w tej godzinie
        double koszt_gaz  = cena_gaz_MWh / 1000.0 / 0.83;   // zł/kWh z uwzgl. sprawności generatora 83%

        double P_gas_opt;
        string tryb;

        if (p1 < SOC_min)
        {
            // PRIORYTET: magazyn poniżej 20% – ładuj zawsze
            P_gas_opt = Math.Min(P_gas_max, P_deficit + P_load_cap);
            tryb = "ALARM_SOC";
        }
        else if (p4 > cena_gaz_MWh * 1.2)
        {
            // Cena sieci wysoka – opłaca się gaz + ładowanie nadwyżką
            P_gas_opt = Math.Min(P_gas_max, P_deficit + P_load_cap);
            tryb = "OPTYMALIZACJA_KOSZTOW";
        }
        else if (p4 < cena_gaz_MWh * 0.8)
        {
            // Sieć tania – rozładuj magazyn, wyłącz gaz
            P_gas_opt = 0;
            tryb = "TANIA_SIEC";
        }
        else
        {
            // Normalny tryb – gaz tylko na deficyt
            P_gas_opt = P_deficit;
            tryb = "NORMALNY";
        }

        P_gas_opt = Math.Max(0, Math.Min(P_gas_opt, P_gas_max));

        double P_gen_total = P_pv + P_gas_opt;
        double P_magazyn   = Math.Min(P_gen_total - P_demand, P_load_cap); // + ładowanie, - rozładowanie
        double P_siec      = Math.Max(0, P_demand - P_gen_total - (P_magazyn < 0 ? -P_magazyn : 0));
        double SOC_po      = Math.Clamp(p1 - P_magazyn * eta, 0, SOC_total);
        double koszt_h     = P_gas_opt * koszt_gaz + P_siec * (p4 / 1000.0); // zł/h

        string zalecenie = tryb switch
        {
            "ALARM_SOC"            => $"⚠️ SOC krytyczny! Ładuj magazyn – P_gas={P_gas_opt:F2} MW",
            "OPTYMALIZACJA_KOSZTOW" => $"💰 Cena EE wysoka – uruchom gaz {P_gas_opt:F2} MW + ładowanie",
            "TANIA_SIEC"           => $"🔌 Sieć tania – wyłącz gaz, kup z sieci",
            _                      => $"⚙️ Tryb normalny – gaz {P_gas_opt:F2} MW"
        };

        return new(P_gas_opt, P_pv, P_magazyn, P_siec, Math.Round(koszt_h, 2), Math.Round(SOC_po, 3), zalecenie, tryb);
    }
}
