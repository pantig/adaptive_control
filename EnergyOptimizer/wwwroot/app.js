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

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
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
    renderResponse(response, true);
  } finally {
    setLoading(false);
  }
}

async function runSimulation() {
  const shouldResumePlayback = state.isPlaying;
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

    renderResponse(response, shouldResumePlayback);
  } catch (error) {
    if (error.name !== "AbortError") {
      $("summary").innerHTML = `
        <article class="panel summary-card">
          <p class="summary-label">Blad</p>
          <p class="summary-value">Brak danych</p>
          <p class="summary-note">${escapeHtml(error.message)}</p>
        </article>`;
    }
  } finally {
    setLoading(false);
  }
}

function renderResponse(response, autoStart = true) {
  state.response = response;
  state.liveIndex = 0;

  renderSummary(response);
  renderMeta(response);
  renderList("rulesList", response.rules);
  renderList("assumptionsList", response.assumptions);
  renderHistory(response.history);
  renderHours(response.hours);

  if (autoStart) {
    startPlayback();
  } else {
    stopPlayback();
  }

  renderLiveView();
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
      <p class="summary-label">${escapeHtml(label)}</p>
      <p class="summary-value">${value}</p>
      <p class="summary-note">${escapeHtml(note)}</p>
    </article>`;
}

function renderMeta(response) {
  const { scenario, calendar, inputs } = response;
  const items = [
    ["Kalendarz", `${formatDate(calendar.date)}<br>${escapeHtml(calendar.label)}`],
    ["Magazyny", `${scenario.batteryCount} x ${formatters.number2.format(scenario.batteryCapacityMWh)} MWh<br>lacznie ${formatters.number2.format(inputs.totalBatteryCapacityMWh)} MWh`],
    ["Ograniczenia", `Min. SOC: ${formatters.number2.format(inputs.minimumSocMWh)} MWh<br>Limit przylacza: ${formatters.number2.format(scenario.gridImportLimitMw)} MW`],
    ["Moc magazynu", `Ladowanie max: ${formatters.number2.format(inputs.maxChargePowerMw)} MW<br>Rozladowanie max: ${formatters.number2.format(inputs.maxDischargePowerMw)} MW`],
    ["Ceny", `RDN srednio: ${formatters.number2.format(inputs.averageRdnPricePlnPerMWh)} PLN/MWh<br>Zakup z dystrybucja: ${formatters.number2.format(inputs.averageEffectiveBuyPricePlnPerMWh)} PLN/MWh`],
    ["Sprzedaz", `Srednia cena oddania: ${formatters.number2.format(inputs.averageSellPricePlnPerMWh)} PLN/MWh<br>Wspolczynnik sprzedazy: ${formatters.number2.format(scenario.exportPriceFactor)}`],
    ["Zrodlo cen", escapeHtml(inputs.priceSource)],
    ["Historia", escapeHtml(inputs.historySource)]
  ];

  $("metaGrid").innerHTML = items.map(([title, value]) => `
    <div class="meta-item">
      <strong>${escapeHtml(title)}</strong>
      <span>${value}</span>
    </div>`).join("");
}

function renderList(id, items) {
  $(id).innerHTML = items.map(item => `<li>${escapeHtml(item)}</li>`).join("");
}

function renderHistory(history) {
  $("historyBody").innerHTML = history.map(day => `
    <tr>
      <td>${formatDate(day.date)}</td>
      <td>${escapeHtml(day.dayType)}</td>
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
    ].join(" ").trim();

    return `
      <tr class="${rowClasses}" data-hour-index="${index}" title="Kliknij, aby przejsc do tej godziny">
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
        <td title="${escapeHtml(hour.decisionReason)}">${escapeHtml(hour.action)}</td>
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
        ${state.isPlaying ? '<span class="badge playback">auto</span>' : '<span class="badge playback paused">manualnie</span>'}
      </div>
      <p class="live-decision-action">${escapeHtml(hour.action)}</p>
      <p class="live-reason">${escapeHtml(hour.decisionReason)}</p>
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
      <p class="live-card-label">${escapeHtml(label)}</p>
      <p class="live-card-value">${value}</p>
      <p class="live-card-note">${escapeHtml(note)}</p>
    </div>`).join("");

  $("timeline").innerHTML = state.response.hours.map((item, index) => `
    <button
      type="button"
      class="timeline-item ${item.cheapHour ? "timeline-item-cheap" : ""} ${item.expensiveHour ? "timeline-item-expensive" : ""} ${index === state.liveIndex ? "timeline-item-active" : ""}"
      data-hour-index="${index}"
      aria-pressed="${index === state.liveIndex}">
      <div class="timeline-hour">${item.label}</div>
      <div class="timeline-price">${formatters.number2.format(item.effectiveBuyPricePlnPerMWh)}</div>
    </button>`).join("");

  renderImportChart(state.response.hours, state.response.scenario.gridImportLimitMw, state.response.summary.peakGridImportMw);
  updateLiveRowHighlight();
}

