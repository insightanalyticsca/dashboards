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
        try { localStorage.setItem(SPAN_KEY, JSON.stringify(state.tileSpans)); }
        catch (e) {}
    }
    function loadLayout() {
        try {
            const saved = localStorage.getItem(SPAN_KEY);
            if (saved) {
                const parsed = JSON.parse(saved);
                if (parsed && typeof parsed === 'object') state.tileSpans = parsed;
            }
        } catch (e) {}
    }
    loadLayout();

    window.__universalResetLayout = function () {
        try { localStorage.removeItem(SPAN_KEY); } catch (e) {}
        state.tileSpans = {};
        if (state.canvasEl) {
            state.canvasEl.querySelectorAll('[data-tile-id]').forEach(tile => {
                tile.style.gridColumn = '';
                tile.style.gridRow = '';
                tile.removeAttribute('data-manual-span');
            });
            reclassifyLayout();
            resizeAllCharts();
        }
    };

    // ── Palette: harmonized brand colors + supplementary hues ──
    // Each series gets a different color from this list (cycling).
    function palette() {
        const s = getComputedStyle(document.documentElement);
        const v = [
            s.getPropertyValue('--exec-primary').trim() || '#0808ee',
            s.getPropertyValue('--exec-secondary').trim() || '#09c698',
            s.getPropertyValue('--exec-highlight').trim() || '#BBFF05',
            s.getPropertyValue('--exec-accent').trim() || '#845ef7',
            '#0ea5e9',   // sky blue
            '#f59e0b',   // amber
            '#ec4899',   // pink
            '#10b981',   // emerald
            '#6366f1',   // indigo
            '#e53e6b',   // rose
            '#14b8a6',   // teal
            '#f97316'    // orange
        ];
        return [...new Set(v)];
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
                <span class="universal-ai-title">
                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6m0 8v6m10-10h-6m-8 0H2m13.5-5.5l-2.5 2.5m-6 6l-2.5 2.5m11 0l-2.5-2.5m-6-6L4.5 4.5"/></svg>
                    AI Brief
                </span>
                <span class="universal-ai-badge">AI-wired</span>
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
        // Tighter fonts — denser look (was 3.5% of width, now 2.7%)
        const labelFont = Math.max(8, Math.min(11, Math.round(tileW * 0.027)));
        const titleFont = Math.max(9, Math.min(12, Math.round(tileW * 0.032)));
        const colors = palette();
        const kind = String(chart.kind || '').toLowerCase();
        const isPie = kind === 'pie' || kind === 'donut';
        const isDonut = kind === 'donut';
        const isHeatmap = kind === 'heatmap';

        // ── PIE / DONUT ──
        if (isPie) {
            const s = chart.series[0] || { data: [] };
            const categories = chart.categories || [];
            const pieData = (s.data || []).map((v, i) => ({
                value: v,
                name: categories[i] || `Segment ${i + 1}`,
                itemStyle: { color: colors[i % colors.length] }
            }));
            const legendRight = tileW > 320;
            return {
                animationDuration: 400,
                animationEasing: 'cubicOut',
                color: colors,
                tooltip: {
                    trigger: 'item',
                    formatter: '{b}: {c} ({d}%)',
                    backgroundColor: 'rgba(255,255,255,0.96)',
                    borderColor: '#e2e8f0',
                    borderWidth: 1,
                    textStyle: { color: '#1e293b', fontSize: labelFont, fontWeight: 600 }
                },
                legend: {
                    type: 'scroll',
                    orient: legendRight ? 'vertical' : 'horizontal',
                    right: legendRight ? 8 : 'center',
                    top: legendRight ? 'middle' : 0,
                    bottom: legendRight ? 'middle' : 0,
                    left: legendRight ? '55%' : 'center',
                    textStyle: { fontSize: labelFont, color: '#1e293b', fontWeight: 600 },
                    itemWidth: 8, itemHeight: 8, itemGap: 6
                },
                series: [{
                    name: s.name || chart.title,
                    type: 'pie',
                    radius: isDonut ? ['38%', '62%'] : '62%',
                    center: legendRight ? ['28%', '50%'] : ['50%', '55%'],
                    avoidLabelOverlap: true,
                    itemStyle: {
                        borderColor: '#fff',
                        borderWidth: 2,
                        borderRadius: 3
                    },
                    label: {
                        show: !legendRight,
                        position: 'outer',
                        formatter: '{b}\n{d}%',
                        fontSize: labelFont - 1,
                        color: '#1e293b',
                        fontWeight: 600
                    },
                    emphasis: {
                        label: { show: true, fontSize: labelFont + 1, fontWeight: 800 },
                        itemStyle: { shadowBlur: 10, shadowColor: 'rgba(99,102,241,0.3)' }
                    },
                    data: pieData
                }]
            };
        }

        // ── HEATMAP (real ECharts heatmap, not multi-series bar) ──
        if (isHeatmap) {
            const categoriesX = chart.categories || [];
            const categoriesY = (chart.series || []).map(s => s.name);
            // Flatten: series[i].data[j] → [j, i, value]
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
            return {
                animationDuration: 400,
                tooltip: {
                    position: 'top',
                    backgroundColor: 'rgba(255,255,255,0.96)',
                    borderColor: '#e2e8f0',
                    borderWidth: 1,
                    textStyle: { color: '#1e293b', fontSize: labelFont, fontWeight: 600 },
                    formatter: params => `${categoriesY[params.value[1]]} · ${categoriesX[params.value[0]]}: <b>${params.value[2]}</b>`
                },
                grid: { left: 70, right: 14, top: 8, bottom: 36, containLabel: false },
                xAxis: {
                    type: 'category',
                    data: categoriesX,
                    splitArea: { show: true },
                    axisLabel: { color: '#1e293b', fontSize: labelFont, fontWeight: 600, interval: 0, hideOverlap: true },
                    axisLine: { lineStyle: { color: '#94a3b8' } },
                    axisTick: { show: false }
                },
                yAxis: {
                    type: 'category',
                    data: categoriesY,
                    splitArea: { show: true },
                    axisLabel: { color: '#1e293b', fontSize: labelFont, fontWeight: 600 },
                    axisLine: { lineStyle: { color: '#94a3b8' } },
                    axisTick: { show: false }
                },
                visualMap: {
                    min, max,
                    calculable: true,
                    orient: 'horizontal',
                    left: 'center',
                    bottom: 0,
                    textStyle: { fontSize: labelFont - 1, color: '#64748b' },
                    inRange: { color: ['#dbeafe', '#93c5fd', '#3b82f6', '#1e40af'] }
                },
                series: [{
                    type: 'heatmap',
                    data: data,
                    label: {
                        show: tileW > 480 && data.length < 30,
                        fontSize: labelFont - 2,
                        color: '#1e293b',
                        fontWeight: 700
                    },
                    emphasis: {
                        itemStyle: { shadowBlur: 8, shadowColor: 'rgba(99,102,241,0.4)' }
                    }
                }]
            };
        }

        // ── BAR / LINE / COMBO / STACKEDBAR (standard cartesian) ──
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
            return {
                name: s.name,
                type,
                yAxisIndex: s.axis === 'right' ? 1 : 0,
                data: s.data,
                smooth: !!s.smooth && type === 'line',
                itemStyle: {
                    color: color,
                    borderRadius: type === 'bar' ? [3, 3, 0, 0] : 0
                },
                lineStyle: type === 'line' ? { color: color, width: 2.2 } : undefined,
                symbol: type === 'line' ? 'circle' : 'none',
                symbolSize: type === 'line' ? 5 : 0,
                stack: s.stack || (kind === 'stackedbar' ? 'total' : undefined),
                barCategoryGap: '40%'
            };
        });

        return {
            animationDuration: 400,
            animationEasing: 'cubicOut',
            color: colors,
            tooltip: {
                trigger: 'axis',
                axisPointer: { type: 'shadow' },
                backgroundColor: 'rgba(255,255,255,0.96)',
                borderColor: '#e2e8f0',
                borderWidth: 1,
                textStyle: { color: '#1e293b', fontSize: labelFont, fontWeight: 600 }
            },
            legend: {
                top: 2, type: 'scroll',
                textStyle: { fontSize: Math.max(7, labelFont - 1), color: '#1e293b', fontWeight: 600 },
                itemWidth: 7, itemHeight: 7, itemGap: 5
            },
            grid: { left: 44, right: hasSecondAxis ? 44 : 14, top: 22, bottom: 28, containLabel: false },
            xAxis: {
                type: 'category',
                data: categories,
                axisLabel: { color: '#1e293b', fontSize: labelFont, fontWeight: 600, interval: 0, hideOverlap: true, rotate: categories.length > 6 ? 25 : 0 },
                axisLine: { lineStyle: { color: '#94a3b8' } },
                axisTick: { show: false }
            },
            yAxis: [
                { type: 'value', name: chart.leftAxisTitle || '', nameTextStyle: { fontSize: Math.max(7, labelFont - 1) }, axisLabel: { formatter: axisFormatter, fontSize: labelFont, color: '#1e293b', fontWeight: 600 }, splitLine: { lineStyle: { color: '#e2e8f0', type: 'dashed' } } },
                ...(hasSecondAxis ? [{ type: 'value', name: chart.rightAxisTitle || '', nameTextStyle: { fontSize: Math.max(7, labelFont - 1) }, axisLabel: { formatter: axisFormatter, fontSize: labelFont, color: '#1e293b', fontWeight: 600 }, splitLine: { show: false } }] : [])
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
    function reclassifyLayout() {
        const canvas = state.canvasEl;
        if (!canvas) return;

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

        function promoteLast() {
            if (lastStretchableIndex < 0) return;
            const prev = tiles[lastStretchableIndex];
            const prevNatural = prev.dataset.naturalType;
            if (prevNatural === 'chart-third') prev.dataset.type = 'chart-half';
            else if (prevNatural === 'chart-half') prev.dataset.type = 'chart-wide';
            else if (prevNatural === 'table-half') prev.dataset.type = 'table-wide';
            lastStretchableIndex = -1;
        }

        function spanFor(natural) {
            if (natural === 'metric') return 1;
            if (natural === 'chart-third') return 1;
            if (natural === 'chart-half' || natural === 'table-half') return 2;
            return colCount; // wide / table-wide / ai-brief
        }

        function isStretchable(natural) {
            return natural === 'chart-third' || natural === 'chart-half' || natural === 'table-half';
        }

        for (let i = 0; i < tiles.length; i++) {
            const tile = tiles[i];
            if (tile.getAttribute('data-manual-span') === '1') continue;

            const natural = tile.dataset.naturalType;
            const span = spanFor(natural);

            if (span === colCount) {
                if (currentX > 0 && currentX < colCount) promoteLast();
                currentX = 0;
                lastStretchableIndex = -1;
                continue;
            }

            if (currentX + span > colCount) {
                promoteLast();
                currentX = 0;
            }

            if (isStretchable(natural)) lastStretchableIndex = i;
            else lastStretchableIndex = -1;

            currentX += span;
            if (currentX >= colCount) {
                currentX = 0;
                lastStretchableIndex = -1;
            }
        }

        if (currentX > 0 && currentX < colCount) promoteLast();

        requestAnimationFrame(resizeAllCharts);
    }

    function wireResizeHandles(canvas, tile) {
        const rx = document.createElement('div');
        rx.className = 'universal-resize-x';
        const ry = document.createElement('div');
        ry.className = 'universal-resize-y';
        const mv = document.createElement('button');
        mv.type = 'button';
        mv.className = 'universal-move';
        mv.setAttribute('aria-label', 'Reset size');
        mv.innerHTML = '⠿';
        mv.addEventListener('dblclick', e => {
            e.preventDefault();
            tile.style.gridColumn = '';
            tile.style.gridRow = '';
            tile.removeAttribute('data-manual-span');
            delete state.tileSpans[tile.dataset.tileId];
            saveLayout();
            reclassifyLayout();
            resizeAllCharts();
        });
        tile.append(mv, rx, ry);

        const beginDrag = (mode) => (event) => {
            if (event.button !== 0) return;
            event.preventDefault();
            event.stopPropagation();
            const startX = event.clientX;
            const startY = event.clientY;
            const startW = tile.getBoundingClientRect().width;
            const startH = tile.getBoundingClientRect().height;
            const cols = getComputedStyle(canvas).gridTemplateColumns.split(' ').filter(s => s.trim()).length;
            const canvasW = canvas.getBoundingClientRect().width;
            const colWidth = (canvasW - 6 * (cols - 1)) / cols;
            const rowHeight = 80;

            const move = (ev) => {
                if (mode === 'x') {
                    const dx = ev.clientX - startX;
                    const newCols = Math.max(1, Math.min(cols, Math.round((startW + dx) / colWidth)));
                    tile.style.gridColumn = `span ${newCols}`;
                    tile.setAttribute('data-manual-span', '1');
                } else if (mode === 'y') {
                    const dy = ev.clientY - startY;
                    const newRows = Math.max(1, Math.round((startH + dy) / rowHeight));
                    tile.style.gridRow = `span ${newRows}`;
                    tile.setAttribute('data-manual-span', '1');
                }
                resizeChart(tile.dataset.tileId);
            };
            const end = () => {
                document.removeEventListener('mousemove', move);
                document.removeEventListener('mouseup', end);
                const gc = tile.style.gridColumn;
                const gr = tile.style.gridRow;
                const colSpan = gc ? parseInt(gc.match(/span\s+(\d+)/)?.[1] || '0', 10) : 0;
                const rowSpan = gr ? parseInt(gr.match(/span\s+(\d+)/)?.[1] || '0', 10) : 0;
                if (colSpan || rowSpan) {
                    state.tileSpans[tile.dataset.tileId] = { colSpan, rowSpan };
                }
                saveLayout();
                resizeChart(tile.dataset.tileId);
            };
            document.addEventListener('mousemove', move);
            document.addEventListener('mouseup', end);
        };
        rx.addEventListener('mousedown', beginDrag('x'));
        ry.addEventListener('mousedown', beginDrag('y'));
    }

    function render(payload) {
        state.payload = payload;
        const aiBriefTile = buildAiBriefTile(payload);

        app.innerHTML = `
            <main class="universal-page">
                <header class="universal-header">
                    <div class="universal-title">${esc(payload.title || '')}</div>
                    <div class="universal-header-right">
                        <div class="universal-asof">${esc(payload.asOfLabel || '')}</div>
                        <button class="universal-btn" type="button" id="universalEditBtn" title="Click to unlock editing — drag the right or bottom edge of any tile to resize."><i class="fa-solid fa-pen-to-square"></i> Edit</button>
                        <button class="universal-btn" type="button" onclick="if(confirm('Reset all tiles to default size?')) window.__universalResetLayout()"><i class="fa-solid fa-rotate-left"></i> Reset</button>
                    </div>
                </header>
                <div class="universal-canvas is-frozen"></div>
            </main>
        `;

        const canvas = app.querySelector('.universal-canvas');
        state.canvasEl = canvas;

        (payload.metrics || []).forEach((m, i) => canvas.appendChild(buildMetricTile(m, i)));
        (payload.charts || []).forEach((c, i) => canvas.appendChild(buildChartTile(c, i)));
        (payload.tables || []).forEach((t, i) => canvas.appendChild(buildTableTile(t, i)));
        if (aiBriefTile) canvas.appendChild(aiBriefTile);

        canvas.querySelectorAll('[data-tile-id]').forEach(tile => {
            applySavedSpans(tile);
            wireResizeHandles(canvas, tile);
        });

        reclassifyLayout();

        const editBtn = app.querySelector('#universalEditBtn');
        if (editBtn) {
            let editing = false;
            editBtn.addEventListener('click', () => {
                editing = !editing;
                if (editing) {
                    canvas.classList.remove('is-frozen');
                    canvas.classList.add('is-editing');
                    editBtn.innerHTML = '<i class="fa-solid fa-check"></i> Done';
                    editBtn.classList.add('is-active');
                } else {
                    canvas.classList.add('is-frozen');
                    canvas.classList.remove('is-editing');
                    editBtn.innerHTML = '<i class="fa-solid fa-pen-to-square"></i> Edit';
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
        if (event.origin !== window.location.origin) return;
        const msg = event.data || {};
        const type = String(msg.type || '');
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
