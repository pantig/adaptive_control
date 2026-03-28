const state = {
  controller: null,
  response: null,
  liveIndex: 0,
  isPlaying: true,
  playbackTimer: null
};

const formatters = {
  currency0: new Intl.NumberFormat("pl-PL", { style: "currency", currency: "PLN", maximumFractionDigits: 0 }),
  number2: new Intl.NumberFormat("pl-PL", { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
  number3: new Intl.NumberFormat("pl-PL", { minimumFractionDigits: 3, maximumFractionDigits: 3 }),
  date: new Intl.DateTimeFormat("pl-PL", { day: "2-digit", month: "2-digit", year: "numeric" })
};

function $(id) {
  return document.getElementById(id);
}

function setLoading(isLoading) {
  document.body.dataset.loading = isLoading ? "true" : "false";
}

function applyScenario(scenario) {
  Object.entries(scenario).forEach(([key, value]) => {
    const element = $(key);
    if (element) {
      element.value = value;
    }
  });
}

function buildRequest() {
  return {
    scenarioDate: $("scenarioDate").value,
    currentLoadMw: Number($("currentLoadMw").value),
    initialStoredEnergyMWh: Number($("initialStoredEnergyMWh").value),
    batteryCount: Number($("batteryCount").value),
    batteryCapacityMWh: Number($("batteryCapacityMWh").value),
    minimumSocPercent: Number($("minimumSocPercent").value),
    gridImportLimitMw: Number($("gridImportLimitMw").value),
    pvMaxMw: Number($("pvMaxMw").value),
    cloudinessPercent: Number($("cloudinessPercent").value),
    distributionFeePlnPerMWh: Number($("distributionFeePlnPerMWh").value),
    exportPriceFactor: Number($("exportPriceFactor").value)
  };
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }

  return response.json();
}

async function loadDefaults() {
  setLoading(true);
  try {
    const response = await fetchJson("/api/demo/defaults");
    applyScenario(response.scenario);
    renderResponse(response);
  } finally {
    setLoading(false);
  }
}

async function runSimulation() {
  stopPlayback(false);

  if (state.controller) {
    state.controller.abort();
  }

  state.controller = new AbortController();
  setLoading(true);

  try {
    const response = await fetchJson("/api/demo/simulate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(buildRequest()),
      signal: state.controller.signal
    });

    renderResponse(response);
  } catch (error) {
    if (error.name !== "AbortError") {
      $("summary").innerHTML = `
        <article class="panel summary-card">
          <p class="summary-label">Blad</p>
          <p class="summary-value">Brak danych</p>
          <p class="summary-note">${error.message}</p>
        </article>`;
    }
  } finally {
    setLoading(false);
  }
}

function renderResponse(response) {
  state.response = response;
  state.liveIndex = 0;

  renderSummary(response);
  renderMeta(response);
  renderList("rulesList", response.rules);
  renderList("assumptionsList", response.assumptions);
  renderHistory(response.history);
  renderHours(response.hours);
  updatePlayPauseButton();
  renderLiveView();
  startPlayback();
}

function renderSummary(response) {
  const { summary } = response;
  $("summary").innerHTML = [
    summaryCard("Koszt dobowy optymalizowany", formatters.currency0.format(summary.optimizedCostPln), summary.note),
    summaryCard("Koszt bez magazynu", formatters.currency0.format(summary.baselineCostPln), "Punkt odniesienia dla tej samej prognozy i cen."),
    summaryCard("Oszczednosc", formatters.currency0.format(summary.savingsPln), `${formatters.number2.format(summary.savingsPercent)}% wzgledem wariantu bez pracy magazynu.`),
    summaryCard("Koncowy SOC", `${formatters.number2.format(summary.finalSocMWh)} MWh`, `Ladowanie: ${formatters.number2.format(summary.totalChargedEnergyMWh)} MWh, rozladowanie: ${formatters.number2.format(summary.totalDischargedEnergyMWh)} MWh.`),
    summaryCard("Ostrzezenia", `${summary.warningHours} h`, `Godziny z limitem przylacza: ${summary.gridLimitHours}, pik importu: ${formatters.number2.format(summary.peakGridImportMw)} MW.`)
  ].join("");
}

function summaryCard(label, value, note) {
  return `
    <article class="panel summary-card">
      <p class="summary-label">${label}</p>
      <p class="summary-value">${value}</p>
      <p class="summary-note">${note}</p>
    </article>`;
}