function renderImportChart(hours, gridLimitMw, peakGridImportMw) {
  const peakLoadMw = Math.max(...hours.map(hour => hour.forecastLoadMw), 0.01);
  const chartMax = Math.max(gridLimitMw, peakGridImportMw, peakLoadMw, 0.01);

  $("gridImportChart").innerHTML = `
    <div class="import-chart-meta">
      <p class="summary-note">Aktywna godzina jest wyrozniona. Klikniecie slupka zatrzymuje auto-przewijanie i ustawia wybrana godzine.</p>
      <p class="summary-note">Limit przylacza: ${formatters.number2.format(gridLimitMw)} MW. Pik importu: ${formatters.number2.format(peakGridImportMw)} MW. Pik poboru: ${formatters.number2.format(peakLoadMw)} MW.</p>
    </div>
    <div class="import-chart-wrap">
      <div class="import-chart-grid">
        ${hours.map((hour, index) => {
          const importRatio = hour.gridImportMw / chartMax;
          const importHeight = hour.gridImportMw > 0 ? Math.max(6, importRatio * 100) : 0;
          const loadRatio = hour.forecastLoadMw / chartMax;
          const loadMarkerBottom = Math.max(0, Math.min(100, loadRatio * 100));
          const classes = [
            "import-bar",
            index === state.liveIndex ? "import-bar-active" : "",
            hour.gridLimitHit ? "import-bar-warning" : ""
          ].join(" ").trim();

          return `
            <button
              type="button"
              class="${classes}"
              data-hour-index="${index}"
              aria-pressed="${index === state.liveIndex}"
              title="${escapeHtml(`${hour.label}: import ${formatters.number3.format(hour.gridImportMw)} MW, pobor ${formatters.number3.format(hour.forecastLoadMw)} MW`)}">
              <span class="import-bar-value">${formatters.number2.format(hour.gridImportMw)}</span>
              <span class="import-bar-track">
                <span class="import-bar-load-marker" style="bottom: calc(${loadMarkerBottom}% - 1px)"></span>
                <span class="import-bar-fill" style="height: ${importHeight}%"></span>
              </span>
              <span class="import-bar-label">${escapeHtml(hour.label.slice(0, 2))}</span>
            </button>`;
        }).join("")}
      </div>
    </div>`;
}

function updateLiveRowHighlight() {
  document.querySelectorAll("#hoursBody tr").forEach(row => {
    row.classList.toggle("active-hour", Number(row.dataset.hourIndex) === state.liveIndex);
  });
}

function setLiveIndex(index) {
  if (!state.response || !state.response.hours.length) {
    return;
  }

  const count = state.response.hours.length;
  state.liveIndex = ((index % count) + count) % count;
  renderLiveView();
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
    advancePlayback();
  }, getPlaybackSpeed());
}

function advancePlayback() {
  if (!state.response || !state.response.hours.length) {
    return;
  }

  setLiveIndex(state.liveIndex + 1);

  if (state.isPlaying) {
    scheduleNextTick();
  }
}

function moveLiveHour(offset) {
  stopPlayback();
  setLiveIndex(state.liveIndex + offset);
}

function jumpToLiveHour(index) {
  stopPlayback();
  setLiveIndex(index);
}

function resetLiveHour() {
  clearPlaybackTimer();
  setLiveIndex(0);
  if (state.isPlaying) {
    scheduleNextTick();
  }
}

function getPlaybackSpeed() {
  return Number($("speedSelect").value || 1000);
}

function updatePlayPauseButton() {
  $("playPauseBtn").textContent = state.isPlaying ? "Wstrzymaj auto" : "Wznow auto";
}

function formatDate(value) {
  return formatters.date.format(new Date(`${value}T00:00:00`));
}

function scheduleSimulation() {
  clearTimeout(scheduleSimulation.timer);
  scheduleSimulation.timer = setTimeout(runSimulation, 250);
}

function bindHourSelector(containerId) {
  $(containerId).addEventListener("click", event => {
    const target = event.target.closest("[data-hour-index]");
    if (!target) {
      return;
    }

    jumpToLiveHour(Number(target.dataset.hourIndex));
  });
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
    resetLiveHour();
  });

  $("prevHourBtn").addEventListener("click", () => {
    moveLiveHour(-1);
  });

  $("stepBtn").addEventListener("click", () => {
    moveLiveHour(1);
  });

  $("speedSelect").addEventListener("change", () => {
    if (state.isPlaying) {
      scheduleNextTick();
    }
  });

  bindHourSelector("timeline");
  bindHourSelector("gridImportChart");
  bindHourSelector("hoursBody");
}

bindEvents();
loadDefaults();
