/* ════════════════════════════════════════════════════════════════════════════
   visuals-renderer.js  —  shared renderer for all cloned .NET visuals
   - Loaded by each /visuals/<name>.html
   - Reads window.__VISUAL_PAYLOAD__ (set by the fetch in the page)
   - Renders: header, KPIs (if any), charts (ECharts), tables, notes
   - Uses the original .NET CSS classes (executive-dashboard-suite.css)
   ════════════════════════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  if (typeof echarts === 'undefined') {
    console.error('ECharts not loaded');
    return;
  }

  // ─── Helpers (ported from .NET executive-dashboard-suite.js) ──────────────
  function esc(v) {
    return String(v ?? '')
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }
  function num(v) {
    if (v === null || v === undefined || v === '') return null;
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }
  function formatValue(value, format) {
    const n = num(value);
    if (n === null) return '—';
    const type = String(format || 'number').toLowerCase();
    if (type === 'currency' || type === 'currency2') {
      return '$' + n.toLocaleString(undefined, { useGrouping: true, maximumFractionDigits: 0 });
    }
    if (type === 'number' || type === 'decimal' || type === 'decimal2') {
      return n.toLocaleString(undefined, { useGrouping: true, maximumFractionDigits: type === 'decimal2' ? 2 : (type === 'decimal' ? 1 : 0) });
    }
    if (type === 'percent') return n.toFixed(1) + '%';
    if (type === 'percent2') return n.toFixed(2) + '%';
    return n.toLocaleString();
  }
  function deltaText(value, mode) {
    const n = num(value);
    if (n === null) return '—';
    const sign = n > 0 ? '+' : '';
    return sign + n.toFixed(1) + (String(mode).toLowerCase() === 'points' ? ' pts' : '%');
  }
  function deltaTone(value, positiveIsGood) {
    const n = num(value);
    if (n === null || n === 0) return 'neutral';
    const good = positiveIsGood === false ? n < 0 : n > 0;
    return good ? 'good' : 'bad';
  }
  function palette() {
    const styles = getComputedStyle(document.documentElement);
    const values = [
      styles.getPropertyValue('--exec-primary').trim() || '#0808ee',
      styles.getPropertyValue('--exec-secondary').trim() || '#09c698',
      styles.getPropertyValue('--exec-accent').trim() || '#BBFF05',
      styles.getPropertyValue('--exec-highlight').trim() || '#38bdf8',
      '#4C6FFF', '#2DD4BF', '#FBBF24', '#845EF7'
    ];
    return [...new Set(values)];
  }

  // ─── KPI rendering ────────────────────────────────────────────────────────
  function renderKpis(kpis, host) {
    if (!Array.isArray(kpis) || !kpis.length) return;
    const wrap = document.createElement('div');
    wrap.className = 'exec-kpis';
    kpis.forEach((kpi, i) => {
      const tone = deltaTone(kpi.delta, kpi.positiveIsGood);
      const card = document.createElement('div');
      card.className = `exec-kpi exec-kpi-${i % 4}`;
      card.innerHTML = `
        <div class="exec-kpi-label">${esc(kpi.label)}</div>
        <div class="exec-kpi-value">${formatValue(kpi.value, kpi.format)}</div>
        <div class="exec-kpi-delta tone-${tone}">
          ${kpi.delta === null || kpi.delta === undefined ? '' : deltaText(kpi.delta, kpi.deltaMode)}
        </div>
      `;
      wrap.appendChild(card);
    });
    host.appendChild(wrap);
  }

  // ─── Table rendering ─────────────────────────────────────────────────────
  function renderTable(table, host) {
    const wrap = document.createElement('div');
    wrap.className = 'exec-visual exec-panel';
    const cols = table.columns || [];
    const formats = table.formats || [];
    const headerHtml = '<tr>' + cols.map(c => `<th>${esc(c)}</th>`).join('') + '</tr>';
    const bodyHtml = (table.rows || []).map(row =>
      '<tr>' + row.map((cell, i) => {
        const fmt = formats[i];
        return `<td>${fmt ? formatValue(cell, fmt) : esc(cell ?? '')}</td>`;
      }).join('') + '</tr>'
    ).join('');
    wrap.innerHTML = `
      <div class="exec-panel-title">${esc(table.title || '')}</div>
      <div class="exec-table-wrap">
        <table class="exec-table exec-table-default">
          <thead>${headerHtml}</thead>
          <tbody>${bodyHtml}</tbody>
        </table>
      </div>`;
    host.appendChild(wrap);
  }

  // ─── Chart rendering ──────────────────────────────────────────────────────
  function chartOption(chart) {
    const categories = Array.isArray(chart.categories) ? chart.categories : [];
    const series = Array.isArray(chart.series || chart.data && [] ) ? (chart.series || []) : [];
    const kind = String(chart.kind || 'line').toLowerCase();
    const colors = palette();

    if (kind === 'pie') {
      const first = series[0] || { data: [] };
      const data = categories.map((name, i) => ({ name, value: num(first.data?.[i]) ?? 0 }));
      return {
        color: colors,
        tooltip: { trigger: 'item', valueFormatter: v => formatValue(v, chart.valueFormat || 'number') },
        legend: { bottom: 2, type: 'scroll', textStyle: { fontSize: 10 } },
        series: [{ type: 'pie', radius: ['44%', '70%'], center: ['50%', '45%'], label: { fontSize: 10 }, data }]
      };
    }

    const hasSecondAxis = series.some(s => String(s.axis || '').toLowerCase() === 'right');
    const echartsSeries = series.map((item, i) => {
      const type = String(item.type || (kind === 'bar' ? 'bar' : 'line')).toLowerCase();
      const out = {
        name: item.name,
        type: type === 'stackedbar' ? 'bar' : type,
        data: Array.isArray(item.data) ? item.data : [],
        yAxisIndex: String(item.axis || '').toLowerCase() === 'right' ? 1 : 0,
        smooth: !!item.smooth,
        symbol: type === 'line' ? 'circle' : undefined,
        symbolSize: type === 'line' ? 6 : undefined,
        lineStyle: type === 'line' ? { width: 2.5, ...(item.lineStyle || {}) } : (item.lineStyle || {}),
        barMaxWidth: 32,
        itemStyle: { color: item.color || colors[i % colors.length] }
      };
      if (type === 'stackedbar' || item.stack) out.stack = item.stack || 'total';
      if (item.areaStyle) out.areaStyle = item.areaStyle;
      return out;
    });

    const axisFormatter = v => {
      const n = Number(v);
      if (!Number.isFinite(n)) return '';
      return n.toLocaleString(undefined, { useGrouping: true, maximumFractionDigits: 0 });
    };

    return {
      color: colors,
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { top: 2, type: 'scroll', textStyle: { fontSize: 10 } },
      grid: { left: 56, right: hasSecondAxis ? 56 : 22, top: 38, bottom: 42, containLabel: true },
      xAxis: {
        type: 'category',
        data: categories,
        axisLabel: { color: '#60708b', fontSize: 10, interval: 0, hideOverlap: true, rotate: categories.length > 8 ? 30 : 0 },
        axisTick: { show: false }
      },
      yAxis: [
        { type: 'value', name: chart.leftAxisTitle || '', axisLabel: { formatter: axisFormatter, fontSize: 10 }, splitLine: { lineStyle: { color: '#e9edf5' } } },
        ...(hasSecondAxis ? [{ type: 'value', name: chart.rightAxisTitle || '', axisLabel: { formatter: axisFormatter, fontSize: 10 }, splitLine: { show: false } }] : [])
      ],
      series: echartsSeries
    };
  }

  function renderChart(chart, host) {
    const wrap = document.createElement('div');
    wrap.className = 'exec-visual exec-panel';
    wrap.innerHTML = `
      <div class="exec-panel-title">${esc(chart.title || '')}</div>
      <div class="exec-chart" data-chart-id="${esc(chart.id)}" style="height:320px"></div>
    `;
    host.appendChild(wrap);
    const chartHost = wrap.querySelector('.exec-chart');
    const inst = echarts.init(chartHost);
    inst.setOption(chartOption(chart), true);
    // Track for resize
    chartInstances.push(inst);
  }

  // ─── Notes ───────────────────────────────────────────────────────────────
  function renderNotes(notes, host) {
    if (!Array.isArray(notes) || !notes.length) return;
    const wrap = document.createElement('div');
    wrap.className = 'exec-notes';
    notes.forEach(n => {
      const div = document.createElement('div');
      div.textContent = n;
      wrap.appendChild(div);
    });
    host.appendChild(wrap);
  }

  // ─── Chart instances for resize handling ──────────────────────────────────
  const chartInstances = [];

  // ─── Public API ────────────────────────────────────────────────────────────
  window.renderVisual = function (payload) {
    const app = document.getElementById('app');
    app.innerHTML = '';

    // Header
    const header = document.createElement('header');
    header.className = 'exec-header';
    header.innerHTML = `
      <div class="exec-title">${esc(payload.title || '')}</div>
      <div class="exec-asof">${esc(payload.asOfLabel || '')}</div>
    `;
    app.appendChild(header);

    // KPIs
    if (Array.isArray(payload.kpis) && payload.kpis.length) {
      renderKpis(payload.kpis, app);
    }

    // Canvas — charts and tables in a responsive grid
    const canvas = document.createElement('div');
    canvas.className = 'exec-canvas';
    canvas.style.minHeight = '480px';
    app.appendChild(canvas);

    // Charts
    (payload.charts || []).forEach(c => renderChart(c, canvas));

    // Tables
    (payload.tables || []).forEach(t => renderTable(t, canvas));

    // Notes
    renderNotes(payload.notes, app);

    // Resize handler
    setTimeout(() => {
      chartInstances.forEach(c => c.resize());
    }, 100);
  };

  window.addEventListener('resize', () => {
    chartInstances.forEach(c => c.resize());
  });

})();