function renderMeta(response) {
  const { scenario, calendar, inputs } = response;
  const items = [
    ["Kalendarz", `${formatDate(calendar.date)}<br>${calendar.label}`],
    ["Magazyny", `${scenario.batteryCount} x ${formatters.number2.format(scenario.batteryCapacityMWh)} MWh<br>lacznie ${formatters.number2.format(inputs.totalBatteryCapacityMWh)} MWh`],
    ["Ograniczenia", `Min. SOC: ${formatters.number2.format(inputs.minimumSocMWh)} MWh<br>Limit przylacza: ${formatters.number2.format(scenario.gridImportLimitMw)} MW`],
    ["Moc magazynu", `Ladowanie max: ${formatters.number2.format(inputs.maxChargePowerMw)} MW<br>Rozladowanie max: ${formatters.number2.format(inputs.maxDischargePowerMw)} MW`],
    ["Ceny", `RDN srednio: ${formatters.number2.format(inputs.averageRdnPricePlnPerMWh)} PLN/MWh<br>Zakup z dystrybucja: ${formatters.number2.format(inputs.averageEffectiveBuyPricePlnPerMWh)} PLN/MWh`],
    ["Sprzedaz", `Srednia cena oddania: ${formatters.number2.format(inputs.averageSellPricePlnPerMWh)} PLN/MWh<br>Wspolczynnik sprzedazy: ${formatters.number2.format(scenario.exportPriceFactor)}`],
    ["Zrodlo cen", inputs.priceSource],
    ["Historia", inputs.historySource]
  ];

  $("metaGrid").innerHTML = items.map(([title, value]) => `
    <div class="meta-item">
      <strong>${title}</strong>
      <span>${value}</span>
    </div>`).join("");
}

function renderList(id, items) {
  $(id).innerHTML = items.map(item => `<li>${item}</li>`).join("");
}

function renderHistory(history) {
  $("historyBody").innerHTML = history.map(day => `
    <tr>
      <td>${formatDate(day.date)}</td>
      <td>${day.dayType}</td>
      <td>${formatters.number3.format(day.averageLoadMw)}</td>
      <td>${formatters.number3.format(day.peakLoadMw)}</td>
    </tr>`).join("");
}

function renderHours(hours) {
  $("hoursBody").innerHTML = hours.map((hour, index) => {
    const rowClasses = [
      hour.unservedMw > 0 ? "warning" : "",
      hour.expensiveHour ? "expensive" : "",
      hour.cheapHour ? "cheap" : ""
    ].join(" ");

    return `
      <tr class="${rowClasses}" data-hour-index="${index}">
        <td>${hour.label}</td>
        <td>${formatters.number2.format(hour.rdnPricePlnPerMWh)}</td>
        <td>${formatters.number2.format(hour.effectiveBuyPricePlnPerMWh)}</td>
        <td>${formatters.number2.format(hour.sellPricePlnPerMWh)}</td>
        <td>${formatters.number3.format(hour.forecastLoadMw)}</td>
        <td>${formatters.number3.format(hour.forecastPvMw)}</td>
        <td>${formatters.number3.format(hour.gridImportMw)}</td>
        <td>${formatters.number3.format(hour.batteryChargeMw)}</td>
        <td>${formatters.number3.format(hour.batteryDischargeMw)}</td>
        <td>${formatters.number3.format(hour.socMWh)}</td>
        <td>${formatters.currency0.format(hour.optimizedCostPln)}</td>
        <td class="${hour.deltaPln >= 0 ? "delta-positive" : "delta-negative"}">${formatters.currency0.format(hour.deltaPln)}</td>
        <td>${hour.action}</td>
      </tr>`;
  }).join("");
}

