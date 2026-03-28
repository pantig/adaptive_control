const labels24 = Array.from({length: 24}, (_, i) => `${i+1}:00`);

// ---- Wykres cen TGE ----
const ctxCeny = document.getElementById('chartCeny').getContext('2d');
const chartCeny = new Chart(ctxCeny, {
  type: 'line',
  data: {
    labels: labels24,
    datasets: [
      {label: 'EE [zł/MWh]', data: [], borderColor: '#f0a500', backgroundColor: 'rgba(240,165,0,0.08)', tension: 0.4, fill: true},
      {label: 'Gaz [zł/MWh]', data: [], borderColor: '#3fb950', backgroundColor: 'rgba(63,185,80,0.08)', tension: 0.4, fill: true}
    ]
  },
  options: {
    responsive: true, maintainAspectRatio: false,
    plugins: {legend: {labels: {color: '#aaa'}}},
    scales: {
      x: {ticks: {color: '#666'}, grid: {color: '#21262d'}},
      y: {ticks: {color: '#666'}, grid: {color: '#21262d'}}
    }
  }
});

// ---- Wykres bilansu ----
const ctxBilans = document.getElementById('chartBilans').getContext('2d');
const chartBilans = new Chart(ctxBilans, {
  type: 'bar',
  data: {
    labels: labels24,
    datasets: [
      {label: 'PV [MW]', data: [], backgroundColor: '#f0a500'},
      {label: 'Gaz [MW]', data: [], backgroundColor: '#f85149'},
      {label: 'Sieć [MW]', data: [], backgroundColor: '#58a6ff'},
      {label: 'Ładowanie magazynu [MW]', data: [], backgroundColor: '#3fb950', stack: 'load'}
    ]
  },
  options: {
    responsive: true, maintainAspectRatio: false,
    plugins: {legend: {labels: {color: '#aaa'}}},
    scales: {
      x: {stacked: true, ticks: {color: '#666'}, grid: {color: '#21262d'}},
      y: {stacked: true, ticks: {color: '#666'}, grid: {color: '#21262d'}}
    }
  }
});

async function inicjalizuj() {
  const res = await fetch(`/api/ceny?godz=12`);
  const data = await res.json();
  chartCeny.data.datasets[0].data = data.WszystkieEE;
  chartCeny.data.datasets[1].data = data.WszystkieGaz;
  chartCeny.update();

  // Symulacja bilansu na 24h
  const pvProfil = [0,0,0,0,0,0.05,0.1,0.2,0.35,0.45,0.5,0.5,0.48,0.45,0.4,0.3,0.2,0.1,0.05,0,0,0,0,0];
  const zapotrzebowanie = [0.4,0.35,0.3,0.3,0.35,0.5,0.6,0.7,0.75,0.8,0.8,0.75,0.7,0.65,0.7,0.75,0.8,0.85,0.9,0.85,0.8,0.7,0.6,0.5];

  const pvData=[], gazData=[], siecData=[], ladData=[];
  for (let i = 0; i < 24; i++) {
    const pv = pvProfil[i];
    const demand = zapotrzebowanie[i];
    const ee = data.WszystkieEE[i];
    const gaz_ref = data.WszystkieGaz[i];
    const deficit = Math.max(0, demand - pv);
    const gas = ee > gaz_ref * 1.2 ? Math.min(0.5, deficit + 0.2) : deficit;
    const siec = Math.max(0, demand - pv - gas);
    const lad = Math.max(0, pv + gas - demand);
    pvData.push(pv.toFixed(2));
    gazData.push(Math.min(gas, 0.5).toFixed(2));
    siecData.push(siec.toFixed(2));
    ladData.push(lad.toFixed(2));
  }
  chartBilans.data.datasets[0].data = pvData;
  chartBilans.data.datasets[1].data = gazData;
  chartBilans.data.datasets[2].data = siecData;
  chartBilans.data.datasets[3].data = ladData;
  chartBilans.update();
}

async function update() {
  const p1 = +document.getElementById('p1').value;
  const p2 = +document.getElementById('p2').value;
  const p3 = +document.getElementById('p3').value;
  const p4 = +document.getElementById('p4').value;
  const godz = +document.getElementById('godz').value;

  document.getElementById('p1v').textContent = p1.toFixed(2);
  document.getElementById('p2v').textContent = p2.toFixed(2);
  document.getElementById('p3v').textContent = p3.toFixed(2);
  document.getElementById('p4v').textContent = p4;
  document.getElementById('gv').textContent = godz;

  const res = await fetch(`/api/algorytm?p1=${p1}&p2=${p2}&p3=${p3}&p4=${p4}&godzina=${godz}`);
  const d = await res.json();

  // Wskaźnik SOC
  const socPct = Math.round((1 - d.p_magazyn / 3) * 100);

  document.getElementById('wynik').innerHTML = `
    <div class="wynik-box ${d.tryb}">
      <strong>${d.zalecenie}</strong>
    </div>
    <div class="metric">
      <div class="metric-item"><div class="val">${d.p_gas.toFixed(2)} MW</div>Generator gazowy</div>
      <div class="metric-item"><div class="val">${d.p_pv.toFixed(2)} MW</div>PV</div>
      <div class="metric-item"><div class="val">${d.p_siec.toFixed(2)} MW</div>Pobór z sieci</div>
      <div class="metric-item"><div class="val">${d.koszt.toFixed(0)} zł/h</div>Koszt godzinowy</div>
      <div class="metric-item"><div class="val">${d.soc_po.toFixed(2)} MWh</div>SOC po godzinie</div>
      <div class="metric-item"><div class="val">${d.tryb}</div>Tryb pracy</div>
    </div>
  `;
}

document.querySelectorAll('input[type=range]').forEach(el => el.addEventListener('input', update));
inicjalizuj();
update();
