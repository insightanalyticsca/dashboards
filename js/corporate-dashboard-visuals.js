(function () {
  const PALETTE = ["#0808EE", "#09C698", "#171777", "#c9c9c9", "#d4d4d9", "#BBFF05", "#4F63F7", "#12DDB8", "#635BCB", "#DDF76D"];
  const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

  function ensureTheme() {
    if (typeof echarts === 'undefined' || !echarts.registerTheme) return;
    if (window.__dashCorporateThemeRegistered) return;
    echarts.registerTheme('vivid', {
      color: PALETTE,
      backgroundColor: 'transparent',
      textStyle: { fontFamily: 'Inter, Segoe UI, system-ui, -apple-system, sans-serif', color: '#171777' },
      title: { textStyle: { color: '#171777', fontWeight: 800 }, subtextStyle: { color: '#4F63F7' } },
      legend: { textStyle: { color: '#171777' }, pageTextStyle: { color: '#171777' } },
      tooltip: {
        backgroundColor: 'rgba(23,23,119,0.96)',
        borderColor: 'rgba(8,8,238,0.28)',
        borderWidth: 1,
        textStyle: { color: '#f4f7ff', fontSize: 12 },
        extraCssText: 'backdrop-filter:blur(10px);border-radius:12px;box-shadow:0 12px 34px rgba(23,23,119,0.28)'
      },
      axisPointer: { lineStyle: { color: 'rgba(8,8,238,0.35)', width: 1 } },
      categoryAxis: {
        axisLine: { lineStyle: { color: 'rgba(23,23,119,0.16)' } },
        axisTick: { lineStyle: { color: 'rgba(23,23,119,0.16)' } },
        axisLabel: { color: '#171777', fontSize: 11 },
        splitLine: { lineStyle: { color: 'rgba(23,23,119,0.07)' } },
        splitArea: { areaStyle: { color: ['rgba(8,8,238,0.02)', 'transparent'] } }
      },
      valueAxis: {
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: { color: '#171777', fontSize: 11 },
        splitLine: { lineStyle: { color: 'rgba(23,23,119,0.08)', type: 'dashed' } }
      },
      line: { lineStyle: { width: 2.5 }, symbolSize: 6, symbol: 'circle', smooth: true },
      bar: { barMaxWidth: 56, itemStyle: { borderRadius: [6, 6, 0, 0] } },
      pie: { itemStyle: { borderWidth: 2, borderColor: '#fff' } },
      scatter: { symbolSize: 8 },
      gauge: {
        axisLine: { lineStyle: { color: [[0.33, PALETTE[0]], [0.66, PALETTE[1]], [1, PALETTE[5]]], width: 12 } },
        pointer: { itemStyle: { color: PALETTE[0] } },
        detail: { color: '#171777', fontWeight: 900 },
        title: { color: '#4F63F7' }
      },
      radar: {
        axisLine: { lineStyle: { color: 'rgba(23,23,119,0.12)' } },
        splitLine: { lineStyle: { color: 'rgba(23,23,119,0.08)' } },
        splitArea: { areaStyle: { color: ['rgba(8,8,238,0.04)', 'transparent'] } }
      },
      sankey: { lineStyle: { opacity: 0.28, curveness: 0.5 } }
    });
    window.__dashCorporateThemeRegistered = true;
  }

  function htmlEncode(value) {
    return String(value ?? '')
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
  }

  function formatMs(ms) {
    const n = Number(ms || 0);
    if (!Number.isFinite(n)) return '0 ms';
    if (n < 1000) return `${Math.round(n)} ms`;
    if (n < 60000) return `${(n / 1000).toFixed(n >= 10000 ? 0 : 1)} s`;
    const m = Math.floor(n / 60000);
    const s = Math.floor((n % 60000) / 1000);
    return `${m}m ${s}s`;
  }

  function formatInt(n) {
    const v = Number(n || 0);
    return Number.isFinite(v) ? v.toLocaleString() : '0';
  }

  function hierarchyLabel(value) {
    const raw = String(value ?? '').trim();
    if (!raw) return '';
    if (/^\d{4}[-/]\d{2}([-/]\d{2})?$/.test(raw)) {
      const parts = raw.replace(/\//g, '-').split('-');
      if (parts.length >= 2) {
        const yr = parts[0];
        const m = Number(parts[1]);
        if (m >= 1 && m <= 12) {
          const day = parts[2];
          return day ? `${yr}\n${MONTHS[m - 1]} ${Number(day)}` : `${yr}\n${MONTHS[m - 1]}`;
        }
      }
    }
    const delim = raw.includes(' • ') ? ' • ' : (raw.includes('|') ? '|' : (raw.includes(' / ') ? ' / ' : null));
    if (!delim) return raw;
    return raw.split(delim).map(s => s.trim()).filter(Boolean).join('\n');
  }

  function templateValue(model, path) {
    const parts = String(path || '').split('.');
    let cur = model;
    for (const p of parts) {
      if (cur == null) return '';
      cur = cur[p];
    }
    return cur == null ? '' : cur;
  }

  function applyTemplate(template, model) {
    return String(template || '').replace(/{{\s*([\w.]+)\s*}}/g, function (_, path) {
      return htmlEncode(templateValue(model, path));
    });
  }

  function executeInlineScripts(host, context) {
    const scripts = Array.from(host.querySelectorAll('script'));
    for (const oldScript of scripts) {
      const script = document.createElement('script');
      for (const attr of oldScript.attributes) script.setAttribute(attr.name, attr.value);
      script.text = oldScript.textContent || '';
      const parent = oldScript.parentNode;
      window.DashVisualContext = context;
      parent.replaceChild(script, oldScript);
    }
  }

  function disposeCustomHtml(host) {
    if (!host) return;
    try { host.__customHtmlResizeObserver?.disconnect(); } catch (_) {}
    try { host.__customHtmlMutationObserver?.disconnect(); } catch (_) {}
    try { if (typeof host.__customHtmlWindowResize === 'function') window.removeEventListener('resize', host.__customHtmlWindowResize); } catch (_) {}
    host.__customHtmlResizeObserver = null;
    host.__customHtmlMutationObserver = null;
    host.__customHtmlWindowResize = null;
  }

  function resizeEmbeddedEcharts(scope) {
    if (!scope || typeof echarts === 'undefined') return;
    const chartNodes = scope.querySelectorAll('[_echarts_instance_]');
    chartNodes.forEach(function (el) {
      try {
        const inst = echarts.getInstanceByDom(el);
        if (inst) inst.resize();
      } catch (_) {}
    });
  }

  function measureAndFitCustomHtml(host, wrap, context) {
    if (!host || !wrap) return;
    wrap.style.height = 'auto';
    const contentHeight = Math.max(wrap.scrollHeight || 0, wrap.offsetHeight || 0, wrap.clientHeight || 0);
    if (contentHeight > 0) {
      wrap.style.height = contentHeight + 'px';
      host.style.minHeight = contentHeight + 'px';
    }
    resizeEmbeddedEcharts(wrap);
    try {
      if (context && typeof context.onResize === 'function') {
        context.onResize({ host: host, wrap: wrap, width: host.clientWidth, height: wrap.clientHeight || contentHeight || host.clientHeight });
      }
    } catch (_) {}
  }

  function renderCustomHtml(host, template, context) {
    if (!host) return;
    disposeCustomHtml(host);

    host.innerHTML = '';
    host.classList.add('custom-html-host');

    const wrap = document.createElement('div');
    wrap.className = 'custom-html-wrap';
    wrap.style.width = '100%';
    wrap.style.height = 'auto';
    wrap.style.minHeight = '100%';
    wrap.style.overflow = 'visible';
    host.appendChild(wrap);

    const safeContext = context || {};
    safeContext.host = host;
    safeContext.wrap = wrap;

    const html = applyTemplate(template, safeContext.model || {});
    wrap.innerHTML = html;
    executeInlineScripts(wrap, safeContext);

    let rafId = 0;
    const scheduleFit = function () {
      cancelAnimationFrame(rafId);
      rafId = requestAnimationFrame(function () {
        measureAndFitCustomHtml(host, wrap, safeContext);
      });
    };

    scheduleFit();
    setTimeout(scheduleFit, 50);
    

    if (typeof ResizeObserver !== 'undefined') {
      const ro = new ResizeObserver(scheduleFit);
      ro.observe(host);
      host.__customHtmlResizeObserver = ro;
    }

    if (typeof MutationObserver !== 'undefined') {
      const mo = new MutationObserver(scheduleFit);
      mo.observe(wrap, { childList: true, subtree: true, attributes: true, characterData: true });
      host.__customHtmlMutationObserver = mo;
    }

    host.__customHtmlWindowResize = scheduleFit;
    window.addEventListener('resize', scheduleFit, { passive: true });
  }

  function defaultCustomHtmlTemplate() {
    return [
      '<div data-dash-health-card style="width:100%;height:100%;min-height:320px"></div>',
      '<script>',
      '  (function(){',
      '    const host = document.currentScript.previousElementSibling;',
      '    const ctx = window.DashVisualContext || {};',
      '    const model = ctx.model || {};',
      '    if (window.DashHtmlVisuals && host) window.DashHtmlVisuals.renderExecHealthCard(host, model);',
      '  })();',
      '</script>'
    ].join('');
  }

  function ensureCardCss() {
    if (document.getElementById('dash-exec-health-card-css')) return;
    const style = document.createElement('style');
    style.id = 'dash-exec-health-card-css';
    style.textContent = `
    .custom-html-host{width:100%;height:auto;min-height:100%;overflow:visible;}
    .custom-html-wrap{width:100%;height:auto;min-height:100%;overflow:visible;}
    .custom-html-wrap [_echarts_instance_]{width:100% !important;}
    .dash-exec-health-card{position:relative;width:100%;min-height:320px;border-radius:28px;overflow:hidden;background:radial-gradient(1200px 500px at 100% 0%, rgba(79,99,247,.20), transparent 45%),radial-gradient(900px 420px at 0% 100%, rgba(18,221,184,.12), transparent 40%),linear-gradient(145deg,#f7f8ff 0%,#eff2ff 48%,#f4fff8 100%);box-shadow:0 18px 40px rgba(23,23,119,.14), inset 0 1px 0 rgba(255,255,255,.55);color:#171777;font-family:Inter,Segoe UI,Arial,sans-serif;}
    .dash-exec-health-card::before{content:"";position:absolute;inset:0;background:linear-gradient(120deg,transparent 0%, rgba(255,255,255,.35) 46%, transparent 56%);transform:translateX(-120%);animation:dashCardShimmer 8s linear infinite;pointer-events:none;}
    @keyframes dashCardShimmer{0%{transform:translateX(-120%)}100%{transform:translateX(120%)}}
    .dehc-shell{
      position:relative;
      z-index:1;
      padding:clamp(10px, 1.5vw, 18px);
      display:grid;
      grid-template-columns:minmax(220px, 30%) 1fr;
      gap:clamp(8px, 1.2vw, 16px);
    }
    .dehc-panel{border:1px solid rgba(23,23,119,.08);background:rgba(255,255,255,.52);backdrop-filter:blur(10px);border-radius:24px}
    .dehc-hero{position:relative;padding:18px 18px 14px;min-height:284px;overflow:hidden}
    .dehc-glow{position:absolute;inset:auto auto -80px -60px;width:220px;height:220px;border-radius:50%;filter:blur(28px);opacity:.28;pointer-events:none}
    .dehc-top{display:flex;align-items:center;justify-content:space-between;gap:10px;margin-bottom:6px}
    .dehc-title{font-size:13px;letter-spacing:.18em;text-transform:uppercase;color:#4F63F7;font-weight:800}
    .dehc-sub{margin-top:4px;font-size:12px;color:rgba(23,23,119,.68)}
    .dehc-pill{display:inline-flex;align-items:center;gap:8px;padding:8px 12px;border-radius:999px;font-size:12px;font-weight:800;border:1px solid rgba(23,23,119,.08);background:rgba(255,255,255,.72);white-space:nowrap}
    .dehc-dot{width:8px;height:8px;border-radius:50%;box-shadow:0 0 14px currentColor;animation:dehcPulse 1.8s infinite ease-in-out}
    @keyframes dehcPulse{0%,100%{transform:scale(1);opacity:1}50%{transform:scale(1.55);opacity:.55}}
    .dehc-gauge-wrap{
          position:relative;
          height:clamp(120px, 22vh, 172px);
          margin-top:8px;
        }
    .dehc-score-core{position:absolute;inset:50% auto auto 50%;transform:translate(-50%,-38%);text-align:center;pointer-events:none}
    .dehc-score{
      font-size:clamp(28px, 6vw, 54px);
      line-height:.9;
      font-weight:900;
      letter-spacing:-.04em;
    }
    .dehc-hero-footer{display:flex;align-items:stretch;gap:10px;margin-top:6px}.dehc-mini{flex:1;padding:10px 12px;border-radius:18px;background:rgba(255,255,255,.55);border:1px solid rgba(23,23,119,.06)}
    .dehc-mini-label{font-size:11px;text-transform:uppercase;letter-spacing:.12em;color:rgba(23,23,119,.52);font-weight:800}.dehc-mini-value{
  margin-top:6px;
  font-size:clamp(14px, 2.2vw, 22px);
  font-weight:900;
  line-height:1;
}.dehc-mini-sub{margin-top:6px;font-size:11px;color:rgba(23,23,119,.64)}
    .dehc-right{display:grid;grid-template-rows:auto 1fr;gap:14px}.dehc-summary{display:grid;grid-template-columns:repeat(4,minmax(120px,1fr));gap:12px}
    .dehc-kpi{padding:14px 14px 12px;border-radius:20px;border:1px solid rgba(23,23,119,.07);background:rgba(255,255,255,.48);position:relative;overflow:hidden}.dehc-kpi::after{content:"";position:absolute;inset:0 auto 0 0;width:3px;background:var(--accent,#0808EE);box-shadow:0 0 12px var(--accent,#0808EE)}
    .dehc-kpi-label{font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:rgba(23,23,119,.52);font-weight:800}.dehc-kpi-value{
  margin-top:10px;
  font-size:clamp(20px, 3vw, 31px);
  font-weight:900;
  line-height:.95;
}.dehc-kpi-sub{margin-top:7px;font-size:12px;color:rgba(23,23,119,.66)}
    .dehc-bottom{display:grid;grid-template-columns:1.15fr .85fr;gap:14px}.dehc-chart-panel,.dehc-detail-panel{padding:14px 14px 10px;min-height:148px}.dehc-panel-title{font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:rgba(23,23,119,.52);font-weight:800;margin-bottom:10px}
    .dehc-trend{width:100%;height:192px}.dehc-detail-grid{display:grid;grid-template-columns:1fr 1fr;gap:10px 14px}.dehc-detail{padding:10px 10px 8px;border-radius:16px;background:rgba(255,255,255,.42);border:1px solid rgba(23,23,119,.05)}
    .dehc-detail-label{font-size:11px;text-transform:uppercase;letter-spacing:.11em;color:rgba(23,23,119,.5);font-weight:800}.dehc-detail-value{margin-top:6px;font-size:17px;font-weight:800}.dehc-detail-sub{margin-top:5px;font-size:11px;color:rgba(23,23,119,.6)}
    .dehc-alert{animation:dehcAlertGlow 2s ease-in-out infinite}@keyframes dehcAlertGlow{0%,100%{box-shadow:0 0 0 rgba(255,90,122,0)}50%{box-shadow:0 0 28px rgba(255,90,122,.18)}}
    .dehc-topwait-value{
          font-size:16px;
          line-height:1.05;
          overflow-wrap:anywhere;
          word-break:break-word;
        }
    @media (max-width:1100px){.dehc-shell{grid-template-columns:1fr}.dehc-summary{grid-template-columns:repeat(2,minmax(140px,1fr))}.dehc-bottom{grid-template-columns:1fr}}
    @media (max-width:640px){.dehc-summary{grid-template-columns:1fr 1fr}.dehc-detail-grid{grid-template-columns:1fr}}
    `;
    document.head.appendChild(style);
  }

  function statusMeta(data) {
    const blockers = Number(data.blocker_sessions || 0);
    const blocked = Number(data.blocked_sessions || 0);
    const longRuns = Number(data.long_running_requests || 0);
    const waiting = Number(data.waiting_sessions || 0);
    const score = Number(data.health_score || 0);
    if (blockers > 0) return { label: data.health_status || 'Blocking', accent: '#ff5a7a', glow: 'rgba(255,90,122,.35)' };
    if (blocked > 0 || waiting > 0 || longRuns > 0 || score < 80) return { label: data.health_status || 'Degraded', accent: '#BBFF05', glow: 'rgba(187,255,5,.28)' };
    return { label: data.health_status || 'Healthy', accent: '#09C698', glow: 'rgba(9,198,152,.28)' };
  }

  function renderExecHealthCard(target, data) {
    ensureTheme();
    ensureCardCss();
    if (!target) return;
    try {
      if (typeof echarts !== 'undefined') {
        target.querySelectorAll('[_echarts_instance_]').forEach(function (el) {
          try { echarts.getInstanceByDom(el)?.dispose(); } catch (_) {}
        });
      }
    } catch (_) {}
    const meta = statusMeta(data || {});
      const history = Array.isArray(data?.score_history) && data.score_history.length ? data.score_history : [Number(data?.health_score || 0)];
      const labels = Array.isArray(data?.score_labels) && data.score_labels.length
          ? data.score_labels
          : history.map((_, i) => String(i + 1));
    const alertClass = Number(data?.blocker_sessions || 0) > 0 ? 'dehc-alert' : '';
    target.innerHTML = `
      <div class="dash-exec-health-card ${alertClass}" style="--accent:${meta.accent}">
        <div class="dehc-shell">
          <div class="dehc-panel dehc-hero">
            <div class="dehc-glow" style="background:${meta.glow}"></div>
            <div class="dehc-top">
              <div>
                <div class="dehc-title">Remote DB Health</div>
                <div class="dehc-sub">${htmlEncode(data?.remote_server || '')} · ${htmlEncode(data?.remote_database || '')}</div>
              </div>
              <div class="dehc-pill" style="color:${meta.accent}"><span class="dehc-dot"></span><span>${htmlEncode(meta.label)}</span></div>
            </div>
            <div class="dehc-gauge-wrap"><div class="dehc-gauge"></div><div class="dehc-score-core"><div class="dehc-score">${formatInt(data?.health_score || 0)}</div><div class="dehc-score-label">Health Score</div></div></div>
            <div class="dehc-hero-footer">
              <div class="dehc-mini"><div class="dehc-mini-label">Top wait</div><div class="dehc-mini-value dehc-topwait-value">${htmlEncode(data?.top_wait_type || '—')}</div><div class="dehc-mini-sub">${formatInt(data?.top_wait_count || 0)} session(s)</div></div>
              <div class="dehc-mini"><div class="dehc-mini-label">Snapshot</div><div class="dehc-mini-value" style="font-size:18px">${htmlEncode(data?.snapshot_time || '—')}</div><div class="dehc-mini-sub">${history.length > 1 ? ((history[history.length - 1] - history[history.length - 2]) >= 0 ? 'Stable / improving' : 'Trending down') : 'No trend yet'}</div></div>
            </div>
          </div>
          <div class="dehc-right">
            <div class="dehc-summary">
              <div class="dehc-kpi" style="--accent:#0808EE"><div class="dehc-kpi-label">Blockers</div><div class="dehc-kpi-value">${formatInt(data?.blocker_sessions || 0)}</div><div class="dehc-kpi-sub">Sessions freezing others</div></div>
              <div class="dehc-kpi" style="--accent:#BBFF05"><div class="dehc-kpi-label">Blocked</div><div class="dehc-kpi-value">${formatInt(data?.blocked_sessions || 0)}</div><div class="dehc-kpi-sub">Waiting behind locks</div></div>
              <div class="dehc-kpi" style="--accent:#4F63F7"><div class="dehc-kpi-label">Long running</div><div class="dehc-kpi-value">${formatInt(data?.long_running_requests || 0)}</div><div class="dehc-kpi-sub">Over threshold now</div></div>
              <div class="dehc-kpi" style="--accent:#12DDB8"><div class="dehc-kpi-label">Sessions</div><div class="dehc-kpi-value">${formatInt(data?.total_user_sessions || 0)}</div><div class="dehc-kpi-sub">${formatInt(data?.active_requests || 0)} active requests</div></div>
            </div>
            <div class="dehc-bottom">
              <div class="dehc-panel dehc-chart-panel"><div class="dehc-panel-title">Score trend</div><div class="dehc-trend"></div></div>
              <div class="dehc-panel dehc-detail-panel"><div class="dehc-panel-title">Operational detail</div><div class="dehc-detail-grid">
                <div class="dehc-detail"><div class="dehc-detail-label">Max wait</div><div class="dehc-detail-value">${formatMs(data?.max_wait_ms)}</div><div class="dehc-detail-sub">Peak wait right now</div></div>
                <div class="dehc-detail"><div class="dehc-detail-label">Avg wait</div><div class="dehc-detail-value">${formatMs(data?.avg_wait_ms)}</div><div class="dehc-detail-sub">Average across waiting sessions</div></div>
                <div class="dehc-detail"><div class="dehc-detail-label">Max elapsed</div><div class="dehc-detail-value">${formatMs(data?.max_elapsed_ms)}</div><div class="dehc-detail-sub">Longest running request</div></div>
                <div class="dehc-detail"><div class="dehc-detail-label">Open txns</div><div class="dehc-detail-value">${formatInt(data?.sessions_with_open_txn || 0)}</div><div class="dehc-detail-sub">Sessions holding transactions</div></div>
              </div></div>
            </div>
          </div>
        </div>
      </div>`;

    const gaugeHost = target.querySelector('.dehc-gauge');
    const trendHost = target.querySelector('.dehc-trend');
    if (typeof echarts === 'undefined' || !gaugeHost || !trendHost) return;

    const gauge = echarts.init(gaugeHost, 'vivid');
    gauge.setOption({
      animationDuration: 1200,
      series: [{
        type: 'gauge', radius: '98%', center: ['50%', '58%'], startAngle: 210, endAngle: -30, min: 0, max: 100,
        progress: { show: true, roundCap: true, width: 18, itemStyle: { color: meta.accent, shadowBlur: 16, shadowColor: meta.glow } },
        axisLine: { roundCap: true, lineStyle: { width: 18, color: [[1, 'rgba(23,23,119,.08)']] } },
        splitLine: { show: false }, axisTick: { show: false },
        axisLabel: { distance: -40, color: 'rgba(23,23,119,.42)', fontSize: 10, formatter: function (v) { return v === 0 || v === 50 || v === 100 ? v : ''; } },
        pointer: { show: false }, anchor: { show: false }, detail: { show: false }, data: [{ value: Number(data?.health_score || 0) }]
      }]
    });

      const trend = echarts.init(trendHost, 'vivid');
      trend.setOption({
          grid: { left: 52, right: 12, top: 10, bottom: 80 },
          xAxis: {
              type: 'category',
              boundaryGap: false,
              name: 'Snapshot time',
              nameLocation: 'middle',
              nameGap: 68,
              data: labels,
              axisLine: { show: true },
              axisTick: { show: true },
              axisLabel: {
                  show: true,
                  fontSize: 10,
                  rotate: 90
              }
          },
          yAxis: {
              type: 'value',
              name: 'Health score',
              nameLocation: 'middle',
              nameGap: 38,
              min: Math.max(0, Math.min.apply(null, history) - 10),
              max: Math.min(100, Math.max.apply(null, history) + 10),
              axisLine: { show: true },
              axisTick: { show: true },
              splitLine: { lineStyle: { color: 'rgba(23,23,119,.06)' } },
              axisLabel: { show: true, fontSize: 10 }
          },
          tooltip: {
              trigger: 'axis',
              backgroundColor: 'rgba(23,23,119,.96)',
              borderColor: 'rgba(8,8,238,.12)',
              textStyle: { color: '#fff' },
              formatter: p => `Health score: <b>${p[0].data}</b>`
          },
          series: [{
              type: 'line',
              smooth: true,
              symbol: 'none',
              data: history,
              lineStyle: { width: 3, color: meta.accent },
              areaStyle: {
                  color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
                      { offset: 0, color: meta.glow },
                      { offset: 1, color: 'rgba(255,255,255,0)' }
                  ])
              }
          }]
      });

    const resizeCharts = function () {
      try { gauge.resize(); } catch (_) {}
      try { trend.resize(); } catch (_) {}
    };

    if (window.DashVisualContext) {
      window.DashVisualContext.onResize = resizeCharts;
    }

    setTimeout(resizeCharts, 0);
    setTimeout(resizeCharts, 120);
  }

  window.DashHtmlVisuals = {
    palette: PALETTE,
    ensureTheme,
    hierarchyLabel,
    applyTemplate,
    renderCustomHtml,
    renderExecHealthCard,
    defaultCustomHtmlTemplate,
    formatMs,
    formatInt
  };

  ensureTheme();
})();
