/* ════════════════════════════════════════════════════════════════════════════
   UNIVERSAL DASHBOARD CONSTRUCTOR v3 — neat & stunning
   ────────────────────────────────────────────────────────────────────────────
   v3 fixes:
   - Proper pie/donut rendering (was rendering as solid-color bars — fixed by
     keeping series.type='pie'/'donut', mapping data [{value, name}] shape,
     applying palette colors per segment, using radius for donut hole, legend
     on the right side instead of top)
   - Proper heatmap rendering (was rendering as multi-series bar — now flattens
     series into [[x, y, value]] data with visualMap color scale)
   - Smaller, denser fonts (was clamp(14px, 2.6cqw, 28px) for metric values —
     now clamp(11px, 1.85cqw, 19px) so tiles look like a Bloomberg terminal,
     not a marketing landing page)
   - Better color palette distribution across series (each series gets a
     different palette color, not all using colors[0])
   - chart-third type added (was missing — third-width charts were rendering
     at half width)
   ════════════════════════════════════════════════════════════════════════════ */
(function () {
    'use strict';

    const body = document.body;
    const suite = String(body.dataset.suite || '').trim().toLowerCase();
    const app = document.getElementById('app');
    const charts = new Map();
    const tileResizeObservers = new Map();
    const state = {
        payload: null,
        tileSpans: {},
        canvasEl: null,
        reclassifyScheduled: false
    };

    const SPAN_KEY = 'universal-layout-' + suite;
    function saveLayout() {
        try { sessionStorage.setItem(SPAN_KEY, JSON.stringify(state.tileSpans)); }
        catch (e) {}
    }
    function loadLayout() {
        try {
            const saved = sessionStorage.getItem(SPAN_KEY);
            if (saved) {
                const parsed = JSON.parse(saved);
                if (parsed && typeof parsed === 'object') state.tileSpans = parsed;
            }
        } catch (e) {}
    }
    loadLayout();

    // Clamp a number to [min, max] range. Returns min if value is not finite.
    // Used by layout geometry functions (x/y/w/h percentages).
    function clampNumber(value, min, max) {
        const n = Number(value);
        return Math.min(max, Math.max(min, Number.isFinite(n) ? n : min));
    }

    window.__universalResetLayout = function () {
        try { sessionStorage.removeItem(SPAN_KEY); } catch (e) {}
        state.tileSpans = {};
        if (state.canvasEl) {
            state.canvasEl.querySelectorAll('[data-tile-id]').forEach(tile => {
                // Clear all inline positioning so CSS Grid takes over
                tile.style.left = '';
                tile.style.top = '';
                tile.style.width = '';
                tile.style.height = '';
                tile.style.zIndex = '';
                tile.style.gridColumn = '';
                tile.style.gridRow = '';
                tile.removeAttribute('data-manual-span');
            });
            // Exit edit mode if active
            state.canvasEl.classList.add('is-frozen');
            state.canvasEl.classList.remove('is-editing');
            state.canvasEl.classList.remove('has-manual-layout');
            reclassifyLayout();
            requestAnimationFrame(resizeAllCharts);
        }
    };

    // ── Pastel palette with per-load hue rotation ──
    // On each page load, pick a random hue offset (0-360°) and shift all
    // palette colors by that offset. This gives each dashboard a slightly
    // different feel without being jarring. Saturation is kept low (~50%)
    // for a pastel, soft look.
    //
    // Implementation: convert each hex to HSL, shift the hue, desaturate,
    // convert back to hex.
    function hexToHsl(hex) {
        const r = parseInt(hex.slice(1, 3), 16) / 255;
        const g = parseInt(hex.slice(3, 5), 16) / 255;
        const b = parseInt(hex.slice(5, 7), 16) / 255;
        const max = Math.max(r, g, b), min = Math.min(r, g, b);
        let h, s, l = (max + min) / 2;
        if (max === min) { h = s = 0; }
        else {
            const d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            switch (max) {
                case r: h = ((g - b) / d + (g < b ? 6 : 0)); break;
                case g: h = ((b - r) / d + 2); break;
                case b: h = ((r - g) / d + 4); break;
            }
            h /= 6;
        }
        return [h * 360, s * 100, l * 100];
    }
    function hslToHex(h, s, l) {
        h = ((h % 360) + 360) % 360 / 360;
        s = Math.max(0, Math.min(100, s)) / 100;
        l = Math.max(0, Math.min(100, l)) / 100;
        let r, g, b;
        if (s === 0) { r = g = b = l; }
        else {
            const hue2rgb = (p, q, t) => {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1/6) return p + (q - p) * 6 * t;
                if (t < 1/2) return q;
                if (t < 2/3) return p + (q - p) * (2/3 - t) * 6;
                return p;
            };
            const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            const p = 2 * l - q;
            r = hue2rgb(p, q, h + 1/3);
            g = hue2rgb(p, q, h);
            b = hue2rgb(p, q, h - 1/3);
        }
        const toHex = x => Math.round(x * 255).toString(16).padStart(2, '0');
        return '#' + toHex(r) + toHex(g) + toHex(b);
    }

    // Pick a random hue offset on each load (0-360°)
    // Stored in a closure so all palette() calls in this session use the same offset
    const HUE_OFFSET = Math.floor(Math.random() * 360);
    // Pastel saturation target (~45-55%) and lightness boost
    const PASTEL_SAT = 48;
    const PASTEL_LIGHTNESS_BOOST = 8;

    function palette() {
        const baseColors = [
            '#6366f1',   // indigo
            '#06b6d4',   // cyan
            '#10b981',   // emerald
            '#f59e0b',   // amber
            '#ec4899',   // pink
            '#8b5cf6',   // violet
            '#14b8a6',   // teal
            '#f97316',   // orange
            '#3b82f6',   // blue
            '#ef4444',   // red
            '#84cc16',   // lime
            '#a855f7'    // purple
        ];
        // Shift hue + desaturate for pastel look
        return baseColors.map(hex => {
            const [h, s, l] = hexToHsl(hex);
            return hslToHex(
                h + HUE_OFFSET,
                PASTEL_SAT,
                Math.min(75, l + PASTEL_LIGHTNESS_BOOST)
            );
        });
    }

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
        const t = String(format || 'number').toLowerCase();
        if (t === 'currency' || t === 'currency2' || t === 'number' || t === 'decimal' || t === 'decimal2' || t === 'integer') {
            const decimals = t === 'integer' ? 0 : 2;
            return n.toLocaleString(undefined, { useGrouping: true, minimumFractionDigits: decimals, maximumFractionDigits: decimals });
        }
        if (t === 'percent' || t === 'percent2') {
            return n.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + '%';
        }
        return n.toLocaleString();
    }

    function deltaText(v, mode) {
        const n = num(v);
        if (n === null) return '—';
        const sign = n > 0 ? '+' : '';
        return sign + n.toFixed(2) + (String(mode).toLowerCase() === 'points' ? ' pts' : '%');
    }
    function deltaTone(v, positiveIsGood) {
        const n = num(v);
        if (n === null || n === 0) return 'neutral';
        const good = positiveIsGood === false ? n < 0 : n > 0;
        return good ? 'good' : 'bad';
    }

    // ── Tile builders ──────────────────────────────────────────────────────
    function buildMetricTile(metric, index) {
        const tile = document.createElement('div');
        tile.className = 'universal-tile';
        tile.dataset.naturalType = 'metric';
        tile.dataset.type = 'metric';
        tile.dataset.tileId = 'metric:' + (metric.key || index);
        const deltas = [];
        if (metric.mom !== undefined && metric.mom !== null) {
            deltas.push(`<span class="universal-delta" data-tone="${deltaTone(metric.mom, metric.positiveIsGood)}">${esc(metric.momLabel || 'MoM')} ${deltaText(metric.mom, metric.deltaMode)}</span>`);
        }
        if (metric.yoy !== undefined && metric.yoy !== null) {
            deltas.push(`<span class="universal-delta" data-tone="${deltaTone(metric.yoy, metric.positiveIsGood)}">${esc(metric.yoyLabel || 'YoY')} ${deltaText(metric.yoy, metric.deltaMode)}</span>`);
        }
        tile.innerHTML = `
            <div class="universal-metric-label">${esc(metric.label || '')}</div>
            <div class="universal-metric-main">
                <span class="universal-metric-value">${esc(formatValue(metric.value, metric.format))}</span>
                <span class="universal-metric-period">${esc(metric.period || '')}</span>
            </div>
            <div class="universal-deltas">${deltas.join('')}</div>
        `;
        return tile;
    }

    function buildChartTile(chart, index) {
        const tile = document.createElement('div');
        tile.className = 'universal-tile';
        const w = chart.width || 'half';
        const isWide = w === 'wide' || w === 'two-thirds';
        const isThird = w === 'third';
        tile.dataset.naturalType = isWide ? 'chart-wide' : (isThird ? 'chart-third' : 'chart-half');
        tile.dataset.type = tile.dataset.naturalType;
        tile.dataset.tileId = 'chart:' + (chart.id || index);
        tile.innerHTML = `
            <div class="universal-tile-title">${esc(chart.title || '')}</div>
            <div class="universal-chart" data-chart-id="${esc(chart.id || 'chart-' + index)}"></div>
        `;
        return tile;
    }

    function buildTableTile(table, index) {
        const tile = document.createElement('div');
        tile.className = 'universal-tile';
        const w = table.width || 'half';
        const isWide = w === 'wide' || w === 'two-thirds';
        tile.dataset.naturalType = isWide ? 'table-wide' : 'table-half';
        tile.dataset.type = tile.dataset.naturalType;
        tile.dataset.tileId = 'table:' + (table.id || index);
        const cols = table.columns || [];
        const rows = table.rows || [];
        const formats = table.formats || {};
        const thead = cols.map(c => `<th>${esc(c)}</th>`).join('');
        const tbody = rows.map(r => `<tr>${cols.map(c => {
            const fmt = formats[c] || 'text';
            const val = r[c];
            const display = fmt === 'currency' || fmt === 'percent' || fmt === 'number' ? formatValue(val, fmt) : esc(val);
            return `<td>${display}</td>`;
        }).join('')}</tr>`).join('');
        tile.innerHTML = `
            <div class="universal-tile-title">${esc(table.title || '')}</div>
            <div class="universal-table-wrap">
                <table class="universal-table">
                    <thead><tr>${thead}</tr></thead>
                    <tbody>${tbody}</tbody>
                </table>
            </div>
        `;
        return tile;
    }

    function buildAiBriefTile(payload) {
        if (!Array.isArray(payload.notes) || payload.notes.length === 0) return null;
        const tile = document.createElement('div');
        tile.className = 'universal-tile';
        tile.dataset.naturalType = 'ai-brief';
        tile.dataset.type = 'ai-brief';
        tile.dataset.tileId = 'ai-brief';
        const sectionMeta = [
            { key: 'what', icon: '✓', label: 'What happened' },
            { key: 'why',  icon: '?', label: 'Why' },
            { key: 'next', icon: '→', label: 'What to expect' },
            { key: 'do',   icon: '⊕', label: 'What to do' }
        ];
        const cells = sectionMeta.map((sec, i) => {
            const note = payload.notes[i] || '';
            const bodyText = note.replace(/^[A-Z ]+:/, '').trim();
            return `<div class="universal-ai-cell" data-section="${sec.key}">
                <div class="universal-ai-cell-head">
                    <span class="universal-ai-cell-icon">${sec.icon}</span>
                    <span class="universal-ai-cell-label">${esc(sec.label)}</span>
                </div>
                <div class="universal-ai-cell-body">${esc(bodyText)}</div>
            </div>`;
        }).join('');
        tile.innerHTML = `
            <div class="universal-ai-head">
                <span class="universal-ai-icon">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6m0 8v6m10-10h-6m-8 0H2m13.5-5.5l-2.5 2.5m-6 6l-2.5 2.5m11 0l-2.5-2.5m-6-6L4.5 4.5"/></svg>
                </span>
                <span class="universal-ai-title">AI Brief</span>
                <span class="universal-ai-sub">Live analysis</span>
                <span class="universal-ai-badge">
                    <span class="universal-ai-badge-dot"></span>
                    AI-wired
                </span>
            </div>
            <div class="universal-ai-grid">${cells}</div>
        `;
        return tile;
    }

    // ══ CHART OPTION BUILDER — handles all chart kinds properly ═════════════════
    function chartOption(chart) {
        const tileEl = state.canvasEl.querySelector(`[data-tile-id="chart:${chart.id}"]`);
        const tileW = tileEl ? tileEl.getBoundingClientRect().width : 360;
        const tileH = tileEl ? tileEl.getBoundingClientRect().height : 220;
        const labelFont = Math.max(8, Math.min(11, Math.round(tileW * 0.027)));
        const colors = palette();
        const isDark = (document.documentElement.getAttribute('data-theme') || 'light') === 'dark';
        const chartTextColor = isDark ? '#e2e8f0' : '#1e293b';
        const chartMutedColor = isDark ? '#94a3b8' : '#64748b';
        const chartAxisColor = isDark ? '#cbd5e1' : '#475569';
        const chartTooltipBg = isDark ? 'rgba(15,23,42,0.92)' : 'rgba(255,255,255,0.96)';
        const chartTooltipBorder = isDark ? 'rgba(6,182,212,0.35)' : 'rgba(99,102,241,0.20)';
        const chartTooltipCss = isDark
            ? 'backdrop-filter:blur(8px);-webkit-backdrop-filter:blur(8px);border-radius:10px;box-shadow:0 8px 24px -8px rgba(0,0,0,.5);'
            : 'backdrop-filter:blur(8px);-webkit-backdrop-filter:blur(8px);border-radius:10px;box-shadow:0 8px 24px -8px rgba(0,0,0,.2);';
        const chartAxisLine = isDark ? 'rgba(99,102,241,0.10)' : 'rgba(99,102,241,0.15)';
        const chartSplitLine = isDark ? 'rgba(148,163,184,0.10)' : 'rgba(99,102,241,0.08)';
        const kind = String(chart.kind || '').toLowerCase();
        const isPie = kind === 'pie' || kind === 'donut';
        const isDonut = kind === 'donut';
        const isHeatmap = kind === 'heatmap';

        // ── Shared tooltip config (glassmorphism, matches markets-finance) ──
        const glassTooltip = {
            backgroundColor: chartTooltipBg,
            borderColor: chartTooltipBorder,
            borderWidth: 1,
            padding: [8, 12],
            textStyle: { color: chartTextColor, fontSize: labelFont, fontWeight: 600, fontFamily: 'Inter, sans-serif' },
            extraCssText: chartTooltipCss
        };

        // Helper: create a vertical gradient fill for a given hex color
        function gradientFill(hex) {
            // Convert hex to rgba with alpha
            const r = parseInt(hex.slice(1, 3), 16);
            const g = parseInt(hex.slice(3, 5), 16);
            const b = parseInt(hex.slice(5, 7), 16);
            return {
                type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
                colorStops: [
                    { offset: 0, color: `rgba(${r},${g},${b},0.85)` },
                    { offset: 1, color: `rgba(${r},${g},${b},0.25)` }
                ]
            };
        }

        // ── PIE / DONUT (with shadow glow + rounded segments) ──
        if (isPie) {
            const s = chart.series[0] || { data: [] };
            const categories = chart.categories || [];
            const pieData = (s.data || []).map((v, i) => {
                const c = colors[i % colors.length];
                return {
                    value: v,
                    name: categories[i] || `Segment ${i + 1}`,
                    itemStyle: {
                        color: gradientFill(c),
                        borderColor: isDark ? 'rgba(15,23,42,0.8)' : '#fff',
                        borderWidth: 2,
                        borderRadius: 4,
                        shadowBlur: 12,
                        shadowColor: `rgba(${parseInt(c.slice(1,3),16)},${parseInt(c.slice(3,5),16)},${parseInt(c.slice(5,7),16)},0.4)`
                    }
                };
            });
            const legendRight = tileW > 320;
            return {
                animationDuration: 600,
                animationEasing: 'cubicOut',
                color: colors,
                tooltip: Object.assign({}, glassTooltip, {
                    trigger: 'item',
                    formatter: '{b}: <b style="color:#06b6d4">{c}</b> ({d}%)'
                }),
                legend: {
                    type: 'scroll',
                    orient: legendRight ? 'vertical' : 'horizontal',
                    right: legendRight ? 8 : 'center',
                    top: legendRight ? 'middle' : 0,
                    bottom: legendRight ? 'middle' : 0,
                    left: legendRight ? '55%' : 'center',
                    textStyle: { fontSize: labelFont, color: chartMutedColor, fontWeight: 600 },
                    itemWidth: 8, itemHeight: 8, itemGap: 6
                },
                series: [{
                    name: s.name || chart.title,
                    type: 'pie',
                    radius: isDonut ? ['38%', '62%'] : '62%',
                    center: legendRight ? ['28%', '50%'] : ['50%', '55%'],
                    avoidLabelOverlap: true,
                    label: {
                        show: !legendRight,
                        position: 'outer',
                        formatter: '{b}\n{d}%',
                        fontSize: labelFont - 1,
                        color: chartTextColor,
                        fontWeight: 600
                    },
                    emphasis: {
                        label: { show: true, fontSize: labelFont + 2, fontWeight: 800 },
                        itemStyle: {
                            shadowBlur: 24,
                            shadowColor: 'rgba(6,182,212,0.6)',
                            borderColor: 'rgba(6,182,212,0.9)',
                            borderWidth: 2
                        },
                        scaleSize: 8
                    },
                    data: pieData
                }]
            };
        }

        // ── HEATMAP (markets-finance style: multi-color diverging, translucent, glow) ──
        if (isHeatmap) {
            const categoriesX = chart.categories || [];
            const categoriesY = (chart.series || []).map(s => s.name);
            const data = [];
            let min = Infinity, max = -Infinity;
            (chart.series || []).forEach((s, y) => {
                (s.data || []).forEach((v, x) => {
                    if (v !== 0 && v !== null && v !== undefined) {
                        data.push([x, y, v]);
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                });
            });
            if (!isFinite(min)) { min = 0; max = 100; }
            // Pastel heatmap color scales — soft, not too vivid
            // If data crosses zero: pastel diverging (rose→amber→slate→mint→emerald)
            // If all positive: warm pastel (cream→peach→rose→lavender)
            // If all negative: reversed diverging
            const isDiverging = min < 0 && max > 0;
            const heatColors = isDiverging
                ? ['#fda4af', '#fdba74', '#fde68a', '#e2e8f0', '#a7f3d0', '#6ee7b7', '#5eead4']
                : min >= 0
                    ? ['#fef3c7', '#fde68a', '#fcd34d', '#fdba74', '#fca5a5', '#c4b5fd', '#a5b4fc']
                    : ['#5eead4', '#6ee7b7', '#a7f3d0', '#e2e8f0', '#fde68a', '#fdba74', '#fda4af'];
            return {
                animationDuration: 600,
                backgroundColor: 'transparent',
                tooltip: Object.assign({}, glassTooltip, {
                    formatter: params => `<b style="color:#fff">${categoriesY[params.value[1]]}</b> <span style="color:${chartMutedColor}">${categoriesX[params.value[0]]}</span><br><b style="color:#06b6d4">${params.value[2]}</b>`
                }),
                grid: { left: 70, right: 14, top: 8, bottom: 36, containLabel: false },
                xAxis: {
                    type: 'category',
                    data: categoriesX,
                    splitArea: { show: false },
                    axisLine: { show: false },
                    axisTick: { show: false },
                    axisLabel: { color: chartAxisColor, fontSize: labelFont, fontWeight: 600, interval: 0, hideOverlap: true, margin: 10 }
                },
                yAxis: {
                    type: 'category',
                    data: categoriesY,
                    splitArea: { show: false },
                    axisLine: { show: false },
                    axisTick: { show: false },
                    axisLabel: { color: chartAxisColor, fontSize: labelFont, fontWeight: 500, margin: 12 }
                },
                visualMap: {
                    min, max,
                    calculable: false,
                    show: false,
                    inRange: { color: heatColors },
                    outOfRange: { color: isDark ? 'rgba(15,23,42,0.92)' : 'rgba(226,232,240,0.5)' }
                },
                series: [{
                    type: 'heatmap',
                    data: data,
                    label: {
                        show: true,
                        fontSize: labelFont,
                        fontWeight: 700,
                        // Dark text reads well on pastel cells (light backgrounds)
                        color: '#1e293b',
                        textShadowColor: 'rgba(255,255,255,0.7)',
                        textShadowBlur: 2,
                        formatter: function(p) {
                            const v = p.value[2];
                            if (v === null || v === undefined) return '';
                            // Format large numbers compactly
                            if (Math.abs(v) >= 1e6) return (v/1e6).toFixed(1) + 'M';
                            if (Math.abs(v) >= 1e3) return (v/1e3).toFixed(1) + 'K';
                            if (Number.isInteger(v)) return String(v);
                            return v.toFixed(1);
                        }
                    },
                    itemStyle: {
                        borderRadius: 6,
                        borderColor: 'transparent',
                        borderWidth: 0,
                        shadowBlur: 8,
                        shadowColor: 'rgba(0,0,0,0.32)',
                        opacity: 0.82
                    },
                    emphasis: {
                        itemStyle: {
                            shadowBlur: 20,
                            shadowColor: 'rgba(6,182,212,0.75)',
                            borderColor: 'rgba(6,182,212,0.9)',
                            borderWidth: 2,
                            opacity: 1
                        },
                        label: { fontSize: labelFont + 1, fontWeight: 800 }
                    }
                }]
            };
        }

        // ── BAR / LINE / COMBO / STACKEDBAR (with gradient fills + shadow + glow) ──
        const categories = chart.categories || [];
        const hasSecondAxis = (chart.series || []).some(s => s.axis === 'right');

        function axisFormatter(v) {
            const t = String(chart.valueFormat || 'number').toLowerCase();
            if (t === 'currency') return '$' + (v >= 1e6 ? (v/1e6).toFixed(1) + 'M' : v >= 1e3 ? (v/1e3).toFixed(0) + 'K' : v.toFixed(0));
            if (t === 'percent') return v.toFixed(0) + '%';
            if (v >= 1e6) return (v/1e6).toFixed(1) + 'M';
            if (v >= 1e3) return (v/1e3).toFixed(0) + 'K';
            return v.toFixed(0);
        }

        const series = (chart.series || []).map((s, idx) => {
            const color = s.color || colors[idx % colors.length];
            const type = s.type === 'line' ? 'line' : 'bar';
            const r = parseInt(color.slice(1, 3), 16);
            const g = parseInt(color.slice(3, 5), 16);
            const b = parseInt(color.slice(5, 7), 16);

            if (type === 'line') {
                return {
                    name: s.name,
                    type: 'line',
                    yAxisIndex: s.axis === 'right' ? 1 : 0,
                    data: s.data,
                    smooth: !!s.smooth,
                    symbol: 'circle',
                    symbolSize: 6,
                    lineStyle: {
                        color: color,
                        width: 2.5,
                        shadowBlur: 8,
                        shadowColor: `rgba(${r},${g},${b},0.5)`
                    },
                    itemStyle: { color: color, borderColor: '#fff', borderWidth: 1.5 },
                    areaStyle: {
                        color: {
                            type: 'linear', x: 0, y: 0, x2: 0, y2: 1,
                            colorStops: [
                                { offset: 0, color: `rgba(${r},${g},${b},0.20)` },
                                { offset: 1, color: `rgba(${r},${g},${b},0)` }
                            ]
                        }
                    },
                    stack: s.stack || undefined
                };
            } else {
                // Bar series — gradient fill + rounded top corners + shadow
                return {
                    name: s.name,
                    type: 'bar',
                    yAxisIndex: s.axis === 'right' ? 1 : 0,
                    data: s.data,
                    itemStyle: {
                        color: gradientFill(color),
                        borderRadius: [4, 4, 0, 0],
                        shadowBlur: 6,
                        shadowColor: `rgba(${r},${g},${b},0.3)`
                    },
                    emphasis: {
                        itemStyle: {
                            color: color,
                            shadowBlur: 16,
                            shadowColor: `rgba(${r},${g},${b},0.6)`
                        }
                    },
                    stack: s.stack || (kind === 'stackedbar' ? 'total' : undefined),
                    barCategoryGap: '40%'
                };
            }
        });

        return {
            animationDuration: 600,
            animationEasing: 'cubicOut',
            color: colors,
            tooltip: Object.assign({}, glassTooltip, {
                trigger: 'axis',
                axisPointer: { type: 'shadow', shadowStyle: { color: isDark ? 'rgba(99,102,241,0.08)' : 'rgba(99,102,241,0.05)' } }
            }),
            legend: {
                top: 2, type: 'scroll',
                textStyle: { fontSize: Math.max(7, labelFont - 1), color: chartMutedColor, fontWeight: 600 },
                itemWidth: 8, itemHeight: 8, itemGap: 6
            },
            grid: { left: 44, right: hasSecondAxis ? 44 : 14, top: 22, bottom: 28, containLabel: false },
            xAxis: {
                type: 'category',
                data: categories,
                axisLabel: { color: chartAxisColor, fontSize: labelFont, fontWeight: 600, interval: 0, hideOverlap: true, rotate: categories.length > 6 ? 25 : 0 },
                axisLine: { lineStyle: { color: chartAxisLine } },
                axisTick: { show: false },
                splitLine: { show: false }
            },
            yAxis: [
                { type: 'value', name: chart.leftAxisTitle || '', nameTextStyle: { fontSize: Math.max(7, labelFont - 1), color: chartMutedColor }, axisLabel: { formatter: axisFormatter, fontSize: labelFont, color: chartMutedColor, fontWeight: 500 }, splitLine: { lineStyle: { color: chartSplitLine, type: 'dashed' } }, axisLine: { show: false }, axisTick: { show: false } },
                ...(hasSecondAxis ? [{ type: 'value', name: chart.rightAxisTitle || '', nameTextStyle: { fontSize: Math.max(7, labelFont - 1), color: chartMutedColor }, axisLabel: { formatter: axisFormatter, fontSize: labelFont, color: chartMutedColor, fontWeight: 500 }, splitLine: { show: false }, axisLine: { show: false }, axisTick: { show: false } }] : [])
            ],
            series
        };
    }

    function resizeChart(tileId) {
        const chart = charts.get(tileId);
        if (!chart) return;
        const tileEl = state.canvasEl.querySelector(`[data-tile-id="${CSS.escape(tileId)}"]`);
        if (!tileEl) return;
        const chartModel = state.payload.charts.find(c => 'chart:' + c.id === tileId);
        if (!chartModel) return;
        chart.setOption(chartOption(chartModel), { lazyUpdate: true });
        chart.resize();
    }
    function resizeAllCharts() {
        charts.forEach((_, id) => resizeChart(id));
    }

    function applySavedSpans(tile) {
        const id = tile.dataset.tileId;
        const span = state.tileSpans[id];
        if (!span) return;
        if (span.colSpan) {
            tile.style.gridColumn = `span ${span.colSpan}`;
            tile.setAttribute('data-manual-span', '1');
        }
        if (span.rowSpan) {
            tile.style.gridRow = `span ${span.rowSpan}`;
            tile.setAttribute('data-manual-span', '1');
        }
    }

    // ══ THE THOROUGH CLASSIFIER ═══════════════════════════════════════════════
    // Strategy: walk tiles in DOM order. For each tile, compute its span
    // (depends on natural-type). Pack into rows of `colCount` cols.
    // When a tile doesn't fit (would leave a hole), promote the PREVIOUS
    // stretchable tile ONE step up (third→half, half→wide) so its larger
    // span fills more of the row. If promotion still doesn't fit, the
    // current tile starts a new row.
    //
    // Key insight for thirds: a chart-third at 4 cols takes 1 col. If 4 thirds
    // appear in a row, they fill the row exactly (4×1=4). If only 3 appear,
    // they take 3 cols and 1 col is empty — promote the LAST one to half (span 2)
    // so the row becomes 2+1+1=4 (rearranged). But CSS Grid reorders by DOM
    // position, so we need to actually have 2+2=4 (two halves) for clean fit.
    //
    // Simplification: count CONSECUTIVE stretchable tiles. If their total span
    // would exceed the row, promote the LAST one to fill the gap.
    // ══ THE THOROUGH CLASSIFIER — per-row cap rules ═══════════════════════════
    // Caps:
    //   - max 4 metrics per row (1+1+1+1 = 4 cols)
    //   - max 2 graphs per row (2+2 = 4 cols for halves, OR 1+1+2 = 4 for thirds+half)
    //   - chart-wide / table-wide / ai-brief always take a full row
    //
    // Algorithm:
    //   1. Walk tiles in DOM order, simulate placement tracking currentX
    //   2. If a tile would overflow current row (currentX + span > colCount),
    //      promote the previous stretchable tile to FILL the remaining cols.
    //      Promotion chain: third → half → wide. We promote ONE STEP at a time
    //      until the gap is filled. So 1 col gap → promote third to half (span 2)
    //      = exactly fills. 2 col gap with a half already placed → no promotion
    //      needed, just start new row.
    //   3. If the next tile is a wide (always full row), promote the previous
    //      stretchable all the way to wide so its row gets a single full-width
    //      tile instead of leaving a half-empty hole.
    //   4. At end-of-loop, if final row is partial, promote previous stretchable
    //      to fill the gap (one step at a time).
    function reclassifyLayout() {
        const canvas = state.canvasEl;
        if (!canvas) return;
        // Skip when in edit mode OR has manual layout overrides — tiles are
        // absolutely positioned, grid classification would interfere
        if (canvas.classList.contains('is-editing')) return;
        if (canvas.classList.contains('has-manual-layout')) return;

        const gridTemplateCols = getComputedStyle(canvas).gridTemplateColumns;
        const colCount = gridTemplateCols.split(' ').filter(s => s.trim()).length;
        if (colCount < 1) return;

        const tiles = Array.from(canvas.querySelectorAll('.universal-tile'));
        tiles.forEach(tile => {
            if (tile.getAttribute('data-manual-span') === '1') return;
            const natural = tile.dataset.naturalType;
            tile.dataset.type = natural;
            tile.style.gridColumn = '';
        });

        let currentX = 0;
        let lastStretchableIndex = -1;
        // Track type counts per row for cap enforcement
        let rowMetrics = 0;
        let rowGraphs = 0;

        function spanOf(tile) {
            const t = tile.dataset.type;
            if (t === 'metric' || t === 'chart-third') return 1;
            if (t === 'chart-half' || t === 'table-half') return 2;
            return colCount; // wide / table-wide / ai-brief
        }
        function isStretchable(natural) {
            return natural === 'chart-third' || natural === 'chart-half' || natural === 'table-half';
        }
        function isGraph(natural) {
            return natural === 'chart-third' || natural === 'chart-half' || natural === 'chart-wide';
        }
        function promote(tile) {
            const natural = tile.dataset.naturalType;
            const cur = tile.dataset.type;
            if (natural === 'chart-third') {
                if (cur === 'chart-third') tile.dataset.type = 'chart-half';
                else if (cur === 'chart-half') tile.dataset.type = 'chart-wide';
            } else if (natural === 'chart-half') {
                tile.dataset.type = 'chart-wide';
            } else if (natural === 'table-half') {
                tile.dataset.type = 'table-wide';
            }
        }
        function promoteToWide(tile) {
            let safety = 5;
            while (tile.dataset.type !== 'chart-wide' && tile.dataset.type !== 'table-wide' && safety-- > 0) {
                promote(tile);
            }
        }
        function fillGapWithPromotion(prevTile, gap) {
            // Promote prevTile just enough to fill `gap` cols.
            // third (span 1) → half (span 2) fills 1-col gap if remaining was 1
            // half (span 2) → wide (span 4) fills 2-col gap if remaining was 2
            let prevSpan = spanOf(prevTile);
            while (prevSpan < gap + prevSpan && prevTile.dataset.type !== 'chart-wide' && prevTile.dataset.type !== 'table-wide') {
                const before = prevTile.dataset.type;
                promote(prevTile);
                if (prevTile.dataset.type === before) break;
                prevSpan = spanOf(prevTile);
                // Stop if we've filled the gap
                if (spanOf(prevTile) >= gap + currentX) break;
            }
        }

        for (let i = 0; i < tiles.length; i++) {
            const tile = tiles[i];
            if (tile.getAttribute('data-manual-span') === '1') continue;
            const natural = tile.dataset.naturalType;
            const span = spanOf(tile);

            // Wide tiles always take a full row. If we have a partial row
            // with a stretchable tile, promote it to wide (fill the row).
            if (span === colCount) {
                if (currentX > 0 && currentX < colCount && lastStretchableIndex >= 0) {
                    promoteToWide(tiles[lastStretchableIndex]);
                }
                currentX = 0;
                lastStretchableIndex = -1;
                rowMetrics = 0;
                rowGraphs = 0;
                continue;
            }

            // Per-row cap enforcement:
            // - max 4 metrics per row (always satisfied since 4 cols × 1 = 4)
            // - max 2 graphs per row (a third counts as graph; 3 thirds in row OK,
            //   but 4th should promote last to half so row = 1+1+2=4)
            const wouldExceedGraphCap = isGraph(natural) && rowGraphs >= 2 && natural !== 'chart-third';
            // For thirds, allow up to 3 (3×1=3 cols), but the 4th third must wrap
            const wouldExceedThirdCap = natural === 'chart-third' && rowGraphs >= 3;

            if (wouldExceedGraphCap || wouldExceedThirdCap || currentX + span > colCount) {
                // Need to wrap to a new row. Promote the previous stretchable
                // tile to fill the gap if possible.
                if (lastStretchableIndex >= 0 && currentX < colCount) {
                    const prev = tiles[lastStretchableIndex];
                    const remaining = colCount - currentX;
                    // Promote until prev fills the remaining cols (or until wide)
                    let prevSpan = spanOf(prev);
                    let safety = 5;
                    while (prevSpan < remaining + (spanOf(prev) - prevSpan) && prev.dataset.type !== 'chart-wide' && prev.dataset.type !== 'table-wide' && safety-- > 0) {
                        const before = prev.dataset.type;
                        promote(prev);
                        if (prev.dataset.type === before) break;
                        prevSpan = spanOf(prev);
                        if (prevSpan >= remaining) break;
                    }
                    // If still gap, promote all the way to wide
                    if (spanOf(prev) < colCount) promoteToWide(prev);
                }
                currentX = 0;
                lastStretchableIndex = -1;
                rowMetrics = 0;
                rowGraphs = 0;
                // Re-check this tile fits now (its own span hasn't changed)
                if (span > colCount) continue;
            }

            if (isStretchable(natural)) lastStretchableIndex = i;
            else lastStretchableIndex = -1;
            if (natural === 'metric') rowMetrics++;
            if (isGraph(natural)) rowGraphs++;

            currentX += span;
            if (currentX >= colCount) {
                currentX = 0;
                lastStretchableIndex = -1;
                rowMetrics = 0;
                rowGraphs = 0;
            }
        }

        // Final row check — promote all the way to wide so no half-empty hole
        if (currentX > 0 && currentX < colCount && lastStretchableIndex >= 0) {
            promoteToWide(tiles[lastStretchableIndex]);
        }

        requestAnimationFrame(resizeAllCharts);
    }

    // ══ Layout handles — mirrors old dash-suite.js UX ═══════════════════════════
    // Two handles per tile:
    //   ⠿ (top-right)  → drag to move freely (changes x%, y%)
    //   corner (bottom-right) → drag to resize (changes w%, h% simultaneously)
    // Double-click ⠿ → reset tile to default position
    //
    // Edit mode UX:
    //   - When canvas enters edit mode, JS converts each tile's current grid
    //     placement to absolute % coords (x, y, w, h) and applies them inline.
    //   - Canvas switches from `display: grid` to `position: relative` via CSS.
    //   - This allows free mix-and-match: tiles can overlap, be moved anywhere,
    //     resized to any dimensions.
    //   - On exit: tiles keep their manual positions (saved to localStorage).
    //     Reset button clears all overrides.
    function getTileGeometry(tileId) {
        // Read from saved overrides, or return null (use grid placement)
        const override = state.tileSpans[tileId];
        if (override && typeof override.x === 'number') {
            return {
                x: clampNumber(override.x, 0, 100),
                y: clampNumber(override.y, 0, 100),
                w: clampNumber(override.w, 4, 100),
                h: clampNumber(override.h, 4, 100),
                z: Number(override.z || 0)
            };
        }
        return null;
    }

    function applyTileGeometry(tile, geometry) {
        if (!tile || !geometry) return;
        tile.style.left = geometry.x + '%';
        tile.style.top = geometry.y + '%';
        tile.style.width = geometry.w + '%';
        tile.style.height = geometry.h + '%';
        tile.style.zIndex = String(geometry.z || 0);
    }

    function captureTilePositionAsPercent(tile) {
        // Convert current pixel position to % of canvas
        const canvasRect = state.canvasEl.getBoundingClientRect();
        const tileRect = tile.getBoundingClientRect();
        if (!canvasRect.width || !canvasRect.height) return null;
        return {
            x: clampNumber(((tileRect.left - canvasRect.left) / canvasRect.width) * 100, 0, 100),
            y: clampNumber(((tileRect.top - canvasRect.top) / canvasRect.height) * 100, 0, 100),
            w: clampNumber((tileRect.width / canvasRect.width) * 100, 4, 100),
            h: clampNumber((tileRect.height / canvasRect.height) * 100, 4, 100),
            z: 0
        };
    }

    function hasManualOverrides() {
        // Check if any tile has geometry overrides (x/y/w/h, not colSpan/rowSpan)
        return Object.values(state.tileSpans).some(span =>
            span && typeof span.x === 'number' && typeof span.h === 'number'
        );
    }

    function computeCanvasHeightForOverrides() {
        // Compute canvas height needed to fit all manually-positioned tiles
        // Each tile's bottom = (y + h) % of canvas. We need canvas tall enough
        // that the tile with the largest bottom % has room.
        // Since heights are stored as %, we just need a canvas tall enough
        // that the absolute heights look right. We pick a comfortable default
        // and grow it if any tile's bottom % exceeds it.
        const minHeight = 1200;
        let maxBottomPct = 100;
        Object.values(state.tileSpans).forEach(span => {
            if (span && typeof span.y === 'number' && typeof span.h === 'number') {
                const bottom = span.y + span.h;
                if (bottom > maxBottomPct) maxBottomPct = bottom;
            }
        });
        // If any tile extends beyond 100% (which it shouldn't), grow canvas
        // proportionally so the percentage resolves to a reasonable pixel height
        const canvasH = Math.max(minHeight, Math.round(minHeight * maxBottomPct / 100));
        return canvasH;
    }

    function applyManualLayout() {
        // Apply manual geometry overrides to all tiles that have them
        // Used when canvas is NOT in edit mode but has manual overrides
        const canvas = state.canvasEl;
        if (!canvas) return;
        const tiles = Array.from(canvas.querySelectorAll('.universal-tile'));
        tiles.forEach(tile => {
            const tileId = tile.dataset.tileId;
            const saved = getTileGeometry(tileId);
            if (saved) {
                applyTileGeometry(tile, saved);
            } else {
                // No override for this tile — capture its grid position
                // (canvas should be in grid mode temporarily to get correct position)
                const geom = captureTilePositionAsPercent(tile);
                if (geom) applyTileGeometry(tile, geom);
            }
        });
    }

    function enterEditMode() {
        const canvas = state.canvasEl;
        if (!canvas) return;
        // CRITICAL: capture canvas height and all tile positions FIRST,
        // BEFORE applying any inline styles. Setting inline width/height
        // on grid items causes the grid to re-layout, which changes the
        // canvas height. By capturing first and applying all at once,
        // we preserve the original layout.
        const canvasRect = canvas.getBoundingClientRect();
        if (!canvasRect.width || !canvasRect.height) return;

        // Step 1: Capture all tile positions into an array (no DOM mutation yet)
        const tiles = Array.from(canvas.querySelectorAll('.universal-tile'));
        const geometries = tiles.map(tile => {
            const tileId = tile.dataset.tileId;
            // If user has a saved manual override from a previous session, use it
            const saved = getTileGeometry(tileId);
            if (saved) return { tile, geom: saved };
            // Otherwise capture current grid-rendered position
            const tileRect = tile.getBoundingClientRect();
            return {
                tile,
                geom: {
                    x: clampNumber(((tileRect.left - canvasRect.left) / canvasRect.width) * 100, 0, 100),
                    y: clampNumber(((tileRect.top - canvasRect.top) / canvasRect.height) * 100, 0, 100),
                    w: clampNumber((tileRect.width / canvasRect.width) * 100, 4, 100),
                    h: clampNumber((tileRect.height / canvasRect.height) * 100, 4, 100),
                    z: 0
                }
            };
        });

        // Step 2: Set canvas min-height to current grid-rendered height.
        // This MUST happen before applying tile styles, because once tiles
        // get inline width/height, the grid re-layouts and canvas height changes.
        canvas.style.minHeight = Math.round(canvasRect.height) + 'px';

        // Step 3: Apply all captured geometries at once
        geometries.forEach(({ tile, geom }) => {
            applyTileGeometry(tile, geom);
        });

        // Step 4: Switch to edit mode. CSS applies position:absolute + display:block.
        // Because tiles now have inline left/top/w/h AND canvas min-height is
        // preserved, tiles stay exactly where they were.
        canvas.classList.remove('is-frozen');
        canvas.classList.remove('has-manual-layout');
        canvas.classList.add('is-editing');
        requestAnimationFrame(resizeAllCharts);
    }

    function exitEditMode() {
        const canvas = state.canvasEl;
        if (!canvas) return;
        canvas.classList.add('is-frozen');
        canvas.classList.remove('is-editing');

        // If any tile has been manually positioned (has inline left/top styles),
        // keep canvas in absolute positioning mode so heights don't collapse.
        // Tiles WITHOUT manual overrides stay in grid flow.
        if (hasManualOverrides()) {
            canvas.classList.add('has-manual-layout');
        } else {
            canvas.classList.remove('has-manual-layout');
            // Clear any inline styles so all tiles return to grid placement
            const tiles = Array.from(canvas.querySelectorAll('.universal-tile'));
            tiles.forEach(tile => {
                tile.style.left = '';
                tile.style.top = '';
                tile.style.width = '';
                tile.style.height = '';
                tile.style.zIndex = '';
            });
            reclassifyLayout();
        }
        requestAnimationFrame(resizeAllCharts);
    }

    // When user starts dragging/resizing a tile, capture its current grid
    // position to absolute coordinates ONCE (so the drag has a starting point).
    // This tile then leaves grid flow and becomes absolutely positioned.
    function captureTileToAbsolute(tile) {
        // Only capture if not already absolute (no inline left/top)
        if (tile.style.left && tile.style.top) return;
        const tileId = tile.dataset.tileId;
        // If user has a saved override, use it
        const saved = getTileGeometry(tileId);
        if (saved) {
            applyTileGeometry(tile, saved);
            return;
        }
        // Capture current grid-rendered position
        const geom = captureTilePositionAsPercent(tile);
        if (geom) applyTileGeometry(tile, geom);
    }

    function wireLayoutHandles(canvas, tile) {
        const mv = document.createElement('button');
        mv.type = 'button';
        mv.className = 'universal-move';
        mv.setAttribute('aria-label', 'Drag to move. Double-click to reset.');
        mv.title = 'Drag to move this tile. Double-click to reset.';
        mv.innerHTML = '⠿';

        const rs = document.createElement('span');
        rs.className = 'universal-resize';
        rs.title = 'Drag to resize';

        tile.append(mv, rs);

        const tileId = tile.dataset.tileId;
        let layoutZ = 0;

        // ── Move + Resize — uses Pointer Events (works on mouse + touch + PWA) ──
        const begin = (event, mode) => {
            if (event.button !== undefined && event.button !== 0) return;
            event.preventDefault();
            event.stopPropagation();
            const canvasRect = canvas.getBoundingClientRect();
            if (!canvasRect.width || !canvasRect.height) return;

            captureTileToAbsolute(tile);

            const start = (function() {
                const left = tile.style.left;
                const top = tile.style.top;
                const width = tile.style.width;
                const height = tile.style.height;
                if (left && top && width && height) {
                    return {
                        x: parseFloat(left) || 0,
                        y: parseFloat(top) || 0,
                        w: parseFloat(width) || 25,
                        h: parseFloat(height) || 25,
                        z: Number(tile.style.zIndex || 0)
                    };
                }
                return captureTilePositionAsPercent(tile) || { x: 0, y: 0, w: 25, h: 25, z: 0 };
            })();

            const pointerX = event.clientX;
            const pointerY = event.clientY;
            layoutZ = Math.max(layoutZ, start.z || 0) + 1;
            start.z = layoutZ;
            applyTileGeometry(tile, start);
            tile.classList.add('universal-tile-active');
            canvas.classList.add('universal-canvas-changing');

            // Capture pointer so we keep getting events even if finger
            // moves outside the handle element (critical for touch/PWA)
            try { event.target.setPointerCapture(event.pointerId); } catch (_) {}

            const onMove = (moveEvent) => {
                moveEvent.preventDefault();
                const dx = ((moveEvent.clientX - pointerX) / canvasRect.width) * 100;
                const dy = ((moveEvent.clientY - pointerY) / canvasRect.height) * 100;
                const next = Object.assign({}, start);
                if (mode === 'move') {
                    next.x = clampNumber(start.x + dx, 0, Math.max(0, 100 - start.w));
                    next.y = clampNumber(start.y + dy, 0, Math.max(0, 100 - start.h));
                } else {
                    next.w = clampNumber(start.w + dx, 4, Math.max(4, 100 - start.x));
                    next.h = clampNumber(start.h + dy, 4, Math.max(4, 100 - start.y));
                }
                applyTileGeometry(tile, next);
                resizeChart(tileId);
            };

            const finish = (finishEvent) => {
                window.removeEventListener('pointermove', onMove);
                window.removeEventListener('pointerup', finish);
                window.removeEventListener('pointercancel', finish);
                try { if (finishEvent && finishEvent.target) finishEvent.target.releasePointerCapture(finishEvent.pointerId); } catch (_) {}
                tile.classList.remove('universal-tile-active');
                canvas.classList.remove('universal-canvas-changing');
                const finalGeom = captureTilePositionAsPercent(tile);
                if (finalGeom) {
                    finalGeom.z = Number(tile.style.zIndex || 0);
                    state.tileSpans[tileId] = finalGeom;
                }
                saveLayout();
                resizeChart(tileId);
            };

            window.addEventListener('pointermove', onMove);
            window.addEventListener('pointerup', finish);
            window.addEventListener('pointercancel', finish);
        };

        // Pointer events (work on mouse + touch + pen)
        mv.addEventListener('pointerdown', e => begin(e, 'move'));
        rs.addEventListener('pointerdown', e => begin(e, 'resize'));

        // Double-click/double-tap ⠿ resets this tile to default
        mv.addEventListener('dblclick', e => {
            e.preventDefault();
            e.stopPropagation();
            delete state.tileSpans[tileId];
            saveLayout();
            // Clear inline styles so grid takes over
            tile.style.left = '';
            tile.style.top = '';
            tile.style.width = '';
            tile.style.height = '';
            tile.style.zIndex = '';
            // If we're in edit mode, re-capture position from new grid placement
            if (canvas.classList.contains('is-editing')) {
                requestAnimationFrame(() => {
                    const geom = captureTilePositionAsPercent(tile);
                    if (geom) applyTileGeometry(tile, geom);
                    resizeChart(tileId);
                });
            } else {
                reclassifyLayout();
                requestAnimationFrame(resizeAllCharts);
            }
        });
    }


    function render(payload) {
        state.payload = payload;
        const aiBriefTile = buildAiBriefTile(payload);

        // Resolve current theme — read from localStorage first, then html attribute, default to dark
        // (matches theme-toggle.js which sets 'docchat-theme' key)
        let currentTheme;
        try {
            currentTheme = localStorage.getItem('docchat-theme');
        } catch (e) {}
        if (currentTheme !== 'light' && currentTheme !== 'dark') {
            currentTheme = document.documentElement.getAttribute('data-theme') || 'light';
        }
        document.documentElement.setAttribute('data-theme', currentTheme);
        document.body.setAttribute('data-theme', currentTheme);

        app.innerHTML = `
            <main class="universal-page">
                <header class="universal-header">
                    <div class="universal-title">${esc(payload.title || '')}</div>
                    <div class="universal-header-right">
                        <div class="universal-asof">${esc(payload.asOfLabel || '')}</div>
                        <button class="universal-theme-toggle" type="button" id="universalThemeToggle" title="Toggle light/dark theme" aria-label="Toggle theme">
                            <span id="universalThemeIcon"></span>
                        </button>
                        <button class="universal-btn" type="button" id="universalEditBtn" title="Click to unlock editing — drag the ⠿ handle to move tiles, drag the right or bottom edge to resize. Double-click ⠿ to reset."><i class="fa-solid fa-pen-to-square"></i><span class="universal-btn-text">Edit</span></button>
                        <button class="universal-btn" type="button" onclick="if(confirm('Reset all tiles to default size?')) window.__universalResetLayout()" title="Reset all tiles to default size and order"><i class="fa-solid fa-rotate-left"></i><span class="universal-btn-text">Reset</span></button>
                    </div>
                </header>
                <div class="universal-canvas is-frozen"></div>
            </main>
        `;

        const canvas = app.querySelector('.universal-canvas');
        state.canvasEl = canvas;

        // Wire theme toggle button
        const themeBtn = app.querySelector('#universalThemeToggle');
        const themeIcon = app.querySelector('#universalThemeIcon');
        function updateThemeIcon() {
            const t = document.documentElement.getAttribute('data-theme') || 'light';
            if (themeIcon) {
                themeIcon.innerHTML = t === 'dark'
                    ? '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3"/><line x1="12" y1="21" x2="12" y2="23"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"/><line x1="1" y1="12" x2="3" y2="12"/><line x1="21" y1="12" x2="23" y2="12"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"/></svg>'
                    : '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>';
            }
        }
        updateThemeIcon();
        if (themeBtn) {
            themeBtn.addEventListener('click', () => {
                const cur = document.documentElement.getAttribute('data-theme') || 'light';
                const next = cur === 'dark' ? 'light' : 'dark';
                document.documentElement.setAttribute('data-theme', next);
                document.body.setAttribute('data-theme', next);
                try { localStorage.setItem('docchat-theme', next); } catch (e) {}
                updateThemeIcon();
                // Re-render charts so their colors match the new theme
                requestAnimationFrame(resizeAllCharts);
            });
        }

        (payload.metrics || []).forEach((m, i) => canvas.appendChild(buildMetricTile(m, i)));
        (payload.charts || []).forEach((c, i) => canvas.appendChild(buildChartTile(c, i)));
        (payload.tables || []).forEach((t, i) => canvas.appendChild(buildTableTile(t, i)));
        if (aiBriefTile) canvas.appendChild(aiBriefTile);

        canvas.querySelectorAll('[data-tile-id]').forEach(tile => {
            applySavedSpans(tile);
            wireLayoutHandles(canvas, tile);
        });

        reclassifyLayout();

        // If user has saved manual overrides from a previous session, apply them
        // to ALL tiles and switch canvas to absolute positioning mode.
        // Uses same capture-first approach as enterEditMode() to prevent
        // canvas height collapse.
        if (hasManualOverrides()) {
            requestAnimationFrame(() => {
                const canvasRect = canvas.getBoundingClientRect();
                if (!canvasRect.width || !canvasRect.height) return;
                // Capture all tile positions FIRST
                const tiles = Array.from(canvas.querySelectorAll('.universal-tile'));
                const geometries = tiles.map(tile => {
                    const tileId = tile.dataset.tileId;
                    const saved = getTileGeometry(tileId);
                    if (saved) return { tile, geom: saved };
                    const tileRect = tile.getBoundingClientRect();
                    return {
                        tile,
                        geom: {
                            x: clampNumber(((tileRect.left - canvasRect.left) / canvasRect.width) * 100, 0, 100),
                            y: clampNumber(((tileRect.top - canvasRect.top) / canvasRect.height) * 100, 0, 100),
                            w: clampNumber((tileRect.width / canvasRect.width) * 100, 4, 100),
                            h: clampNumber((tileRect.height / canvasRect.height) * 100, 4, 100),
                            z: 0
                        }
                    };
                });
                // Set canvas min-height BEFORE applying tile styles
                canvas.style.minHeight = Math.round(canvasRect.height) + 'px';
                // Apply all geometries at once
                geometries.forEach(({ tile, geom }) => applyTileGeometry(tile, geom));
                // Switch to manual layout mode (handles hidden, absolute positioning)
                canvas.classList.add('has-manual-layout', 'is-frozen');
                canvas.classList.remove('is-editing');
                requestAnimationFrame(resizeAllCharts);
            });
        }

        const editBtn = app.querySelector('#universalEditBtn');
        if (editBtn) {
            let editing = false;
            editBtn.addEventListener('click', () => {
                editing = !editing;
                if (editing) {
                    enterEditMode();
                    editBtn.innerHTML = '<i class="fa-solid fa-check"></i><span class="universal-btn-text">Done</span>';
                    editBtn.classList.add('is-active');
                } else {
                    exitEditMode();
                    editBtn.innerHTML = '<i class="fa-solid fa-pen-to-square"></i><span class="universal-btn-text">Edit</span>';
                    editBtn.classList.remove('is-active');
                    saveLayout();
                }
            });
        }

        if (!window.echarts) {
            console.warn('echarts not loaded');
        } else {
            (payload.charts || []).forEach(chartModel => {
                const host = canvas.querySelector(`[data-chart-id="${CSS.escape(chartModel.id)}"]`);
                if (!host) return;
                const chart = window.echarts.init(host);
                chart.setOption(chartOption(chartModel), true);
                charts.set('chart:' + chartModel.id, chart);

                const ro = new ResizeObserver(() => {
                    requestAnimationFrame(() => resizeChart('chart:' + chartModel.id));
                });
                ro.observe(host);
                tileResizeObservers.set('chart:' + chartModel.id, ro);
            });
        }

        const canvasRO = new ResizeObserver(() => {
            if (state.reclassifyScheduled) return;
            state.reclassifyScheduled = true;
            requestAnimationFrame(() => {
                state.reclassifyScheduled = false;
                reclassifyLayout();
            });
        });
        canvasRO.observe(canvas);
    }

    async function load() {
        if (!suite) {
            app.innerHTML = '<div class="universal-error">Missing data-suite attribute on &lt;body&gt;.</div>';
            return;
        }
        app.innerHTML = '<div class="universal-loading">Loading dashboard data…</div>';
        try {
            const endpoint = new URL('../data/executive/' + suite + '.json', window.location.href);
            const response = await fetch(endpoint.toString(), { cache: 'no-store' });
            if (!response.ok) throw new Error(await response.text() || `HTTP ${response.status}`);
            render(await response.json());
        } catch (error) {
            app.innerHTML = `<div class="universal-error">${esc(error?.message || error || 'Dashboard data failed to load.')}</div>`;
        }
    }

    window.addEventListener('resize', () => {
        if (state.reclassifyScheduled) return;
        state.reclassifyScheduled = true;
        requestAnimationFrame(() => {
            state.reclassifyScheduled = false;
            reclassifyLayout();
        });
    });

    window.addEventListener('message', event => {
        // Allow cross-origin messages from parent (theme-toggle.js broadcasts to all iframes)
        const msg = event.data || {};
        const type = String(msg.type || '');

        // Theme broadcast from parent frame (csr-dashboard-theme:apply)
        if (type === 'csr-dashboard-theme:apply' || type === 'universal-theme:apply') {
            const theme = msg.theme === 'dark' ? 'dark' : 'light';
            document.documentElement.setAttribute('data-theme', theme);
            document.body.setAttribute('data-theme', theme);
            // Update toggle icon if present
            const iconHost = document.getElementById('universalThemeIcon');
            if (iconHost) {
                iconHost.innerHTML = theme === 'dark'
                    ? '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"/><line x1="12" y1="1" x2="12" y2="3"/><line x1="12" y1="21" x2="12" y2="23"/><line x1="4.22" y1="4.22" x2="5.64" y2="5.64"/><line x1="18.36" y1="18.36" x2="19.78" y2="19.78"/><line x1="1" y1="12" x2="3" y2="12"/><line x1="21" y1="12" x2="23" y2="12"/><line x1="4.22" y1="19.78" x2="5.64" y2="18.36"/><line x1="18.36" y1="5.64" x2="19.78" y2="4.22"/></svg>'
                    : '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/></svg>';
            }
            requestAnimationFrame(resizeAllCharts);
            return;
        }

        // Same-origin layout/resize messages
        if (event.origin !== window.location.origin) return;
        if (type === 'dashboard-custom-html:layout') {
            const layout = msg?.payload?.visualLayout ?? msg?.visualLayout ?? {};
            if (layout && typeof layout === 'object') {
                state.tileSpans = {};
                Object.entries(layout).forEach(([id, span]) => {
                    if (span && typeof span === 'object') {
                        state.tileSpans[id] = { colSpan: span.colSpan || 0, rowSpan: span.rowSpan || 0 };
                    }
                });
                if (state.canvasEl) {
                    state.canvasEl.querySelectorAll('[data-tile-id]').forEach(tile => {
                        tile.style.gridColumn = '';
                        tile.style.gridRow = '';
                        tile.removeAttribute('data-manual-span');
                        applySavedSpans(tile);
                    });
                    reclassifyLayout();
                    resizeAllCharts();
                }
            }
            return;
        }
        if (type.endsWith(':resize')) {
            requestAnimationFrame(reclassifyLayout);
        }
    });

    try {
        window.parent.postMessage({
            type: 'dashboard-custom-html:ready',
            templateId: `universal-${suite}`
        }, window.location.origin);
    } catch (_) {}

    window.universalDashboard = { render, resizeAllCharts, reclassifyLayout };

    load();
})();
