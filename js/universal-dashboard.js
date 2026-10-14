/* ════════════════════════════════════════════════════════════════════════════
   UNIVERSAL DASHBOARD CONSTRUCTOR v2 — thorough classification
   ────────────────────────────────────────────────────────────────────────────
   Reads the same JSON contract as dash-suite.js. Renders every tile via
   a fixed-breakpoint CSS Grid (4 cols desktop / 2 tablet / 1 mobile).

   CLASSIFICATION SYSTEM (the contract every visual obeys):

     data-natural-type  = immutable, set once at build time
     data-type         = mutable, reclassifyLayout() may promote on the fly

     metric       → natural span 1 (quarter)
     chart-half   → natural span 2 (half)        STRETCHABLE → can promote to chart-wide
     table-half   → natural span 2 (half)        STRETCHABLE → can promote to table-wide
     chart-wide   → natural span all (full)
     table-wide   → natural span all (full)
     ai-brief     → natural span all (full)

   reclassifyLayout() runs on EVERY resize and:
     1. Resets all tiles back to their natural-type (data-natural-type → data-type)
     2. Simulates grid placement: walks tiles in DOM order, tracks current col
     3. When a half-tile would leave a hole (current + 2 > colCount, OR final
        row is partial), promotes it to wide
     4. Resizes all charts (their containers changed width)

   User overrides (Edit mode drag) are preserved via `data-manual-span="1"`
   on tiles the user has manually resized — reclassifyLayout() skips those.
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

    function palette() {
        const s = getComputedStyle(document.documentElement);
        const v = [
            s.getPropertyValue('--exec-primary').trim(),
            s.getPropertyValue('--exec-secondary').trim(),
            s.getPropertyValue('--exec-highlight').trim(),
            s.getPropertyValue('--exec-accent').trim(),
            '#4C6FFF', '#2DD4BF', '#FBBF24', '#845EF7'
        ].filter(Boolean);
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
    // Every tile gets:
    //   data-natural-type  = immutable classification (set here, never changed)
    //   data-type          = current display type (mutable via reclassifyLayout)
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
        const isWide = chart.width === 'wide' || chart.width === 'two-thirds';
        tile.dataset.naturalType = isWide ? 'chart-wide' : 'chart-half';
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
        const isWide = table.width === 'wide' || table.width === 'two-thirds';
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
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6m0 8v6m10-10h-6m-8 0H2m13.5-5.5l-2.5 2.5m-6 6l-2.5 2.5m11 0l-2.5-2.5m-6-6L4.5 4.5"/></svg>
                    AI Brief
                </span>
                <span class="universal-ai-badge">AI-wired</span>
            </div>
            <div class="universal-ai-grid">${cells}</div>
        `;
        return tile;
    }

    // ── Chart option builder (uses measured tile width for responsive fonts) ─
    function chartOption(chart) {
        const tileEl = state.canvasEl.querySelector(`[data-tile-id="chart:${chart.id}"]`);
        const tileW = tileEl ? tileEl.getBoundingClientRect().width : 360;
        const labelFont = Math.max(8, Math.min(13, Math.round(tileW * 0.035)));
        const colors = palette();
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

        const series = (chart.series || []).map(s => {
            const color = s.color || colors[0];
            return {
                name: s.name,
                type: s.type === 'heatmap' ? 'bar' : (s.type || 'bar'),
                yAxisIndex: s.axis === 'right' ? 1 : 0,
                data: s.data,
                smooth: !!s.smooth,
                itemStyle: { color, borderRadius: chart.kind === 'bar' ? [3, 3, 0, 0] : 0 },
                lineStyle: s.type === 'line' ? { color, width: 2.5 } : undefined,
                symbol: s.type === 'line' ? 'circle' : undefined,
                symbolSize: s.type === 'line' ? 6 : 0,
                stack: s.stack || undefined
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
                textStyle: { fontSize: Math.max(8, labelFont - 1), color: '#1e293b', fontWeight: 600 },
                itemWidth: 8, itemHeight: 8, itemGap: 4
            },
            grid: { left: 52, right: hasSecondAxis ? 52 : 18, top: 28, bottom: 32 },
            xAxis: {
                type: 'category', data: categories,
                axisLabel: { color: '#1e293b', fontSize: labelFont, fontWeight: 600, interval: 0, hideOverlap: true, rotate: categories.length > 6 ? 25 : 0 },
                axisLine: { lineStyle: { color: '#94a3b8' } },
                axisTick: { show: false }
            },
            yAxis: [
                { type: 'value', name: chart.leftAxisTitle || '', nameTextStyle: { fontSize: Math.max(7, labelFont - 1) }, axisLabel: { formatter: axisFormatter, fontSize: labelFont, color: '#1e293b', fontWeight: 600 }, splitLine: { lineStyle: { color: '#cbd5e1' } } },
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

    // ── Apply saved grid spans (Edit mode persistence) ──
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
    // reclassifyLayout() runs on initial render AND every canvas resize.
    // It simulates grid placement and promotes lone half-tiles to wide so
    // no row has an empty right half — at any viewport.
    function reclassifyLayout() {
        const canvas = state.canvasEl;
        if (!canvas) return;

        // Step 1: detect current column count from computed grid style
        const gridTemplateCols = getComputedStyle(canvas).gridTemplateColumns;
        const colCount = gridTemplateCols.split(' ').filter(s => s.trim()).length;
        if (colCount < 1) return;

        // Step 2: reset every tile back to its natural type
        //         (unless user has manually overridden it via Edit mode drag —
        //          then we preserve their span and skip promotion logic)
        const tiles = Array.from(canvas.querySelectorAll('.universal-tile'));
        tiles.forEach(tile => {
            if (tile.getAttribute('data-manual-span') === '1') return; // skip user overrides
            const natural = tile.dataset.naturalType;
            tile.dataset.type = natural;
            tile.style.gridColumn = '';
        });

        // Step 3: simulate grid placement. For each tile (in DOM order),
        // track current column position. If a half-tile would leave a hole
        // (current + 2 > colCount, OR a wide tile starts a new row when
        // current > 0 from a lone half), promote the lone half to wide.
        // Also: at end, if final row is partial (current > 0), promote the
        // last placed half-tile to wide.
        let currentX = 0;
        let lastHalfTileIndex = -1;

        // Helper: promote the previously-placed lone half-tile to wide,
        // so its row gets a single full-width tile instead of a half + hole.
        function promoteLastHalf() {
            if (lastHalfTileIndex < 0) return;
            const prev = tiles[lastHalfTileIndex];
            // Only promote if the previous half-tile is still in its natural state
            // (don't promote a tile that's already wide, e.g. chart-wide natural)
            const prevNatural = prev.dataset.naturalType;
            if (prevNatural === 'chart-half') prev.dataset.type = 'chart-wide';
            else if (prevNatural === 'table-half') prev.dataset.type = 'table-wide';
            lastHalfTileIndex = -1;
        }

        for (let i = 0; i < tiles.length; i++) {
            const tile = tiles[i];
            if (tile.getAttribute('data-manual-span') === '1') continue;

            const natural = tile.dataset.naturalType;
            let span;
            if (natural === 'metric') span = 1;
            else if (natural === 'chart-half' || natural === 'table-half') span = 2;
            else span = colCount; // wide / table-wide / ai-brief

            // Wide tiles always take a full row. If the previous row was
            // partial (currentX > 0) because a lone half-tile was placed
            // there, promote that lone half to wide BEFORE resetting.
            if (span === colCount) {
                if (currentX > 0 && currentX < colCount) {
                    promoteLastHalf();
                }
                currentX = 0;
                lastHalfTileIndex = -1;
                continue;
            }

            // Check if this half-tile fits in current row
            if (span === 2 && currentX + span > colCount) {
                // Doesn't fit — would leave a hole.
                // Promote the previously-placed half-tile in this partial row.
                promoteLastHalf();
                // Now this tile starts a new row at column 0
                currentX = 0;
            }

            // Place this tile
            if (span === 2) lastHalfTileIndex = i;
            else lastHalfTileIndex = -1;

            currentX += span;
            if (currentX >= colCount) {
                currentX = 0;
                lastHalfTileIndex = -1;
            }
        }

        // Step 4: after loop, if final row is partial (current > 0 but < colCount)
        // and there's a half-tile in it, promote it to wide so the row isn't half-empty.
        if (currentX > 0 && currentX < colCount) {
            promoteLastHalf();
        }

        // Step 5: resize all charts (their containers may have changed width
        // due to promotion from half → wide)
        requestAnimationFrame(resizeAllCharts);
    }

    // ── Wire up drag-to-resize handles (Edit mode) ──
    function wireResizeHandles(canvas, tile) {
        const rx = document.createElement('div');
        rx.className = 'universal-resize-x';
        rx.title = 'Drag to widen / shrink';
        const ry = document.createElement('div');
        ry.className = 'universal-resize-y';
        ry.title = 'Drag to make taller / shorter';
        const mv = document.createElement('button');
        mv.type = 'button';
        mv.className = 'universal-move';
        mv.setAttribute('aria-label', 'Reset size');
        mv.title = 'Double-click to reset size';
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
            const colWidth = (canvasW - 8 * (cols - 1)) / cols;
            const rowHeight = 120;

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

    // ── Main render ──
    function render(payload) {
        state.payload = payload;
        const aiBriefTile = buildAiBriefTile(payload);

        app.innerHTML = `
            <main class="universal-page">
                <header class="universal-header">
                    <div class="universal-title">${esc(payload.title || '')}</div>
                    <div class="universal-header-right">
                        <div class="universal-asof">${esc(payload.asOfLabel || '')}</div>
                        <button class="universal-btn" type="button" id="universalEditBtn" title="Click to unlock editing — drag the right or bottom edge of any tile to resize. Double-click ⠿ to reset. Click Done to save."><i class="fa-solid fa-pen-to-square"></i> Edit</button>
                        <button class="universal-btn" type="button" onclick="if(confirm('Reset all tiles to default size?')) window.__universalResetLayout()"><i class="fa-solid fa-rotate-left"></i> Reset</button>
                    </div>
                </header>
                <div class="universal-canvas is-frozen"></div>
            </main>
        `;

        const canvas = app.querySelector('.universal-canvas');
        state.canvasEl = canvas;

        // Build all tiles — Grid auto-flows them
        (payload.metrics || []).forEach((m, i) => canvas.appendChild(buildMetricTile(m, i)));
        (payload.charts || []).forEach((c, i) => canvas.appendChild(buildChartTile(c, i)));
        (payload.tables || []).forEach((t, i) => canvas.appendChild(buildTableTile(t, i)));
        if (aiBriefTile) canvas.appendChild(aiBriefTile);

        // Apply saved spans + wire resize handles
        canvas.querySelectorAll('[data-tile-id]').forEach(tile => {
            applySavedSpans(tile);
            wireResizeHandles(canvas, tile);
        });

        // Run initial classification
        reclassifyLayout();

        // Edit button
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

        // Init charts
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

        // ══ CANVAS-LEVEL ResizeObserver — the heart of the universal constructor
        // Every time the canvas changes size (viewport resize, browser chrome
        // toggle, orientation change, parent frame layout shift), we re-run
        // reclassifyLayout() so half-tiles get re-promoted correctly for the
        // new column count.
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

    // ── Load JSON, render ──
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

    // Window resize fallback (in addition to ResizeObserver)
    window.addEventListener('resize', () => {
        if (state.reclassifyScheduled) return;
        state.reclassifyScheduled = true;
        requestAnimationFrame(() => {
            state.reclassifyScheduled = false;
            reclassifyLayout();
        });
    });

    // Listen for external layout overrides (parent frame integration)
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

    // Announce readiness to parent frame
    try {
        window.parent.postMessage({
            type: 'dashboard-custom-html:ready',
            templateId: `universal-${suite}`
        }, window.location.origin);
    } catch (_) {}

    // Expose render globally
    window.universalDashboard = { render, resizeAllCharts, reclassifyLayout };

    load();
})();
