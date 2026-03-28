# Energy Optimizer

Prosta aplikacja webowa `.NET 10`, która pokazuje dobową symulację sterowania magazynem energii dla układu:

- `sieć`
- `PV`
- `magazyn energii`

W aplikacji jest też tryb `live`, który odtwarza kolejne godziny doby na danych testowych i pokazuje:

- zmieniające się ceny
- zmieniające się parametry wejściowe w danej godzinie
- aktualną decyzję systemu
- aktywną godzinę w harmonogramie 24h

## Cel

Aplikacja ma w prosty sposób pokazać, jak dane wejściowe wpływają na decyzję algorytmu:

1. ceny `RDN 1h`
2. kalendarz `dzień roboczy / weekend / święto`
3. historyczne dane zużycia z ostatnich `7 dni`
4. aktualne zużycie obiektu
5. początkową energię w magazynie
6. liczbę magazynów
7. pojemność magazynów
8. pogodę wpływającą na produkcję `PV`

## Co liczy algorytm

Dla każdej godziny doby symulacja wyznacza:

- prognozę zużycia
- prognozę produkcji z `PV`
- import energii z sieci
- ładowanie magazynu
- rozładowanie magazynu
- oddanie energii do sieci
- koszt godzinowy i koszt dobowy

Koszt dobowy uwzględnia:

- cenę zakupu energii
- cenę sprzedaży energii do sieci
- opłatę dystrybucyjną

## Ograniczenia w modelu

- maksymalny pobór z przyłącza
- maksymalna moc `PV`
- minimalny poziom naładowania magazynu
- maksymalne ładowanie i rozładowanie magazynu
- równomierny podział ładowania i rozładowania między magazyny

## Najważniejsze uproszczenia

- ceny `RDN 1h` są profilem demonstracyjnym
- historia tygodniowa jest syntetyczna
- maksymalna moc ładowania i rozładowania jednego magazynu to `25%` jego pojemności na godzinę
- symulacja nie używa generatora gazowego, bo ta wersja ma pokazać prosty wariant spełniający wskazane wymagania

## Endpointy

- `GET /api/demo/defaults`
- `POST /api/demo/simulate`

## Uruchomienie

```powershell
dotnet build adaptive_control.sln
dotnet run --project EnergyOptimizer/EnergyOptimizer.csproj
```

## Uruchomienie w Docker

```powershell
docker compose up --build
```

Aplikacja będzie dostępna pod adresem `http://localhost:8080`.

Możesz też zbudować i uruchomić obraz ręcznie:

```powershell
docker build -t energy-optimizer .
docker run --rm -p 8080:8080 energy-optimizer
```

## Weryfikacja

Sprawdzone lokalnie:

- `dotnet build adaptive_control.sln`
- `node --check EnergyOptimizer/wwwroot/app.js`
- `GET /`
- `GET /api/demo/defaults`
- `GET /app.js`
- `GET /styles.css`

Środowisko robocze nie miało dostępnego polecenia `docker`, więc pliki kontenerowe zostały przygotowane, ale sam `docker build` i `docker compose up` nie mogły zostać uruchomione tutaj.