function renderLiveView() {
  if (!state.response || !state.response.hours.length) {
    return;
  }

  const hour = state.response.hours[state.liveIndex];
  const netDemand = Math.max(0, hour.forecastLoadMw - hour.forecastPvMw);
  const deltaClass = hour.deltaPln >= 0 ? "delta-positive" : "delta-negative";

  $("liveStatus").innerHTML = `
    <div class="live-status-card">
      <strong>Symulowany czas</strong>
      <div class="live-clock">${hour.label}</div>
      <div class="summary-note">${formatDate(state.response.calendar.date)}</div>
    </div>
    <div class="live-decision">
      <strong>Decyzja systemu</strong>
      <div class="badge-row">
        ${hour.cheapHour ? '<span class="badge price-cheap">tania godzina</span>' : ""}
        ${hour.expensiveHour ? '<span class="badge price-expensive">droga godzina</span>' : ""}
        ${hour.unservedMw > 0 ? '<span class="badge warning">uwaga: brak mocy</span>' : ""}
      </div>
      <p>${hour.action}</p>
    </div>`;

  const liveCards = [
    ["Cena zakupu", `${formatters.number2.format(hour.effectiveBuyPricePlnPerMWh)} PLN/MWh`, `RDN: ${formatters.number2.format(hour.rdnPricePlnPerMWh)} PLN/MWh`],
    ["Cena sprzedazy", `${formatters.number2.format(hour.sellPricePlnPerMWh)} PLN/MWh`, "Cena przy oddawaniu energii do sieci"],
    ["Zuzycie obiektu", `${formatters.number3.format(hour.forecastLoadMw)} MW`, `Po PV zostaje ${formatters.number3.format(netDemand)} MW`],
    ["Produkcja PV", `${formatters.number3.format(hour.forecastPvMw)} MW`, "Prognoza wynikajaca z pogody i pory dnia"],
    ["Praca magazynu", `${formatters.number3.format(hour.batteryChargeMw - hour.batteryDischargeMw)} MW`, `Na magazyn: +${formatters.number3.format(hour.batteryChargeMw)} / -${formatters.number3.format(hour.batteryDischargeMw)} MW`],
    ["SOC", `${formatters.number3.format(hour.socMWh)} MWh`, `Na 1 magazyn: +${formatters.number3.format(hour.chargePerBatteryMw)} / -${formatters.number3.format(hour.dischargePerBatteryMw)} MW`],
    ["Import z sieci", `${formatters.number3.format(hour.gridImportMw)} MW`, hour.gridLimitHit ? "Praca przy limicie przylacza" : "Import miesci sie w limicie"],
    ["Wynik godziny", `<span class="${deltaClass}">${formatters.currency0.format(hour.deltaPln)}</span>`, `Koszt tej godziny: ${formatters.currency0.format(hour.optimizedCostPln)}`]
  ];

  $("liveCards").innerHTML = liveCards.map(([label, value, note]) => `
    <div class="live-card">
      <p class="live-card-label">${label}</p>
      <p class="live-card-value">${value}</p>
      <p class="live-card-note">${note}</p>
    </div>`).join("");

  $("timeline").innerHTML = state.response.hours.map((item, index) => `
    <div class="timeline-item ${item.cheapHour ? "timeline-item-cheap" : ""} ${item.expensiveHour ? "timeline-item-expensive" : ""} ${index === state.liveIndex ? "timeline-item-active" : ""}">
      <div class="timeline-hour">${item.label}</div>
      <div class="timeline-price">${formatters.number2.format(item.effectiveBuyPricePlnPerMWh)}</div>
    </div>`).join("");

  updateLiveRowHighlight();
}

function updateLiveRowHighlight() {
  document.querySelectorAll("#hoursBody tr").forEach(row => {
    row.classList.toggle("active-hour", Number(row.dataset.hourIndex) === state.liveIndex);
  });
}

function startPlayback() {
  if (!state.response || !state.response.hours.length) {
    return;
  }

  clearPlaybackTimer();
  state.isPlaying = true;
  updatePlayPauseButton();
  scheduleNextTick();
}

function stopPlayback(updateButton = true) {
  clearPlaybackTimer();
  state.isPlaying = false;
  if (updateButton) {
    updatePlayPauseButton();
  }
}

function clearPlaybackTimer() {
  if (state.playbackTimer) {
    clearTimeout(state.playbackTimer);
    state.playbackTimer = null;
  }
}

function scheduleNextTick() {
  clearPlaybackTimer();
  if (!state.isPlaying || !state.response) {
    return;
  }

  state.playbackTimer = setTimeout(() => {
    stepLiveHour(true);
  }, getPlaybackSpeed());
}

function stepLiveHour(continuePlaying) {
  if (!state.response || !state.response.hours.length) {
    return;
  }

  state.liveIndex = (state.liveIndex + 1) % state.response.hours.length;
  renderLiveView();

  if (continuePlaying && state.isPlaying) {
    scheduleNextTick();
  }
}

function resetLiveHour() {
  state.liveIndex = 0;
  renderLiveView();
  if (state.isPlaying) {
    scheduleNextTick();
  }
}

function getPlaybackSpeed() {
  return Number($("speedSelect").value || 1000);
}

function updatePlayPauseButton() {
  $("playPauseBtn").textContent = state.isPlaying ? "Pauza" : "Start";
}

function formatDate(value) {
  return formatters.date.format(new Date(`${value}T00:00:00`));
}

function scheduleSimulation() {
  clearTimeout(scheduleSimulation.timer);
  scheduleSimulation.timer = setTimeout(runSimulation, 250);
}

function bindEvents() {
  document.querySelectorAll("input").forEach(input => {
    input.addEventListener("input", scheduleSimulation);
    input.addEventListener("change", scheduleSimulation);
  });

  $("playPauseBtn").addEventListener("click", () => {
    if (state.isPlaying) {
      stopPlayback();
    } else {
      startPlayback();
    }
  });

  $("resetBtn").addEventListener("click", () => {
    clearPlaybackTimer();
    resetLiveHour();
  });

  $("stepBtn").addEventListener("click", () => {
    stopPlayback();
    stepLiveHour(false);
  });

  $("speedSelect").addEventListener("change", () => {
    if (state.isPlaying) {
      scheduleNextTick();
    }
  });
}

bindEvents();
loadDefaults();
