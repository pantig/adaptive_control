# EnergyOptimizer – Makieta algorytmu sterowania energią

Aplikacja webowa .NET 10 (Minimal API) prezentująca heurystyczny algorytm sterowania mocą generatora hybrydowego (PV + gaz) z magazynem energii.

## Architektura systemu

- **PV**: 0.5 MW
- **Generator gazowy**: 0–0.5 MW (sterowany)
- **Magazyn energii**: 3 MWh, η=80%, SOC_min=20%
- **Ceny referencyjne**: TGE RDN (~450–900 zł/MWh EE, ~280–380 zł/MWh gaz)

## Parametry algorytmu

| Param | Opis | Zakres |
|-------|------|--------|
| p1 | Dostępna pojemność magazynu [MWh] | 0–3 |
| p2 | Aktualna moc odbiorów [MW] | 0–1 |
| p3 | Prognozowana moc odbiorów [MW] | 0–1 |
| p4 | Przewidywana cena EE [zł/MWh] | 300–1000 |

## Wymagania

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Przeglądarka z obsługą JavaScript

## Uruchomienie

```bash
git clone https://github.com/pantig/adaptive_control.git
cd adaptive_control/EnergyOptimizer
dotnet run
```

Aplikacja uruchamia się na `http://localhost:5000`.

## Budowanie i publikacja

```bash
dotnet publish -c Release -o ./publish
./publish/EnergyOptimizer
```

## Endpointy API

- `GET /api/algorytm?p1=2&p2=0.6&p3=0.7&p4=800` → wynik sterowania
- `GET /api/ceny?godz=12` → ceny TGE dla danej godziny

## Logika heurystyki

1. PV (0.5 MW) traktowane jako baza – zawsze dostępne
2. Deficyt = max(p2, p3) – P_pv
3. Jeśli p4 > cena_gaz × 1.2 → uruchom gaz + ładuj magazyn
4. Jeśli SOC < 20% → zawsze ładuj magazyn przez gaz
5. W przeciwnym razie → minimalny gaz (tylko deficyt)
