(function (global) {
    'use strict';

    //global.__csrDashboardRuntimeVersion = 'executive-dashboard.css?v=20260724-polish-3';

    global.__csrDashboardRuntimeVersion = 'executive-dashboard.css?v=20260724-polish-3';

    const CSR_THEME_STORAGE_KEY = 'its-dashboard-csr-theme';

    const THEME_PRESETS = {
        light: {
            palette: [
                '#845EF7', '#00D4FF', '#00E6A8', '#38BDF8', '#FFD166',
                '#4C6FFF', '#B7F34A', '#22D3EE', '#2DD4BF', '#7DD3FC'
            ],
            ink: '#15203b',
            muted: '#64718d',
            axis: '#3f506f',
            grid: 'rgba(68,84,122,.14)',
            border: 'rgba(63,78,121,.18)',
            tooltipBackground: 'rgba(255,255,255,.98)',
            tooltipBorder: 'rgba(92,72,195,.35)',
            tooltipText: '#17203b',
            legend: '#3f4c69',
            legendInactive: '#9aa5b9',
            zoomFill: 'rgba(108,92,231,.18)',
            mapLabel: '#263451',
            mapGlowStart: 'rgba(0,168,184,.16)',
            mapGlowEnd: 'rgba(108,92,231,.035)'
        },
        dark: {
            palette: [
                '#A78BFA', '#22D3EE', '#34D399', '#38BDF8', '#FBBF24',
                '#60A5FA', '#A3E635', '#7DD3FC', '#2DD4BF', '#BAE6FD'
            ],
            ink: '#F5F7FF',
            muted: '#A9B4D0',
            axis: '#B6C1DC',
            grid: 'rgba(168,184,224,.14)',
            border: 'rgba(143,162,224,.20)',
            tooltipBackground: 'rgba(7,10,26,.97)',
            tooltipBorder: 'rgba(34,211,238,.44)',
            tooltipText: '#F8FAFF',
            legend: '#D7DDF0',
            legendInactive: '#68738f',
            zoomFill: 'rgba(167,139,250,.20)',
            mapLabel: '#DCE7FF',
            mapGlowStart: 'rgba(34,211,238,.20)',
            mapGlowEnd: 'rgba(167,139,250,.05)'
        }
    };

    function normalizeTheme(value) {
        return String(value || '').trim().toLowerCase() === 'dark' ? 'dark' : 'light';
    }

    function preferredTheme() {
        try {
            const saved = global.localStorage?.getItem(CSR_THEME_STORAGE_KEY);
            if (saved === 'light' || saved === 'dark') return saved;
        } catch (_) { }

        try {
            return global.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
        } catch (_) {
            return 'light';
        }
    }

    function themeTokens(state) {
        return THEME_PRESETS[normalizeTheme(state?.theme || preferredTheme())];
    }

    const MONTHS = {
        jan: 1, january: 1, feb: 2, february: 2, mar: 3, march: 3,
        apr: 4, april: 4, may: 5, jun: 6, june: 6, jul: 7, july: 7,
        aug: 8, august: 8, sep: 9, sept: 9, september: 9, oct: 10,
        october: 10, nov: 11, november: 11, dec: 12, december: 12
    };

    const FIELD_CACHE = new WeakMap();
    let LEAFLET_PROMISE = null;

    function ensureLeaflet() {
        if (global.L?.map) return Promise.resolve(global.L);
        if (LEAFLET_PROMISE) return LEAFLET_PROMISE;

        LEAFLET_PROMISE = new Promise((resolve, reject) => {
            const cssId = 'csr-leaflet-css';
            if (!document.getElementById(cssId)) {
                const link = document.createElement('link');
                link.id = cssId;
                link.rel = 'stylesheet';
                link.href = 'https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.css';
                document.head.appendChild(link);
            }

            const existing = document.querySelector('script[data-csr-leaflet]');
            if (existing) {
                existing.addEventListener('load', () => global.L?.map ? resolve(global.L) : reject(new Error('Leaflet did not initialize.')), { once: true });
                existing.addEventListener('error', () => reject(new Error('Leaflet failed to load.')), { once: true });
                return;
            }

            const script = document.createElement('script');
            script.src = 'https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.js';
            script.async = true;
            script.dataset.csrLeaflet = '1';
            script.onload = () => global.L?.map ? resolve(global.L) : reject(new Error('Leaflet did not initialize.'));
            script.onerror = () => reject(new Error('Leaflet failed to load.'));
            document.head.appendChild(script);
        }).catch(error => {
            LEAFLET_PROMISE = null;
            throw error;
        });

        return LEAFLET_PROMISE;
    }

    function esc(v) {
        return String(v == null ? '' : v).replace(/[&<>"']/g, m => ({
            '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
        }[m]));
    }

    function norm(v) {
        return String(v == null ? '' : v)
            .trim()
            .toLowerCase()
            .replace(/[\s_\-\.\[\]\(\)%#\/&]+/g, '');
    }

    function cleanSemanticText(value) {
        return String(value == null ? '' : value).trim().replace(/\s+/g, ' ').toLowerCase();
    }

    function bucketIs(value, expected) {
        return cleanSemanticText(value) === cleanSemanticText(expected);
    }

    function daxRound(value, digits) {
        const n = Number(value);
        if (!Number.isFinite(n)) return 0;
        const factor = Math.pow(10, Math.max(0, Number(digits) || 0));
        return Math.round((n + Number.EPSILON) * factor) / factor;
    }

    function daxNumberText(value) {
        const n = Number(value);
        if (!Number.isFinite(n)) return '';
        return n.toFixed(8).replace(/\.?0+$/, '');
    }

    function rowMap(row) {
        if (!row || typeof row !== 'object') return {};
        if (FIELD_CACHE.has(row)) return FIELD_CACHE.get(row);
        const map = {};
        Object.keys(row).forEach(k => {
            map[k] = k;
            map[String(k).toLowerCase()] = k;
            map[norm(k)] = k;
        });
        FIELD_CACHE.set(row, map);
        return map;
    }

    function rawGet(row, names, fallback = null) {
        if (!row || typeof row !== 'object') return fallback;
        const list = Array.isArray(names) ? names : [names];
        const map = rowMap(row);
        for (const name of list) {
            if (name == null || name === '') continue;
            if (Object.prototype.hasOwnProperty.call(row, name)) return row[name];
            const direct = map[String(name).toLowerCase()];
            if (direct != null) return row[direct];
            const cleaned = map[norm(name)];
            if (cleaned != null) return row[cleaned];
        }
        return fallback;
    }

    function num(v, fallback = 0) {
        if (v == null || v === '') return fallback;
        if (typeof v === 'number') return Number.isFinite(v) ? v : fallback;

        let text = String(v)
            .replace(/CA\s*\$/gi, '')
            .replace(/\$/g, '')
            .trim();

        const negativeInParentheses = /^\(.*\)$/.test(text);
        if (negativeInParentheses) text = text.slice(1, -1);

        const n = Number(text.replace(/[,%#\s,]/g, ''));
        if (!Number.isFinite(n)) return fallback;
        return negativeInParentheses ? -Math.abs(n) : n;
    }

    function parseDate(v) {
        if (v == null || v === '') return null;
        if (v instanceof Date && !Number.isNaN(v.getTime())) return v;
        if (typeof v === 'number' && Number.isFinite(v) && v > 1 && v < 80000) {
            const epoch = new Date(Date.UTC(1899, 11, 30));
            const d = new Date(epoch.getTime() + v * 86400000);
            return new Date(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate());
        }
        const s = String(v).trim();
        let m = s.match(/^(\d{4})[-\/]?(\d{1,2})(?:[-\/]?(\d{1,2}))?/);
        if (m) {
            const d = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3] || 1));
            return Number.isNaN(d.getTime()) ? null : d;
        }
        m = s.match(/^([A-Za-z]{3,9})\s+(\d{4})$/);
        if (m) {
            const month = MONTHS[m[1].toLowerCase()];
            if (month) return new Date(Number(m[2]), month - 1, 1);
        }
        const d = new Date(s);
        return Number.isNaN(d.getTime()) ? null : d;
    }

    function monthNumber(v) {
        if (v == null || v === '') return null;
        const n = Number(v);
        if (Number.isFinite(n) && n >= 1 && n <= 12) return n;
        const key = String(v).trim().toLowerCase();
        return MONTHS[key] || MONTHS[key.slice(0, 3)] || null;
    }

    function fmtNumber(v, opts = {}) {
        if (v == null || v === '' || !Number.isFinite(Number(v))) {
            return v == null ? '' : String(v);
        }

        const n = Number(v);
        const abs = Math.abs(n);
        const currency = !!opts.currency;
        const percent = !!opts.percent;

        // Currency fields must show the full number, not K/M/B.
        const compact = !currency && opts.compact !== false;

        let value = n;
        let suffix = '';

        if (compact && !percent) {
            if (abs >= 1e9) {
                value = n / 1e9;
                suffix = 'B';
            } else if (abs >= 1e6) {
                value = n / 1e6;
                suffix = 'M';
            } else if (abs >= 1e3) {
                value = n / 1e3;
                suffix = 'K';
            }
        }

        const decimals = opts.decimals != null
            ? opts.decimals
            : currency
                ? 0
                : percent
                    ? (Math.abs(value - Math.round(value)) < 1e-8 ? 0 : 1)
                    : suffix
                        ? 1
                        : (Math.abs(value - Math.round(value)) < 1e-8 ? 0 : 1);

        const text = value.toLocaleString(undefined, {
            useGrouping: true,
            minimumFractionDigits: Math.max(0, decimals),
            maximumFractionDigits: Math.max(0, decimals)
        });

        // Currency controls formatting only. It does not add a symbol.
        return text + suffix + (percent ? '%' : '');
    }

    function isCurrencyField(name) {
        const s = String(name || '').toLowerCase();
        return /(amount|balance|paid|cost|deposit|arrear|incentive|payment|cash|dollar|\$)/.test(s);
    }

    function isPercentField(name) {
        const s = String(name || '').toLowerCase();
        return /(%|pct|percent|ratio|rate|auc|accuracy|success)/.test(s);
    }

    function formatCell(v, fieldName) {
        if (v == null) return '';

        if (v instanceof Date) {
            return v.toLocaleDateString();
        }

        if (typeof v === 'number') {
            return fmtNumber(v, {
                currency: isCurrencyField(fieldName),
                percent: isPercentField(fieldName)
            });
        }

        const raw = String(v).trim();
        const hadCurrencySymbol = /CA\s*\$|\$/i.test(raw);

        // Remove CA$ and $ only from number values before parsing and rendering.
        const normalized = raw
            .replace(/CA\s*\$/gi, '')
            .replace(/\$/g, '')
            .trim();

        const n = num(normalized, NaN);
        const numericText = /^\s*(?:[-+]?\s*#?\s*[\d,]+(?:\.\d+)?|\(\s*[\d,]+(?:\.\d+)?\s*\))%?\s*$/i.test(normalized);

        if (Number.isFinite(n) && numericText) {
            return fmtNumber(n, {
                currency: isCurrencyField(fieldName) || hadCurrencySymbol,
                percent: isPercentField(fieldName) || normalized.includes('%')
            });
        }

        const d = parseDate(v);

        if (d && /date|time/i.test(String(fieldName || ''))) {
            return d.toLocaleDateString(undefined, {
                year: 'numeric',
                month: 'short',
                day: 'numeric'
            });
        }

        return raw;
    }

    function titleCase(v) {
        return String(v == null ? '' : v)
            .replace(/[_-]+/g, ' ')
            .replace(/\s+/g, ' ')
            .trim()
            .replace(/\b\w/g, c => c.toUpperCase());
    }

    function parseDurationMinutes(v) {
        const s = String(v == null ? '' : v).trim();
        const m = s.match(/^(\d{1,3}):(\d{2}):(\d{2})$/);
        if (!m) return num(v, 0);
        return Number(m[1]) * 60 + Number(m[2]) + Number(m[3]) / 60;
    }

    function aliasesFor(cfg, entity, property) {
        const map = cfg.aliases || {};
        const entityEntry = map[entity] || map[Object.keys(map).find(k => norm(k) === norm(entity))] || {};
        const propKey = Object.keys(entityEntry).find(k => norm(k) === norm(property));
        const values = propKey ? entityEntry[propKey] : [];
        const list = Array.isArray(values) ? values.slice() : (values ? [values] : []);
        list.unshift(property);
        return Array.from(new Set(list.filter(Boolean)));
    }

    function getField(state, row, entity, property) {
        const p = String(property || '');
        const lower = p.toLowerCase();

        /*
         * Recreate the Power Query and model-only columns used by the PBIP report.
         * A physical source value wins whenever it already exists.
         */
        if (lower === 'isebill') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p), null);
            if (direct != null && direct !== '') return direct;
            if (norm(entity) === norm('ns_daily_ebnotes')) return 'EBilling';
        }

        if (lower === 'amount for 31 plus days' || lower === 'amount for 31+ days') {
            const bucket = rawGet(row, aliasesFor(state.cfg, entity, 'AgingBucket'), '');
            const amount = num(rawGet(row, aliasesFor(state.cfg, entity, 'Amount'), 0), 0);
            return bucketIs(bucket, '31-60 Days') || bucketIs(bucket, '61-90 Days') || bucketIs(bucket, '90+ Days')
                ? amount : 0;
        }
        if (lower === 'amount for 90+ days 2') {
            const bucket = rawGet(row, aliasesFor(state.cfg, entity, 'AgingBucket'), '');
            const amount = num(rawGet(row, aliasesFor(state.cfg, entity, 'Amount'), 0), 0);
            return bucketIs(bucket, '90+ Days') ? amount : 0;
        }
        if (lower === 'amount for 61-90 days or 90+ days') {
            const bucket = rawGet(row, aliasesFor(state.cfg, entity, 'AgingBucket'), '');
            const amount = num(rawGet(row, aliasesFor(state.cfg, entity, 'Amount'), 0), 0);
            return bucketIs(bucket, '61-90 Days') || bucketIs(bucket, '90+ Days') ? amount : 0;
        }

        if (lower === 'long call flag') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p), null);
            if (direct != null && direct !== '') return direct;
            const flag = num(rawGet(row, aliasesFor(state.cfg, entity, 'Long Calls').concat(['LongerCalls'])), 0);
            return flag === 1 ? 'Longer Call' : '';
        }
        if (lower === 'duration_time') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p), null);
            if (direct != null && direct !== '') return direct;
            return parseDurationMinutes(rawGet(row, aliasesFor(state.cfg, entity, 'Duration')));
        }
        if (lower === 'metrics') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p), null);
            if (direct != null && direct !== '') return direct;
            const accuracy = daxRound(num(rawGet(row, ['R2']), 0) * 100, 2);
            const leftFive = daxNumberText(accuracy).slice(0, 5);
            const when = rawGet(row, ['RunDateTime'], '');
            return 'Prophet Forecast with Advanced Statistical Modeling: Dynamic Seasonality and Residual Optimization' +
                '   |   Model Accuracy: ' + leftFive + '%' +
                '   |   Last Retrained: ' + String(when == null ? '' : when);
        }
        if (lower === 'bill month key') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p), null);
            if (direct != null && direct !== '') return direct;
            const y = num(rawGet(row, aliasesFor(state.cfg, entity, 'year').concat(['gl_year'])), 0);
            const m = num(rawGet(row, aliasesFor(state.cfg, entity, 'month').concat(['gl_month'])), 0);
            return y && m ? y * 100 + m : null;
        }
        if (lower === 'month-year' || lower === 'year-month') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p), null);
            if (direct != null && direct !== '') return direct;
            const y = num(rawGet(row, aliasesFor(state.cfg, entity, 'year').concat(['gl_year'])), 0);
            const m = num(rawGet(row, aliasesFor(state.cfg, entity, 'month').concat(['gl_month'])), 0);
            return y && m ? `${String(y).padStart(4, '0')}-${String(m).padStart(2, '0')}` : null;
        }
        if (lower === 'y') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p).concat(['year']), null);
            if (direct != null && direct !== '') return direct;
        }
        if (lower === 'm') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p).concat(['month_abbr', 'month_name', 'month']), null);
            if (direct != null && direct !== '') return direct;
        }
        if (lower === 'd') {
            const direct = rawGet(row, aliasesFor(state.cfg, entity, p).concat(['date', 'report_date', 'createdon']), null);
            if (direct != null && direct !== '') return direct;
        }

        return rawGet(row, aliasesFor(state.cfg, entity, p), null);
    }

    function getAnyField(state, row, field, preferredEntity) {
        if (preferredEntity) {
            const v = getField(state, row, preferredEntity, field);
            if (v != null && v !== '') return v;
        }
        const direct = rawGet(row, [field], null);
        if (direct != null && direct !== '') return direct;
        for (const entity of Object.keys(state.cfg.aliases || {})) {
            const v = getField(state, row, entity, field);
            if (v != null && v !== '') return v;
        }
        return null;
    }

    function datasetFor(state, entity) {
        const activeVisualId = String(state?.activeVisualId || '');
        const visualRows = activeVisualId && state?.visualDataSets
            ? state.visualDataSets[activeVisualId]
            : null;
        if (Array.isArray(visualRows)) return visualRows;

        const sets = state.dataSets || {};
        if (sets[entity]) return sets[entity];
        const key = Object.keys(sets).find(k => norm(k) === norm(entity));
        if (key) return sets[key];
        if (Object.keys(sets).length === 1) return sets[Object.keys(sets)[0]];
        return [];
    }

    function sourceMetaList(state) {
        const meta = state?.sourceMeta;
        if (Array.isArray(meta)) return meta;
        if (meta && typeof meta === 'object') return Object.values(meta);
        return [];
    }

    function sourceMetaForEntity(state, entity) {
        const target = norm(entity);
        return sourceMetaList(state).find(item =>
            norm(item?.alias) === target || norm(item?.semanticEntity) === target
        ) || null;
    }

    function sourceEmptyMarkup(state, visual, fallbackText) {
        const entity = mainEntity(visual);
        const meta = sourceMetaForEntity(state, entity);
        if (meta?.error) {
            const sourceName = [meta.sourceServer, meta.sourceDatabase, meta.schema, meta.object]
                .filter(Boolean).join(' · ');
            return `<div class="csr-error"><div><strong>PBIP source failed</strong>${sourceName ? `<br><span>${esc(sourceName)}</span>` : ''}<br>${esc(meta.error)}</div></div>`;
        }
        if (meta && Number(meta.returnedRows || 0) === 0) {
            const sourceName = [meta.sourceServer, meta.sourceDatabase, meta.schema, meta.object]
                .filter(Boolean).join(' · ');
            return `<div class="csr-empty"><div><strong>Source returned 0 rows</strong>${sourceName ? `<br><span>${esc(sourceName)}</span>` : ''}</div></div>`;
        }
        if (state?.payloadError) {
            return `<div class="csr-error"><div><strong>Dashboard source failed</strong><br>${esc(state.payloadError)}</div></div>`;
        }
        return `<div class="csr-empty">${esc(fallbackText || 'No matching data')}</div>`;
    }

    function parseFilterValue(v) {
        if (v == null) return null;
        const s = String(v).trim();
        const dt = s.match(/^datetime'(.+)'$/i);
        if (dt) return parseDate(dt[1]);
        const n = Number(s);
        if (Number.isFinite(n) && s !== '') return n;
        const d = parseDate(s);
        if (d && /[-\/]|\d{4}/.test(s)) return d;
        return s;
    }

    function compareValues(a, b) {
        if (a instanceof Date || b instanceof Date) {
            const ad = a instanceof Date ? a : parseDate(a);
            const bd = b instanceof Date ? b : parseDate(b);
            if (ad && bd) return ad.getTime() - bd.getTime();
        }
        const an = num(a, NaN), bn = num(b, NaN);
        if (Number.isFinite(an) && Number.isFinite(bn)) return an - bn;
        return String(a == null ? '' : a).localeCompare(String(b == null ? '' : b), undefined, { numeric: true, sensitivity: 'base' });
    }

    function sameSemanticValue(a, b, fieldName) {
        if (a == null || b == null) return false;
        if (/month/i.test(String(fieldName || ''))) {
            const am = monthNumber(a), bm = monthNumber(b);
            if (am && bm) return am === bm;
        }
        return compareValues(a, b) === 0;
    }

    function passFilter(state, row, filter, entity) {
        const value = getAnyField(state, row, filter.field, entity);
        const op = String(filter.op || 'eq').toLowerCase();
        if (op === 'notnull') return value != null && value !== '';
        if (op === 'null') return value == null || value === '';
        const parsed = filter.value != null ? parseFilterValue(filter.value) : null;
        const values = Array.isArray(filter.values) ? filter.values.map(parseFilterValue) : [];
        if (op === 'in') return values.some(x => sameSemanticValue(value, x, filter.field));
        if (op === 'notin') return !values.some(x => sameSemanticValue(value, x, filter.field));
        if (op === 'eq') return sameSemanticValue(value, parsed, filter.field);
        if (op === 'neq') return !sameSemanticValue(value, parsed, filter.field);
        const cmp = compareValues(value, parsed);
        if (op === 'gt') return cmp > 0;
        if (op === 'gte') return cmp >= 0;
        if (op === 'lt') return cmp < 0;
        if (op === 'lte') return cmp <= 0;
        return true;
    }

    function selectedValues(selection) {
        if (Array.isArray(selection?.values)) return selection.values.filter(v => v != null && v !== '');
        if (selection?.value == null || selection.value === '') return [];
        return [selection.value];
    }

    function relationshipSelection(state, selection, targetEntity) {
        const relationships = Array.isArray(state.cfg.relationships) ? state.cfg.relationships : [];
        const sourceEntity = String(selection.entity || '');
        const target = String(targetEntity || '');
        const selected = selectedValues(selection);
        if (!sourceEntity || !target || !selected.length || norm(sourceEntity) === norm(target)) return null;

        for (const rel of relationships) {
            if (rel && rel.active === false) continue;
            let sourceKey = '', targetKey = '';
            if (norm(rel.fromEntity) === norm(sourceEntity) && norm(rel.toEntity) === norm(target)) {
                sourceKey = rel.fromField; targetKey = rel.toField;
            } else if (rel.bothDirections !== false && norm(rel.toEntity) === norm(sourceEntity) && norm(rel.fromEntity) === norm(target)) {
                sourceKey = rel.toField; targetKey = rel.fromField;
            } else {
                continue;
            }

            const cacheKey = [sourceEntity, selection.field, selected.map(String).sort().join('~'), target, sourceKey, targetKey].join('|');
            if (!state.relationshipCache.has(cacheKey)) {
                const sourceRows = datasetFor(state, sourceEntity);
                const allowed = new Set();
                for (const sourceRow of sourceRows) {
                    const selectedValue = getAnyField(state, sourceRow, selection.field, sourceEntity);
                    if (!selected.some(value => sameSemanticValue(selectedValue, value, selection.field))) continue;
                    const keyValue = getField(state, sourceRow, sourceEntity, sourceKey);
                    if (keyValue != null && keyValue !== '') allowed.add(String(keyValue));
                }
                state.relationshipCache.set(cacheKey, { targetKey, allowed });
            }
            return state.relationshipCache.get(cacheKey);
        }
        return null;
    }

    function selectionAllowsRow(state, row, selection, entity) {
        const selected = selectedValues(selection);
        if (!selected.length) return true;
        const direct = getAnyField(state, row, selection.field, entity);
        if (direct != null && direct !== '') {
            return selected.some(value => sameSemanticValue(direct, value, selection.field));
        }

        const related = relationshipSelection(state, selection, entity);
        if (!related) return true;
        const targetValue = getField(state, row, entity, related.targetKey);
        return targetValue != null && related.allowed.has(String(targetValue));
    }

    function applyFiltersWithList(state, rows, visual, entity, filters) {
        let out = Array.isArray(rows) ? rows.slice() : [];
        for (const filter of filters || []) {
            out = out.filter(row => passFilter(state, row, filter, entity));
        }
        for (const selection of Object.values(state.slicerSelections)) {
            if (!selectedValues(selection).length) continue;
            out = out.filter(row => selectionAllowsRow(state, row, selection, entity));
        }
        return out;
    }

    function applyFilters(state, rows, visual, entity) {
        const activeVisualId = String(state?.activeVisualId || '');
        const hasServerVisualRows = state?.serverFilteredVisualData === true &&
            activeVisualId && Array.isArray(state?.visualDataSets?.[activeVisualId]);

        // Monthly EBNotes rows are already filtered and aggregated in SQL for the
        // active visual. Reapplying PBIP filters in the browser removes slicer/table
        // rows because those result sets intentionally contain only their output
        // fields, not every source column used by the original PBIP filter context.
        if (hasServerVisualRows) return Array.isArray(rows) ? rows.slice() : [];

        return applyFiltersWithList(state, rows, visual, entity, visual.filters || []);
    }

    const PBIP_MEASURE_FILTERS = new Set([
        norm('Amount for 31 Plus Days'),
        norm('Amount for 31+ Days'),
        norm('Amount for 90+ Days 2'),
        norm('Amount for 61-90 Days or 90+ Days'),
        norm('Paid Ratio'),
        norm('Metrics_Accuracy'),
        norm('Metrics_Accuracy_Resid'),
        norm('Metrics_ROC'),
        norm('E-Bill %')
    ]);

    function isMeasureFilter(filter, specs) {
        const fieldKey = norm(filter?.field);
        if (!fieldKey) return false;
        if (PBIP_MEASURE_FILTERS.has(fieldKey)) return true;
        return (specs || []).some(spec => spec.kind === 'measure' && norm(spec.property) === fieldKey);
    }

    function passAggregateFilter(state, rows, filter, entity) {
        const actual = measureValue(state, rows, {
            kind: 'measure',
            entity,
            property: filter.field,
            measure: filter.field,
            agg: 'measure'
        }, null);
        const op = String(filter.op || 'eq').toLowerCase();
        if (op === 'notnull') return actual != null && actual !== '';
        if (op === 'null') return actual == null || actual === '';
        const parsed = filter.value != null ? parseFilterValue(filter.value) : null;
        const values = Array.isArray(filter.values) ? filter.values.map(parseFilterValue) : [];
        if (op === 'in') return values.some(x => sameSemanticValue(actual, x, filter.field));
        if (op === 'notin') return !values.some(x => sameSemanticValue(actual, x, filter.field));
        if (op === 'eq') return sameSemanticValue(actual, parsed, filter.field);
        if (op === 'neq') return !sameSemanticValue(actual, parsed, filter.field);
        const cmp = compareValues(actual, parsed);
        if (op === 'gt') return cmp > 0;
        if (op === 'gte') return cmp >= 0;
        if (op === 'lt') return cmp < 0;
        if (op === 'lte') return cmp <= 0;
        return true;
    }

    function categoryInfo(state, row, specs) {
        const fields = Array.isArray(specs) ? specs : [];
        if (!fields.length) return { label: '(all)', key: '(all)', sort: '(all)' };
        const parts = fields.map(s => ({ spec: s, value: getField(state, row, s.entity, s.property) }));
        const yearPart = parts.find(x => /^(year|y)$/i.test(x.spec.property));
        const monthPart = parts.find(x => /^month(_name)?$/i.test(x.spec.property) || /^m$/i.test(x.spec.property));
        const datePart = parts.find(x => /^(d)$/i.test(x.spec.property) || /date|selecteddate|reportdate|trans_date|createdon|timeSort/i.test(x.spec.property));

        let d = datePart ? parseDate(datePart.value) : null;
        if (!d && yearPart && monthPart) {
            const month = monthNumber(monthPart.value);
            if (month) d = new Date(num(yearPart.value, 0), month - 1, 1);
        }
        if (!d && parts.length === 1) d = parseDate(parts[0].value);

        if (d) {
            const hasDay = !!datePart && fields.length >= 3;
            const label = d.toLocaleDateString(undefined, hasDay
                ? { month: 'short', day: 'numeric', year: 'numeric' }
                : { month: 'short', year: 'numeric' });
            const sort = d.getFullYear() * 10000 + (d.getMonth() + 1) * 100 + (hasDay ? d.getDate() : 1);
            return { label, key: String(sort), sort, date: d };
        }

        const values = parts.map(x => x.value == null || x.value === '' ? '(blank)' : String(x.value));
        const label = values.join(' · ');
        return { label, key: label, sort: sortRank(label) };
    }

    function sortRank(v) {
        const s = String(v == null ? '' : v).toLowerCase();
        const bucketOrder = [
            'current', '0-30', '1-30', '31-60', '61-90', '61-120', '90+', '120+',
            'residential', 'small commercial', 'large commercial', 'commercial', 'water'
        ];
        const ix = bucketOrder.findIndex(x => s.includes(x));
        if (ix >= 0) return ix;
        const month = monthNumber(s);
        if (month) return month;
        const n = num(s, NaN);
        return Number.isFinite(n) ? n : s;
    }

    function aggregateColumn(state, rows, spec) {
        const values = rows.map(r => getField(state, r, spec.entity, spec.property)).filter(v => v != null && v !== '');
        const agg = String(spec.agg || 'none').toLowerCase();
        if (agg === 'count') return values.length;
        if (agg === 'countdistinct' || agg === 'distinctcount') return new Set(values.map(v => String(v))).size;
        if (agg === 'min') return values.length ? values.slice().sort(compareValues)[0] : null;
        if (agg === 'max') return values.length ? values.slice().sort(compareValues).at(-1) : null;
        if (agg === 'avg' || agg === 'average') {
            const ns = values.map(v => num(v, NaN)).filter(Number.isFinite);
            return ns.length ? ns.reduce((a, b) => a + b, 0) / ns.length : null;
        }
        if (agg === 'sum') return values.reduce((a, v) => a + num(v, 0), 0);
        if (values.length === 1) return values[0];
        const ns = values.map(v => num(v, NaN)).filter(Number.isFinite);
        if (ns.length === values.length && ns.length) return ns.reduce((a, b) => a + b, 0);
        return values[0] ?? null;
    }

    function measureValue(state, rows, spec, category) {
        const name = String(spec.property || spec.measure || '').trim();
        const key = norm(name);
        const entity = spec.entity;

        if (key === norm('Amount for 31 Plus Days') || key === norm('Amount for 31+ Days')) {
            return rows.reduce((sum, row) => {
                const bucket = getField(state, row, entity, 'AgingBucket');
                const include = bucketIs(bucket, '31-60 Days') || bucketIs(bucket, '61-90 Days') || bucketIs(bucket, '90+ Days');
                return include ? sum + num(getField(state, row, entity, 'Amount'), 0) : sum;
            }, 0);
        }
        if (key === norm('Amount for 90+ Days 2')) {
            return rows.reduce((sum, row) => bucketIs(getField(state, row, entity, 'AgingBucket'), '90+ Days')
                ? sum + num(getField(state, row, entity, 'Amount'), 0) : sum, 0);
        }
        if (key === norm('Amount for 61-90 Days or 90+ Days')) {
            return rows.reduce((sum, row) => {
                const bucket = getField(state, row, entity, 'AgingBucket');
                return bucketIs(bucket, '61-90 Days') || bucketIs(bucket, '90+ Days')
                    ? sum + num(getField(state, row, entity, 'Amount'), 0) : sum;
            }, 0);
        }
        if (key === norm('Paid Ratio')) {
            const paid = rows.reduce((sum, row) => sum + num(getField(state, row, entity, 'Post Paid'), 0), 0);
            const balance = rows.reduce((sum, row) => sum + num(getField(state, row, entity, 'Balance'), 0), 0);
            // DAX returns a ratio. The HTML formatter consumes display-scale percentages.
            return balance ? (-paid / balance) * 100 : 0;
        }
        if (key === norm('Metrics_Accuracy')) {
            const all = datasetFor(state, entity);
            const values = all
                .filter(row => cleanSemanticText(getField(state, row, entity, 'metric_name')) === 'roc_auc')
                .map(row => num(getField(state, row, entity, 'metric_value'), NaN))
                .filter(Number.isFinite);
            const percentage = daxRound(values.length ? Math.max(...values) : 0, 4) * 100;
            return 'Optimized XGBoost Ensemble Classification Model with SHAP Interpretability for Imbalanced Datasets' +
                '   |   Overall Model ROC AUC: ' + daxNumberText(percentage) + '%';
        }
        if (key === norm('Metrics_Accuracy_Resid')) {
            const all = datasetFor(state, entity);
            const values = all
                .filter(row => cleanSemanticText(getField(state, row, entity, 'metric_name')) === 'f1_score')
                .map(row => num(getField(state, row, entity, 'metric_value'), NaN))
                .filter(Number.isFinite);
            const percentage = daxRound(values.length ? Math.max(...values) : 0, 4) * 100;
            return 'Optimized XGBoost Ensemble Classification Model with SHAP Interpretability for Imbalanced Datasets' +
                '   |   Overall Model Success Rate: ' + daxNumberText(percentage) + '%';
        }
        if (key === norm('Metrics_ROC')) {
            const all = datasetFor(state, entity);
            const values = all
                .filter(row => cleanSemanticText(getField(state, row, entity, 'metric_name')) === 'roc_auc')
                .map(row => num(getField(state, row, entity, 'metric_value'), NaN))
                .filter(Number.isFinite);
            const percentage = daxRound(values.length ? Math.max(...values) : 0, 4) * 100;
            return 'ROC_AUC Precision: ' + daxNumberText(percentage) + '%';
        }
        if (key === norm('E-Bill %')) {
            // The Monthly EBNotes server path returns the PBI-equivalent percentage
            // directly on each aggregated row. Prefer that value and only use the
            // legacy raw-row calculation for unconverted CSR payloads.
            const direct = rows
                .map(row => getField(state, row, entity || 'ns_daily_ebnotes', 'E-Bill %'))
                .map(value => num(value, NaN))
                .filter(Number.isFinite);
            if (direct.length) return Math.max(...direct);

            const notes = datasetFor(state, 'ns_daily_ebnotes');
            const bills = datasetFor(state, 'ns_total_bills_monthly');
            let year = null;
            let month = null;

            if (category?.date) {
                year = category.date.getFullYear();
                month = category.date.getMonth() + 1;
            }
            if ((!year || !month) && rows.length) {
                year = num(getField(state, rows[0], 'ns_daily_ebnotes', 'year'), 0);
                month = num(getField(state, rows[0], 'ns_daily_ebnotes', 'month'), 0);
            }
            if (!year || !month) return null;

            // Exact calculated-table grain: one row per year/month/AccountID.
            const accountMonthRows = new Set(notes.filter(row =>
                num(getField(state, row, 'ns_daily_ebnotes', 'year'), 0) === year &&
                num(getField(state, row, 'ns_daily_ebnotes', 'month'), 0) === month
            ).map(row => [
                year,
                month,
                String(getField(state, row, 'ns_daily_ebnotes', 'AccountID') || '')
            ].join('|')).filter(keyText => !keyText.endsWith('|'))).size;

            const totalMonthlyBills = bills.filter(row => {
                const rowYear = num(getField(state, row, 'ns_total_bills_monthly', 'gl_year') ?? getField(state, row, 'ns_total_bills_monthly', 'year'), 0);
                const rowMonth = num(getField(state, row, 'ns_total_bills_monthly', 'gl_month') ?? getField(state, row, 'ns_total_bills_monthly', 'month'), 0);
                return rowYear === year && rowMonth === month;
            }).reduce((sum, row) => sum + num(getField(state, row, 'ns_total_bills_monthly', 'bills'), 0), 0);

            return totalMonthlyBills ? 100 * accountMonthRows / totalMonthlyBills : 0;
        }
        if (key === norm('Duration_Time average per Long Call Flag')) {
            const values = rows
                .map(row => parseDurationMinutes(getField(state, row, entity, 'Duration')))
                .filter(Number.isFinite);
            return values.length ? values.reduce((a, b) => a + b, 0) / values.length : 0;
        }

        return aggregateColumn(state, rows, { ...spec, agg: spec.agg === 'measure' ? 'sum' : spec.agg });
    }

    function aggregateValue(state, rows, spec, category) {
        return spec.kind === 'measure' ? measureValue(state, rows, spec, category) : aggregateColumn(state, rows, spec);
    }

    function mainEntity(visual) {
        const roles = visual.roles || {};
        for (const role of Object.keys(roles)) {
            const f = (roles[role] || [])[0];
            if (f && f.entity) return f.entity;
        }
        return (visual.filters || [])[0]?.entity || '';
    }

    function getRoles(visual, name) {
        return Array.isArray(visual.roles?.[name]) ? visual.roles[name] : [];
    }

    function isRedundantFlowVisual(visual) {
        const type = String(visual?.type || '');
        const text = String(visual?.text || visual?.title || '').trim();
        return /^FlowVisual_/i.test(type) || /^\+?snap$/i.test(text);
    }

    function effectiveVisualType(visual) {
        const declared = String(visual?.type || '');
        if (declared === 'map') return 'map';
        const roleFields = Object.values(visual?.roles || {}).flat();
        const names = roleFields.map(spec => norm(spec?.property || spec?.label || ''));
        const hasLatitude = names.some(name => name === 'latitude' || name === 'lat');
        const hasLongitude = names.some(name => name === 'longitude' || name === 'longtitude' || name === 'lon' || name === 'lng');
        if (hasLatitude && hasLongitude) return 'map';
        return declared;
    }

    function groupRows(state, rows, categorySpecs, seriesSpec) {
        const map = new Map();
        for (const row of rows) {
            const cat = categoryInfo(state, row, categorySpecs);
            const series = seriesSpec ? String(getField(state, row, seriesSpec.entity, seriesSpec.property) ?? '(blank)') : '';
            const key = cat.key + '||' + series;
            if (!map.has(key)) map.set(key, { category: cat, series, rows: [] });
            map.get(key).rows.push(row);
        }
        return Array.from(map.values());
    }

    function formatAxisLabel(label) {
        const d = parseDate(label);
        if (d && /\d{4}/.test(String(label))) {
            return d.toLocaleDateString(undefined, { month: 'short', year: 'numeric' });
        }
        return String(label == null ? '' : label);
    }

    function clampNumber(value, min, max) {
        const n = Number(value);
        return Math.max(min, Math.min(max, Number.isFinite(n) ? n : min));
    }

    function wrapCategoryLabel(value, maxChars = 12, maxLines = 3, forceWordLines = false) {
        const text = String(value == null ? '' : value).replace(/\s+/g, ' ').trim();
        if (!text) return '';

        const words = text.split(' ').filter(Boolean);

        // Compact CSR tiles need deterministic labels.  Do not let the calculated
        // character threshold keep multi-word labels such as "Small Commercial"
        // on one line; one word per line is clearer and preserves bar height.
        if (forceWordLines && words.length > 1) {
            if (words.length <= maxLines) return words.join('\n');
            const head = words.slice(0, maxLines - 1);
            head.push(words.slice(maxLines - 1).join(' '));
            return head.join('\n');
        }

        if (text.length <= maxChars) return text;

        const lines = [];
        let line = '';
        const push = candidate => {
            if (!candidate || lines.length >= maxLines) return;
            lines.push(candidate);
        };

        for (let i = 0; i < words.length && lines.length < maxLines; i++) {
            let word = words[i];
            while (word.length > maxChars && lines.length < maxLines) {
                if (line) {
                    push(line);
                    line = '';
                    if (lines.length >= maxLines) break;
                }
                push(word.slice(0, maxChars));
                word = word.slice(maxChars);
            }
            if (lines.length >= maxLines) break;
            if (!word) continue;

            const next = line ? `${line} ${word}` : word;
            if (next.length <= maxChars) line = next;
            else {
                push(line);
                line = word;
            }
        }

        if (line && lines.length < maxLines) push(line);
        if (!lines.length) lines.push(text.slice(0, maxChars));

        const represented = lines.join(' ').replace(/…$/, '');
        if (represented.length < text.length) {
            const last = lines.length - 1;
            lines[last] = lines[last].slice(0, Math.max(1, maxChars - 1)).replace(/\s+$/, '') + '…';
        }
        return lines.join('\n');
    }

    function chartSizeHint(state, visual, host) {
        const rect = host?.getBoundingClientRect?.();
        const liveWidth = Number(rect?.width || host?.clientWidth || 0);
        const liveHeight = Number(rect?.height || host?.clientHeight || 0);
        if (liveWidth > 20 && liveHeight > 20) return { width: liveWidth, height: liveHeight };

        const section = host?.closest?.('.csr-visual');
        const sectionRect = section?.getBoundingClientRect?.();
        if (Number(sectionRect?.width) > 20 && Number(sectionRect?.height) > 20) {
            return { width: sectionRect.width, height: sectionRect.height };
        }

        const pageWidth = Math.max(320, Number(state?.cfg?.width) || 1280);
        const pageHeight = Math.max(240, Number(state?.cfg?.height) || 720);
        const layout = state?.visualLayoutOverrides?.[String(visual?.id || '')] || visual?.position || {};
        return {
            width: pageWidth * clampNumber(layout?.w, 1, 100) / 100,
            height: pageHeight * clampNumber(layout?.h, 1, 100) / 100
        };
    }

    function normalizeChartKind(value) {
        const type = String(value || '').trim().toLowerCase();
        if (type === 'linestackedcolumncombochart') return 'stacked-combo';
        if (type === 'stackedcolumnchart') return 'stacked-column';
        if (type === 'stackedbarchart') return 'stacked-horizontal-bar';
        if (type === 'barchart') return 'horizontal-bar';
        if (type === 'linechart') return 'line';
        if (type === 'piechart' || type === 'pie') return 'pie';
        if (type === 'donutchart' || type === 'donut') return 'donut';
        return 'column';
    }

    function chartKindForVisual(visual, requestedKind) {
        return requestedKind || normalizeChartKind(effectiveVisualType(visual));
    }

    function axisSeriesValues(series, axisIndex) {
        const selected = (series || []).filter(item => Number(item?.yAxisIndex || 0) === axisIndex);
        const values = [];
        const stacked = new Map();

        selected.forEach((item, seriesIndex) => {
            const data = Array.isArray(item?.data) ? item.data : [];
            if (!item?.stack) {
                data.forEach(value => {
                    const n = Number(value);
                    if (Number.isFinite(n)) values.push(n);
                });
                return;
            }

            const stackKey = String(item.stack);
            if (!stacked.has(stackKey)) stacked.set(stackKey, { positive: [], negative: [] });
            const totals = stacked.get(stackKey);
            data.forEach((value, index) => {
                const n = Number(value);
                if (!Number.isFinite(n)) return;
                if (n >= 0) totals.positive[index] = Number(totals.positive[index] || 0) + n;
                else totals.negative[index] = Number(totals.negative[index] || 0) + n;
            });
        });

        stacked.forEach(totals => {
            totals.positive.forEach(value => { if (Number.isFinite(value)) values.push(value); });
            totals.negative.forEach(value => { if (Number.isFinite(value)) values.push(value); });
        });
        return values;
    }

    function niceCeiling(value) {
        const n = Number(value);
        if (!Number.isFinite(n) || n <= 0) return 1;
        const exponent = Math.floor(Math.log10(n));
        const scale = Math.pow(10, exponent);
        const fraction = n / scale;
        const step = fraction <= 1 ? 1
            : fraction <= 1.2 ? 1.2
                : fraction <= 1.5 ? 1.5
                    : fraction <= 2 ? 2
                        : fraction <= 2.5 ? 2.5
                            : fraction <= 3 ? 3
                                : fraction <= 4 ? 4
                                    : fraction <= 5 ? 5
                                        : fraction <= 6 ? 6
                                            : fraction <= 8 ? 8
                                                : 10;
        return step * scale;
    }

    function numericAxisExtent(series, axisIndex, includeZero) {
        const values = axisSeriesValues(series, axisIndex);
        if (!values.length) return { min: 0, max: 1 };

        let min = Math.min(...values);
        let max = Math.max(...values);
        if (includeZero) {
            min = Math.min(0, min);
            max = Math.max(0, max);
        }

        if (min === max) {
            if (min === 0) return { min: 0, max: 1 };
            const pad = Math.max(Math.abs(min) * .12, 1);
            return includeZero
                ? (min > 0 ? { min: 0, max: niceCeiling(min + pad) } : { min: -niceCeiling(Math.abs(min - pad)), max: 0 })
                : { min: min - pad, max: max + pad };
        }

        if (includeZero && min >= 0) return { min: 0, max: niceCeiling(max * 1.08) };
        if (includeZero && max <= 0) return { min: -niceCeiling(Math.abs(min) * 1.08), max: 0 };

        const span = Math.max(max - min, 1);
        const pad = span * .08;
        return { min: min - pad, max: max + pad };
    }

    function chartTitle(visual) {
        return visual.title || '';
    }

    function sourceRowsForVisual(state, visual) {
        const entity = mainEntity(visual);
        return { entity, rows: applyFilters(state, datasetFor(state, entity), visual, entity) };
    }

    function chartPalette(state) {
        const theme = normalizeTheme(state?.theme);
        const explicit = theme === 'dark' ? state?.cfg?.paletteDark : state?.cfg?.paletteLight;
        if (Array.isArray(explicit) && explicit.length) return explicit;
        return themeTokens(state).palette;
    }

    function gradient(color) {
        if (!global.echarts) return color;
        return new echarts.graphic.LinearGradient(0, 0, 0, 1, [
            { offset: 0, color },
            { offset: 1, color: color + '66' }
        ]);
    }

    function standardToolbox(state, orientation = 'x') {
        const tokens = themeTokens(state);
        const zoomAxis = orientation === 'y'
            ? { xAxisIndex: 'none', yAxisIndex: 0 }
            : { xAxisIndex: 0, yAxisIndex: 'none' };
        return {
            show: false,
            right: 30,
            top: 1,
            itemSize: 11,
            itemGap: 5,
            padding: [4, 6],
            backgroundColor: normalizeTheme(state?.theme) === 'dark' ? 'rgba(10,15,30,.76)' : 'rgba(255,255,255,.76)',
            borderColor: tokens.border,
            borderWidth: 1,
            borderRadius: 8,
            iconStyle: { borderColor: tokens.axis },
            emphasis: { iconStyle: { borderColor: chartPalette(state)[0] } },
            feature: {
                dataZoom: { ...zoomAxis, title: { zoom: 'Zoom', back: 'Back' } },
                restore: { title: 'Reset' },
                saveAsImage: { title: 'Save image', pixelRatio: 2, backgroundColor: 'transparent' }
            }
        };
    }

    function bindChartHoverTools(container, chart) {
        if (!container || !chart) return;
        const prior = container._csrChartToolBinding;
        if (prior) {
            container.removeEventListener('mouseenter', prior.show);
            container.removeEventListener('focusin', prior.show);
            container.removeEventListener('mouseleave', prior.hide);
            container.removeEventListener('focusout', prior.hide);
            if (prior.timer) global.clearTimeout(prior.timer);
        }
        const binding = { chart, timer: null };
        binding.show = () => {
            if (binding.timer) global.clearTimeout(binding.timer);
            try { chart.setOption({ toolbox: { show: true } }, { lazyUpdate: true }); } catch (_) { }
        };
        binding.hide = () => {
            if (binding.timer) global.clearTimeout(binding.timer);
            binding.timer = global.setTimeout(() => {
                if (container.matches(':hover') || container.contains(document.activeElement)) return;
                try { chart.setOption({ toolbox: { show: false } }, { lazyUpdate: true }); } catch (_) { }
            }, 70);
        };
        container._csrChartToolBinding = binding;
        container.addEventListener('mouseenter', binding.show);
        container.addEventListener('focusin', binding.show);
        container.addEventListener('mouseleave', binding.hide);
        container.addEventListener('focusout', binding.hide);
    }

    function resizeInteractiveSoon(state) {
        global.requestAnimationFrame(() => {
            state.charts?.forEach(chart => { try { chart.resize(); } catch (_) { } });
            state.maps?.forEach(map => { try { map.invalidateSize(false); } catch (_) { } });
        });
    }

    function chartSeriesModel(state, visual, chartKind) {
        const { entity, rows } = sourceRowsForVisual(state, visual);
        const categorySpecs = getRoles(visual, 'Category').concat(getRoles(visual, 'X'));
        const seriesSpec = getRoles(visual, 'Series')[0] || null;
        const ySpecs = getRoles(visual, 'Y');
        const y2Specs = getRoles(visual, 'Y2');
        const groups = groupRows(state, rows, categorySpecs, seriesSpec);
        const categoriesMap = new Map();
        groups.forEach(group => categoriesMap.set(group.category.key, group.category));
        const categories = Array.from(categoriesMap.values()).sort((a, b) => compareValues(a.sort, b.sort));
        const xLabels = categories.map(category => formatAxisLabel(category.label));
        const palette = chartPalette(state);
        const series = [];
        const barStack = chartKind === 'stacked-combo' || chartKind === 'stacked-column' || chartKind === 'stacked-horizontal-bar'
            ? 'csr-primary-stack'
            : undefined;
        const primaryIsLine = chartKind === 'line';

        const addSeries = (name, data, options) => {
            const index = series.length;
            const color = palette[index % palette.length];
            const line = options.type === 'line';
            series.push({
                name,
                type: options.type,
                stack: options.stack,
                yAxisIndex: options.axis || 0,
                smooth: line ? .28 : undefined,
                showSymbol: line,
                symbolSize: line ? 5 : undefined,
                connectNulls: line,
                data,
                itemStyle: {
                    color: line ? color : gradient(color),
                    borderRadius: line ? 0 : [4, 4, 1, 1],
                    shadowBlur: line ? 0 : 4,
                    shadowColor: line ? 'transparent' : color + '44'
                },
                lineStyle: line ? { width: 2, color } : undefined,
                areaStyle: chartKind === 'line' && series.length === 0 ? { color: color + '12' } : undefined
            });
        };

        // One primary-series construction path for both clustered multi-series
        // columns and ordinary single-series columns. The prior separate no-Series
        // branch let single-series visuals drift from Category Balance by Aging.
        const seriesValues = seriesSpec
            ? Array.from(new Set(groups.map(group => group.series)))
                .sort((a, b) => compareValues(sortRank(a), sortRank(b)))
            : [null];

        ySpecs.forEach(spec => {
            seriesValues.forEach(seriesName => {
                const data = categories.map(category => {
                    const matching = groups.filter(group =>
                        group.category.key === category.key &&
                        (!seriesSpec || group.series === seriesName)
                    );
                    return aggregateValue(state, matching.flatMap(group => group.rows), spec, category);
                });

                const label = seriesSpec
                    ? (ySpecs.length === 1
                        ? String(seriesName ?? '')
                        : `${String(seriesName ?? '')} · ${spec.label || spec.property}`)
                    : (spec.label || spec.property);

                addSeries(label, data, {
                    type: primaryIsLine ? 'line' : 'bar',
                    stack: primaryIsLine ? undefined : barStack,
                    axis: 0
                });
            });
        });

        y2Specs.forEach(spec => {
            const data = categories.map(category => {
                const matching = groups.filter(group => group.category.key === category.key);
                return aggregateValue(state, matching.flatMap(group => group.rows), spec, category);
            });
            addSeries(spec.label || spec.property, data, { type: 'line', axis: 1 });
        });

        return { entity, rows, categorySpecs, seriesSpec, ySpecs, y2Specs, categories, xLabels, series };
    }

    function buildPieOption(state, visual, host, chartKind) {
        const { entity, rows } = sourceRowsForVisual(state, visual);
        const categorySpecs = getRoles(visual, 'Category')
            .concat(getRoles(visual, 'Legend'))
            .concat(getRoles(visual, 'Details'))
            .concat(getRoles(visual, 'X'));
        const valueSpec = getRoles(visual, 'Values')[0] || getRoles(visual, 'Y')[0] || getRoles(visual, 'Size')[0];
        const groups = groupRows(state, rows, categorySpecs, null);
        const categoriesMap = new Map();
        groups.forEach(group => categoriesMap.set(group.category.key, group.category));
        const categories = Array.from(categoriesMap.values()).sort((a, b) => compareValues(a.sort, b.sort));
        const data = valueSpec ? categories.map(category => {
            const matching = groups.filter(group => group.category.key === category.key);
            return {
                name: formatAxisLabel(category.label),
                value: aggregateValue(state, matching.flatMap(group => group.rows), valueSpec, category)
            };
        }) : [];
        const tokens = themeTokens(state);
        const palette = chartPalette(state);
        const size = chartSizeHint(state, visual, host);
        const compact = size.width < 300 || size.height < 190;
        const option = {
            animationDuration: 520,
            color: palette,
            tooltip: {
                trigger: 'item',
                confine: true,
                backgroundColor: tokens.tooltipBackground,
                borderColor: tokens.tooltipBorder,
                textStyle: { color: tokens.tooltipText, fontSize: 10 },
                formatter: params => `${esc(params.name)}<br><strong>${fmtNumber(params.value, { currency: isCurrencyField(valueSpec?.label || valueSpec?.property) })}</strong> (${Number(params.percent || 0).toFixed(1)}%)`
            },
            legend: {
                show: data.length > 1,
                type: 'scroll',
                orient: compact ? 'horizontal' : 'vertical',
                bottom: compact ? 0 : undefined,
                right: compact ? 2 : 2,
                top: compact ? undefined : 2,
                textStyle: { color: tokens.legend, fontSize: 8 },
                itemWidth: 9,
                itemHeight: 7
            },
            series: [{
                name: valueSpec?.label || valueSpec?.property || 'Value',
                type: 'pie',
                radius: chartKind === 'donut' ? (compact ? ['38%', '66%'] : ['44%', '72%']) : (compact ? '64%' : '72%'),
                center: compact ? ['50%', '46%'] : ['42%', '50%'],
                minAngle: 2,
                avoidLabelOverlap: true,
                label: { show: !compact, color: tokens.axis, fontSize: 8, formatter: '{b}\n{d}%' },
                labelLine: { show: !compact, length: 8, length2: 5 },
                data
            }],
            toolbox: standardToolbox(state, 'x')
        };
        return { option, empty: !rows.length || !data.length };
    }

    function buildCartesianOption(state, visual, host, chartKind) {
        const model = chartSeriesModel(state, visual, chartKind);
        const { rows, ySpecs, y2Specs, xLabels, series } = model;
        const tokens = themeTokens(state);
        const palette = chartPalette(state);
        const visualTitle = chartTitle(visual);
        const isHorizontal = chartKind === 'horizontal-bar' || chartKind === 'stacked-horizontal-bar';
        const size = chartSizeHint(state, visual, host);
        const compact = size.height < 190;
        const veryCompact = size.height < 130;
        const fontSize = veryCompact ? 7 : compact ? 7.5 : 8.5;
        const lineHeight = Math.ceil(fontSize + 3);
        const maxLines = veryCompact ? 2 : compact ? 3 : 4;
        const options = visual.options || {};
        const xTitle = String(options.xTitle || '').trim();
        const yTitle = String(options.yTitle || '').trim();
        const y2Title = String(options.y2Title || '').trim();
        const legendVisible = series.length > 1 && size.height >= 105;
        const showZoom = xLabels.length > 24;

        const leftReserve = isHorizontal
            ? Math.min(150, Math.max(74, size.width * .23))
            : (veryCompact ? 34 : compact ? 40 : 48);
        const rightReserve = y2Specs.length ? (compact ? 40 : 48) : 7;
        const categoryArea = Math.max(90, size.width - leftReserve - rightReserve);
        const categorySlot = categoryArea / Math.max(1, xLabels.length);
        const maxChars = Math.round(clampNumber(
            Math.floor(categorySlot / Math.max(3.7, fontSize * .50)),
            xLabels.length <= 6 ? 7 : 6,
            compact ? 16 : 20
        ));
        const forceWordLines = !isHorizontal && (compact || xLabels.length <= 8);
        const wrappedLabels = xLabels.map(label => wrapCategoryLabel(label, Math.min(maxChars, compact ? 11 : 14), maxLines, forceWordLines));
        const labelLines = Math.max(1, ...wrappedLabels.map(label => String(label).split('\n').length));
        const xTitleReserve = !isHorizontal && xTitle && !veryCompact ? 12 : 0;
        const axisBottom = isHorizontal
            ? (xTitle && !veryCompact ? 18 : 5)
            : Math.min(veryCompact ? 34 : compact ? 44 : 62,
                4 + labelLines * lineHeight + xTitleReserve + (showZoom ? 10 : 0));

        const primaryHasBars = series.some(item => Number(item?.yAxisIndex || 0) === 0 && item?.type === 'bar');
        const secondaryHasBars = series.some(item => Number(item?.yAxisIndex || 0) === 1 && item?.type === 'bar');
        const extents = [
            numericAxisExtent(series, 0, primaryHasBars),
            numericAxisExtent(series, 1, secondaryHasBars)
        ];
        const allPrimarySpecs = ySpecs;
        const primaryCurrency = allPrimarySpecs.some(spec => isCurrencyField(spec.label || spec.property)) || isCurrencyField(visualTitle) || isCurrencyField(yTitle);
        const primaryPercent = allPrimarySpecs.some(spec => isPercentField(spec.label || spec.property)) || isPercentField(yTitle);
        const secondaryCurrency = y2Specs.some(spec => isCurrencyField(spec.label || spec.property)) || isCurrencyField(y2Title);
        const secondaryPercent = y2Specs.some(spec => isPercentField(spec.label || spec.property)) || isPercentField(y2Title);

        // Treat each independent series or stack as one bar group.  Using a real
        // barWidth (rather than only barMaxWidth) is important in short tiles:
        // ECharts otherwise compresses a single series to a nearly invisible sliver.
        const barGroups = new Set();
        series.forEach((item, index) => {
            if (item.type !== 'bar') return;
            barGroups.add(item.stack ? `stack:${item.stack}` : `series:${index}`);
        });
        const barGroupCount = Math.max(1, barGroups.size);
        const compactWidthRatio = veryCompact ? .90 : compact ? .84 : .72;
        const dynamicBarWidth = Math.round(clampNumber(
            categorySlot * compactWidthRatio / barGroupCount,
            veryCompact ? 22 : compact ? 18 : 12,
            veryCompact ? 82 : compact ? 76 : 62
        ));

        series.forEach((item, index) => {
            if (item.type !== 'bar') return;
            const color = palette[index % palette.length];
            item.barWidth = dynamicBarWidth;
            item.barMaxWidth = dynamicBarWidth;
            item.barMinHeight = veryCompact ? 9 : compact ? 8 : 5;
            item.barCategoryGap = xLabels.length <= 6 ? '2%' : xLabels.length <= 12 ? '8%' : '18%';
            item.barGap = item.stack ? '0%' : '4%';
            item.itemStyle = {
                ...(item.itemStyle || {}),
                color: compact ? color : gradient(color),
                opacity: 1,
                borderRadius: isHorizontal ? [0, 3, 3, 0] : [3, 3, 0, 0],
                shadowBlur: compact ? 0 : 3,
                shadowColor: compact ? 'transparent' : color + '3d'
            };
        });

        const valueAxis = axisIndex => {
            const currency = axisIndex === 0 ? primaryCurrency : secondaryCurrency;
            const percent = axisIndex === 0 ? primaryPercent : secondaryPercent;
            const name = axisIndex === 0 ? yTitle : y2Title;
            return {
                type: 'value',
                scale: !(axisIndex === 0 ? primaryHasBars : secondaryHasBars),
                min: extents[axisIndex]?.min ?? 0,
                max: extents[axisIndex]?.max ?? 1,
                splitNumber: veryCompact ? 2 : compact ? 3 : 5,
                name: name && !compact ? name : '',
                nameLocation: 'middle',
                nameGap: axisIndex === 0 ? (compact ? 30 : 36) : (compact ? 31 : 38),
                nameTextStyle: { color: tokens.axis, fontSize: 7.5, fontWeight: 650 },
                axisLabel: {
                    color: tokens.axis,
                    fontSize,
                    margin: 4,
                    formatter: value => fmtNumber(value, { currency, percent })
                },
                splitLine: { lineStyle: { color: tokens.grid, type: 'dashed' } },
                axisLine: { show: false },
                axisTick: { show: false }
            };
        };

        const categoryAxis = {
            type: 'category',
            data: wrappedLabels,
            name: !isHorizontal && xTitle && !compact ? xTitle : '',
            nameLocation: 'middle',
            nameGap: Math.max(18, 8 + labelLines * lineHeight),
            nameTextStyle: { color: tokens.axis, fontSize: 7.5, fontWeight: 650 },
            axisLabel: {
                interval: 0,
                rotate: 0,
                hideOverlap: false,
                color: tokens.axis,
                fontSize,
                lineHeight,
                fontWeight: 650,
                margin: 4,
                width: isHorizontal ? Math.max(62, leftReserve - 12) : Math.max(36, categorySlot - 4),
                overflow: 'break',
                align: 'center',
                verticalAlign: 'top',
                formatter: value => wrapCategoryLabel(value, compact ? 11 : 14, maxLines, forceWordLines)
            },
            // Keep the category axis at the physical bottom/left of the plot even
            // when every value is negative.  The default onZero=true moves labels to
            // the zero line, which is why compact aging charts appeared empty and
            // their labels floated diagonally through the plot.
            axisLine: {
                onZero: false,
                lineStyle: { color: tokens.border }
            },
            axisTick: { alignWithLabel: true, lineStyle: { color: tokens.border } }
        };

        const option = {
            animationDuration: 520,
            animationEasing: 'cubicOut',
            color: palette,
            tooltip: {
                trigger: 'axis',
                confine: true,
                backgroundColor: tokens.tooltipBackground,
                borderColor: tokens.tooltipBorder,
                textStyle: { color: tokens.tooltipText, fontSize: 10 },
                axisPointer: { type: primaryHasBars ? 'shadow' : 'line' },
                formatter: params => {
                    const items = Array.isArray(params) ? params : [params];
                    const title = String(items[0]?.axisValueLabel || items[0]?.name || '').replace(/\n/g, ' ');
                    const rows = items.map(item => `${item.marker || ''}${esc(item.seriesName)}: <strong>${esc(formatCell(item.value, item.seriesName))}</strong>`);
                    return [esc(title), ...rows].join('<br>');
                }
            },
            legend: {
                show: legendVisible,
                selectedMode: legendVisible,
                type: 'scroll',
                top: 0,
                right: 3,
                left: visualTitle ? '42%' : 3,
                itemWidth: 8,
                itemHeight: 6,
                itemGap: 7,
                textStyle: { color: tokens.legend, fontSize: 7, fontWeight: 700, overflow: 'truncate', width: 96 },
                pageIconColor: palette[0],
                pageIconInactiveColor: tokens.legendInactive
            },
            grid: {
                left: leftReserve,
                right: rightReserve,
                top: legendVisible ? (compact ? 15 : 19) : 3,
                bottom: axisBottom,
                containLabel: false
            },
            xAxis: isHorizontal ? valueAxis(0) : categoryAxis,
            yAxis: isHorizontal
                ? {
                    ...categoryAxis,
                    inverse: true,
                    name: xTitle && !veryCompact ? xTitle : '',
                    nameGap: 8,
                    axisLabel: { ...categoryAxis.axisLabel, width: Math.max(62, leftReserve - 12), overflow: 'break' }
                }
                : (y2Specs.length ? [valueAxis(0), valueAxis(1)] : valueAxis(0)),
            series,
            toolbox: standardToolbox(state, isHorizontal ? 'y' : 'x'),
            dataZoom: showZoom ? [{
                type: 'inside',
                xAxisIndex: isHorizontal ? [] : [0],
                yAxisIndex: isHorizontal ? [0] : [],
                zoomOnMouseWheel: true,
                moveOnMouseMove: true,
                moveOnMouseWheel: true,
                filterMode: 'none'
            }, {
                type: 'slider',
                xAxisIndex: isHorizontal ? [] : [0],
                yAxisIndex: isHorizontal ? [0] : [],
                width: isHorizontal ? 10 : undefined,
                height: isHorizontal ? undefined : 8,
                right: isHorizontal ? 2 : undefined,
                bottom: isHorizontal ? undefined : 2,
                borderColor: 'transparent',
                fillerColor: tokens.zoomFill,
                handleStyle: { color: palette[1] },
                textStyle: { color: tokens.muted, fontSize: 7 }
            }] : []
        };

        return { option, empty: !rows.length || !series.length };
    }

    function createChartOption(state, visual, host, requestedKind) {
        const chartKind = chartKindForVisual(visual, requestedKind);
        const result = chartKind === 'pie' || chartKind === 'donut'
            ? buildPieOption(state, visual, host, chartKind)
            : buildCartesianOption(state, visual, host, chartKind);
        global.__csrLastChartOptions = global.__csrLastChartOptions || {};
        global.__csrLastChartOptions[visual?.id || chartTitle(visual) || `chart-${Object.keys(global.__csrLastChartOptions).length}`] = result.option;
        return result;
    }


    function visualTitleFields(visual, roleNames) {
        const roles = visual?.roles || {};
        const labels = [];
        (roleNames || Object.keys(roles)).forEach(roleName => {
            (roles[roleName] || []).forEach(spec => {
                const label = String(spec?.label || spec?.property || '').trim();
                if (label && !labels.includes(label)) labels.push(label);
            });
        });
        return labels;
    }

    function derivedVisualTitle(visual, fallback) {
        const configured = String(visual?.title || visual?.text || '').trim();
        if (configured && !/^csr-v\d+-/i.test(configured)) return configured;
        const type = effectiveVisualType(visual);
        const categories = visualTitleFields(visual, ['Rows', 'Category', 'X']);
        const values = visualTitleFields(visual, ['Values', 'Y', 'Size']);
        if (type === 'slicer') return categories[0] || visualTitleFields(visual)[0] || 'Filter';
        if (type === 'map') return `${visualTitleFields(visual, ['Series'])[0] || 'Locations'} Map`;
        if (type === 'pivotTable') return `${values[0] || 'Value'}${categories.length ? ` by ${categories.join(' / ')}` : ' Matrix'}`;
        if (type === 'tableEx') return `${categories[0] || values[0] || 'Details'} Details`;
        if (type === 'card' || type === 'multiRowCard') return values[0] || visualTitleFields(visual)[0] || 'Summary';
        if (/Chart$/i.test(type)) return `${values[0] || 'Value'}${categories.length ? ` by ${categories.join(' / ')}` : ''}`;
        return String(fallback || titleCase(type) || 'Visual');
    }

    function visualTitleHtml(visual, fallback) {
        const title = derivedVisualTitle(visual, fallback);
        return title ? `<div class="csr-v-title" title="${esc(title)}">${esc(title)}</div>` : '';
    }


    // Shared bar-chart policy. Every column, stacked-column, horizontal-bar,
    // stacked-horizontal-bar and bar+line combo component runs through this one
    // implementation. This prevents single-series charts from drifting into a
    // separate axis/legend/label path.
    function applyBarChartClassPolicy(state, visual, host, option, component) {
        if (!option || !component) return option;

        const allSeries = Array.isArray(option.series) ? option.series : [];
        const barSeries = allSeries.filter(item => item && item.type === 'bar');
        if (!barSeries.length) return option;

        const horizontal = Boolean(component.horizontal);
        const categoryAxisCollection = horizontal ? option.yAxis : option.xAxis;
        const valueAxisCollection = horizontal ? option.xAxis : option.yAxis;
        const categoryAxis = Array.isArray(categoryAxisCollection)
            ? categoryAxisCollection.find(axis => axis && axis.type === 'category') || categoryAxisCollection[0]
            : categoryAxisCollection;
        if (!categoryAxis || categoryAxis.type !== 'category') return option;

        const size = chartSizeHint(state, visual, host);
        const compact = size.height < 230;
        const veryCompact = size.height < 150;
        const rawLabels = Array.isArray(categoryAxis.data) ? categoryAxis.data : [];
        const maxLines = veryCompact ? 2 : compact ? 3 : 4;
        const maxChars = compact ? 10 : 14;
        const forceWordLines = !horizontal && (compact || rawLabels.length <= 8);
        const wrappedLabels = rawLabels.map(value =>
            wrapCategoryLabel(
                String(value == null ? '' : value).replace(/\n/g, ' '),
                maxChars,
                maxLines,
                forceWordLines
            )
        );
        const labelLines = Math.max(1, ...wrappedLabels.map(value => String(value).split('\n').length));
        const fontSize = veryCompact ? 7 : compact ? 8 : 9;
        const lineHeight = fontSize + 3;

        categoryAxis.data = wrappedLabels;
        categoryAxis.axisLabel = {
            ...(categoryAxis.axisLabel || {}),
            interval: 0,
            rotate: 0,
            hideOverlap: false,
            fontSize,
            lineHeight,
            margin: 5,
            overflow: 'break',
            align: horizontal ? 'right' : 'center',
            verticalAlign: horizontal ? 'middle' : 'top',
            formatter: value => String(value)
        };
        categoryAxis.axisLine = {
            ...(categoryAxis.axisLine || {}),
            onZero: false
        };

        if (horizontal) {
            if (Array.isArray(option.yAxis)) {
                const index = option.yAxis.indexOf(categoryAxis);
                option.yAxis[index < 0 ? 0 : index] = categoryAxis;
            } else option.yAxis = categoryAxis;
        } else {
            if (Array.isArray(option.xAxis)) {
                const index = option.xAxis.indexOf(categoryAxis);
                option.xAxis[index < 0 ? 0 : index] = categoryAxis;
            } else option.xAxis = categoryAxis;
        }

        const legendVisible = allSeries.length > 1;
        if (Array.isArray(option.legend)) {
            option.legend = option.legend.map(item => ({
                ...(item || {}),
                show: legendVisible,
                selectedMode: legendVisible
            }));
        } else {
            option.legend = {
                ...(option.legend || {}),
                show: legendVisible,
                selectedMode: legendVisible
            };
        }

        option.grid = {
            ...(option.grid || {}),
            top: legendVisible ? (compact ? 15 : 19) : 4,
            right: horizontal ? 12 : 8,
            bottom: horizontal
                ? 12
                : Math.min(veryCompact ? 42 : compact ? 54 : 70, 10 + labelLines * lineHeight),
            containLabel: false
        };
        if (horizontal) {
            option.grid.left = Math.max(Number(option.grid.left || 0), compact ? 96 : 118);
        } else {
            option.grid.left = Math.max(Number(option.grid.left || 0), veryCompact ? 40 : compact ? 46 : 52);
        }

        // Include zero on every numeric axis that owns at least one bar series.
        const valueAxes = Array.isArray(valueAxisCollection) ? valueAxisCollection : [valueAxisCollection];
        const barAxisIndexes = Array.from(new Set(barSeries.map(item => Number(item.yAxisIndex || 0))));
        barAxisIndexes.forEach(axisIndex => {
            const axis = valueAxes[axisIndex] || valueAxes[0];
            if (!axis) return;
            const extent = numericAxisExtent(allSeries, axisIndex, true);
            if (extent) {
                axis.min = extent.min;
                axis.max = extent.max;
                axis.scale = false;
                axis.splitNumber = veryCompact ? 2 : compact ? 3 : 5;
            }
        });

        const palette = chartPalette(state);
        const categoryPixels = horizontal
            ? Math.max(90, size.height - Number(option.grid.top || 0) - Number(option.grid.bottom || 0))
            : Math.max(120, size.width - Number(option.grid.left || 0) - Number(option.grid.right || 0));
        const categorySlot = categoryPixels / Math.max(1, wrappedLabels.length);
        const barGroups = new Set();
        barSeries.forEach((item, index) => {
            barGroups.add(item.stack ? `stack:${item.stack}` : `series:${index}`);
        });
        const barGroupCount = Math.max(1, barGroups.size);
        const widthRatio = veryCompact ? .78 : compact ? .72 : .62;
        const barWidth = Math.round(clampNumber(
            categorySlot * widthRatio / barGroupCount,
            compact ? 24 : 16,
            compact ? 82 : 68
        ));

        barSeries.forEach((bar, index) => {
            const color = palette[index % palette.length];
            bar.barWidth = barWidth;
            bar.barMaxWidth = barWidth;
            bar.barMinHeight = veryCompact ? 10 : compact ? 9 : 6;
            bar.barCategoryGap = wrappedLabels.length <= 6 ? '2%' : wrappedLabels.length <= 12 ? '8%' : '18%';
            bar.barGap = bar.stack ? '0%' : '4%';
            bar.itemStyle = {
                ...(bar.itemStyle || {}),
                color: compact ? color : gradient(color),
                opacity: 1,
                borderRadius: horizontal ? [0, 4, 4, 0] : [4, 4, 0, 0],
                shadowBlur: compact ? 0 : 3,
                shadowColor: compact ? 'transparent' : color + '3d'
            };
        });

        return option;
    }

    function renderChart(state, visual, host, requestedKind, component) {
        if (!global.echarts) {
            host.innerHTML = '<div class="csr-empty">ECharts failed to load.</div>';
            return;
        }

        host.innerHTML = visualTitleHtml(visual, titleCase(visual.type)) + '<div class="csr-chart"></div>';
        const chartEl = host.querySelector('.csr-chart');
        const result = createChartOption(state, visual, chartEl, requestedKind);
        const option = component && typeof component.finalizeOption === 'function'
            ? component.finalizeOption({ state, visual, host: chartEl }, result.option)
            : result.option;
        const empty = result.empty;
        if (empty) {
            chartEl.innerHTML = sourceEmptyMarkup(state, visual, 'No matching data');
            return;
        }

        const chart = echarts.init(chartEl, null, { renderer: 'canvas' });
        global.__csrLastChartOptions = global.__csrLastChartOptions || {};
        global.__csrLastChartOptions[String(visual.id || visual.title || state.charts.length)] = option;
        chart.setOption(option, { notMerge: true });
        const resize = () => { try { chart.resize(); } catch (_) { } };
        requestAnimationFrame(() => {
            resize();
            requestAnimationFrame(resize);
            global.setTimeout(resize, 120);
        });
        if (global.ResizeObserver) {
            const observer = new ResizeObserver(resize);
            observer.observe(chartEl);
            chart.__csrResizeObserver = observer;
        }
        bindChartHoverTools(host.closest('.csr-visual') || host, chart);
        state.charts.push(chart);
    }

    function tableRows(state, visual) {
        const specs = getRoles(visual, 'Values');
        const entity = specs[0]?.entity || mainEntity(visual);
        const allFilters = visual.filters || [];
        const measureFilters = allFilters.filter(filter => isMeasureFilter(filter, specs));
        const rowFilters = allFilters.filter(filter => !isMeasureFilter(filter, specs));
        let rows = applyFiltersWithList(state, datasetFor(state, entity), visual, entity, rowFilters);
        const dims = specs.filter(spec => String(spec.agg || 'none').toLowerCase() === 'none' && spec.kind !== 'measure');

        if (!dims.length) {
            let raw = rows.map(row => ({
                _raw: row, rows: [row], values: specs.map(spec =>
                    spec.kind === 'measure' ? measureValue(state, [row], spec, null) : getField(state, row, spec.entity, spec.property)
                )
            }));
            if (measureFilters.length) {
                raw = raw.filter(group => measureFilters.every(filter => passAggregateFilter(state, group.rows, filter, entity)));
            }
            return raw;
        }

        const map = new Map();
        rows.forEach(row => {
            const keyValues = dims.map(spec => getField(state, row, spec.entity, spec.property));
            const key = JSON.stringify(keyValues);
            if (!map.has(key)) map.set(key, { keyValues, rows: [] });
            map.get(key).rows.push(row);
        });

        let out = Array.from(map.values()).map(group => {
            const values = specs.map(spec => dims.includes(spec)
                ? group.keyValues[dims.indexOf(spec)]
                : aggregateValue(state, group.rows, spec, null));
            return { values, rows: group.rows };
        });

        if (measureFilters.length) {
            out = out.filter(group => measureFilters.every(filter =>
                passAggregateFilter(state, group.rows, filter, entity)
            ));
        }

        const sortSpec = (visual.sort || [])[0];
        if (sortSpec) {
            let index = specs.findIndex(spec => norm(spec.property) === norm(sortSpec.property));
            if (index < 0 && isMeasureFilter({ field: sortSpec.property }, specs)) {
                out.sort((left, right) => {
                    const a = measureValue(state, left.rows, { kind: 'measure', entity, property: sortSpec.property, agg: 'measure' }, null);
                    const b = measureValue(state, right.rows, { kind: 'measure', entity, property: sortSpec.property, agg: 'measure' }, null);
                    return compareValues(a, b) * (String(sortSpec.direction).toLowerCase() === 'descending' ? -1 : 1);
                });
            } else if (index >= 0) {
                out.sort((left, right) => compareValues(left.values[index], right.values[index]) *
                    (String(sortSpec.direction).toLowerCase() === 'descending' ? -1 : 1));
            }
        }

        const topN = Math.max(0, Number(visual.topN || 0));
        if (topN) out = out.slice(0, topN);
        return out;
    }

    function isMonthlyEbnotesPage(state) {
        const key = norm(state?.cfg?.key || state?.cfg?.templateId || '');
        const pageKey = norm(state?.cfg?.pageKey || '');
        return key === norm('csr_monthly-ebnotes') || pageKey === norm('csr_monthly-ebnotes');
    }

    function customerPaymentsPageKey(state) {
        const key = norm(state?.cfg?.key || state?.cfg?.templateId || '');
        const pageKey = norm(state?.cfg?.pageKey || '');
        if (key === norm('csr_customer-payments-daily') || pageKey === norm('csr_customer-payments-daily')) {
            return 'csr_customer-payments-daily';
        }
        if (key === norm('csr_customer-payments-monthly') || pageKey === norm('csr_customer-payments-monthly')) {
            return 'csr_customer-payments-monthly';
        }
        return '';
    }

    function isCustomerPaymentsPage(state) {
        return !!customerPaymentsPageKey(state);
    }

    function isAgingReportPage(state) {
        const key = norm(state?.cfg?.key || state?.cfg?.templateId || '');
        const pageKey = norm(state?.cfg?.pageKey || '');
        return key === norm('csr_aging-report-hourly-updates') ||
            pageKey === norm('csr_aging-report-hourly-updates');
    }

    function monthlyRequestFilters(state) {
        const filters = {};
        for (const selection of Object.values(state?.slicerSelections || {})) {
            if (!selection?.field) continue;
            const values = Array.isArray(selection.values)
                ? selection.values.filter(value => value != null && String(value).trim() !== '')
                : [];
            if (!values.length) continue;
            filters[selection.field] = { mode: 'in', values };
        }
        return filters;
    }

    function visualPageInfo(state, visual) {
        return state?.pageInfoByVisual?.[visual?.id] || state?.pageInfo || null;
    }

    function visualQueryContext(state, visual) {
        return state?.queryContextByVisual?.[visual?.id] || state?.queryContext || null;
    }

    async function readJsonResponse(response, fallbackLabel) {
        const text = await response.text();
        if (!response.ok) throw new Error(text || `${fallbackLabel || 'Request'} failed ${response.status}`);
        if (!text.trim()) throw new Error(`${fallbackLabel || 'Request'} returned an empty response (HTTP ${response.status})`);
        try {
            return JSON.parse(text);
        } catch (error) {
            const sample = text.slice(0, 240).replace(/\s+/g, ' ');
            throw new Error(`${fallbackLabel || 'Request'} returned invalid JSON: ${error.message}${sample ? ` — ${sample}` : ''}`);
        }
    }

    async function loadMoreCsrTable(state, visual, button) {
        const pageInfo = visualPageInfo(state, visual) || {};
        const context = visualQueryContext(state, visual) || {};
        if (state.tableLoading || !pageInfo.hasMore || pageInfo.nextOffset == null || !context.templateId) return;

        state.tableLoading = true;
        const originalText = button.textContent;
        button.disabled = true;
        button.textContent = 'Loading…';
        try {
            const endpoint = new URL(context.endpoint || state.cfg.sourceEndpoint || '../Dashboard/GetCustomHtmlLiveData', global.location.href).toString();
            const response = await fetch(endpoint, {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    ...context,
                    payloadMode: 'csrVisual',
                    skip: pageInfo.nextOffset,
                    take: context.take || pageInfo.pageSize || 100
                })
            });
            const result = await readJsonResponse(response, 'Table page request');
            const alias = configuredSourceAliases(state)[0] || mainEntity(visual) || 'data';
            const current = Array.isArray(state?.visualDataSets?.[visual.id])
                ? state.visualDataSets[visual.id].slice()
                : datasetFor(state, alias).slice();
            const incoming = Array.isArray(result.data) ? result.data : [];
            if (state.visualDataSets && visual?.id) {
                state.visualDataSets[visual.id] = current.concat(incoming);
                state.pageInfoByVisual = state.pageInfoByVisual || {};
                state.queryContextByVisual = state.queryContextByVisual || {};
                state.pageInfoByVisual[visual.id] = result.pageInfo || null;
                state.queryContextByVisual[visual.id] = result.queryContext || context;
            } else {
                state.dataSets[alias] = current.concat(incoming);
                state.pageInfo = result.pageInfo || null;
                state.queryContext = result.queryContext || context;
            }
            state.sourceStatus[alias] = 'loaded';
            state.tableLoading = false;
            renderPage(state);
        } catch (error) {
            button.disabled = false;
            button.textContent = originalText;
            button.title = String(error?.message || error);
        } finally {
            state.tableLoading = false;
        }
    }

    function renderTable(state, visual, host) {
        const specs = getRoles(visual, 'Values');
        const rows = tableRows(state, visual);
        const more = visualPageInfo(state, visual)?.hasMore
            ? `<div class="csr-load-more-row"><button type="button" class="csr-load-more">Load more</button></div>`
            : '';
        host.innerHTML = visualTitleHtml(visual, '') + `<div class="csr-table-wrap"><table class="csr-table"><thead><tr>${specs.map(s => `<th>${esc(s.label || titleCase(s.property))}</th>`).join('')}</tr></thead><tbody>${rows.map(r => `<tr>${r.values.map((v, i) => `<td title="${esc(formatCell(v, specs[i]?.label || specs[i]?.property))}">${esc(formatCell(v, specs[i]?.label || specs[i]?.property))}</td>`).join('')}</tr>`).join('')}</tbody></table>${more}</div>`;
        if (!rows.length) {
            host.querySelector('.csr-table-wrap').innerHTML = sourceEmptyMarkup(state, visual, 'No matching data');
            return;
        }
        const loadButton = host.querySelector('.csr-load-more');
        loadButton?.addEventListener('click', event => loadMoreCsrTable(state, visual, event.currentTarget));
        if (loadButton && 'IntersectionObserver' in global) {
            const scrollRoot = host.querySelector('.csr-table-wrap');
            const observer = new IntersectionObserver(entries => {
                if (entries.some(entry => entry.isIntersecting)) {
                    observer.disconnect();
                    if (state.tableObserver === observer) state.tableObserver = null;
                    loadMoreCsrTable(state, visual, loadButton);
                }
            }, { root: scrollRoot, rootMargin: '0px 0px 80px 0px', threshold: 0.01 });
            state.tableObserver = observer;
            observer.observe(loadButton);
        }
    }

    function visualSortDirection(visual, property, fallback = 'ascending') {
        const match = (visual.sort || []).find(item => norm(item?.property) === norm(property));
        return String(match?.direction || fallback).toLowerCase() === 'descending' ? -1 : 1;
    }

    function matrixValueCell(state, rows, spec, category) {
        const value = aggregateValue(state, rows, spec, category);
        const formatted = formatCell(value, spec.label || spec.property);
        return `<td title="${esc(formatted)}">${esc(formatted)}</td>`;
    }

    function renderHierarchicalPivot(state, visual, host) {
        const rowSpecs = getRoles(visual, 'Rows');
        const colSpecs = getRoles(visual, 'Columns');
        const valSpecs = getRoles(visual, 'Values');
        const entity = rowSpecs[0]?.entity || colSpecs[0]?.entity || valSpecs[0]?.entity || mainEntity(visual);
        const rows = applyFilters(state, datasetFor(state, entity), visual, entity);
        if (!rows.length) {
            host.innerHTML = visualTitleHtml(visual, '') + `<div class="csr-table-wrap">${sourceEmptyMarkup(state, visual, 'No matching data')}</div>`;
            return;
        }

        const yearSpec = rowSpecs[0];
        const monthSpec = rowSpecs[1];
        const yearDirection = visualSortDirection(visual, yearSpec?.property, 'descending');
        const monthDirection = visualSortDirection(visual, monthSpec?.property, 'descending');
        const allColRows = rows.filter(row => num(rawGet(row, ['__HierarchyLevel']), 1) !== 0);
        const colMap = new Map();
        if (colSpecs.length) {
            allColRows.forEach(row => {
                const info = categoryInfo(state, row, colSpecs);
                colMap.set(info.key, info);
            });
        } else {
            colMap.set('(all)', { key: '(all)', label: valSpecs.length === 1 ? (valSpecs[0].label || valSpecs[0].property) : '', sort: 0 });
        }
        const colCats = Array.from(colMap.values()).sort((a, b) => compareValues(a.sort, b.sort));

        const yearMap = new Map();
        rows.forEach(row => {
            const value = getField(state, row, yearSpec?.entity || entity, yearSpec?.property || 'year');
            const key = String(value == null || value === '' ? '(blank)' : value);
            if (!yearMap.has(key)) yearMap.set(key, { key, value, rows: [] });
            yearMap.get(key).rows.push(row);
        });
        const years = Array.from(yearMap.values()).sort((a, b) => compareValues(a.value, b.value) * yearDirection);

        if (!(state.matrixCollapsed instanceof Set)) state.matrixCollapsed = new Set();
        const body = [];
        for (const yearGroup of years) {
            const parentRows = yearGroup.rows.filter(row => num(rawGet(row, ['__HierarchyLevel']), 1) === 0);
            const detailRows = yearGroup.rows.filter(row => num(rawGet(row, ['__HierarchyLevel']), 1) !== 0);
            const collapsed = state.matrixCollapsed.has(`${visual.id}|${yearGroup.key}`);
            const parentCells = colCats.flatMap(col => valSpecs.map(spec => {
                const scoped = colSpecs.length
                    ? parentRows.filter(row => categoryInfo(state, row, colSpecs).key === col.key)
                    : parentRows;
                return matrixValueCell(state, scoped, spec, col);
            })).join('');
            body.push(`<tr class="csr-matrix-parent" data-year-key="${esc(yearGroup.key)}"><th><button type="button" class="csr-matrix-toggle" data-matrix-toggle="${esc(yearGroup.key)}" aria-expanded="${collapsed ? 'false' : 'true'}"><span aria-hidden="true">${collapsed ? '▸' : '▾'}</span><strong>${esc(yearGroup.value)}</strong></button></th>${parentCells}</tr>`);

            if (collapsed || !monthSpec) continue;
            const monthMap = new Map();
            detailRows.forEach(row => {
                const value = getField(state, row, monthSpec.entity || entity, monthSpec.property);
                const key = String(value == null || value === '' ? '(blank)' : value);
                if (!monthMap.has(key)) monthMap.set(key, { key, value, sort: monthNumber(value) || sortRank(value), rows: [] });
                monthMap.get(key).rows.push(row);
            });
            const months = Array.from(monthMap.values()).sort((a, b) => compareValues(a.sort, b.sort) * monthDirection);
            for (const monthGroup of months) {
                const childCells = colCats.flatMap(col => valSpecs.map(spec => {
                    const scoped = colSpecs.length
                        ? monthGroup.rows.filter(row => categoryInfo(state, row, colSpecs).key === col.key)
                        : monthGroup.rows;
                    return matrixValueCell(state, scoped, spec, col);
                })).join('');
                body.push(`<tr class="csr-matrix-child"><th><span class="csr-matrix-branch" aria-hidden="true">└</span><span>${esc(monthGroup.value)}</span></th>${childCells}</tr>`);
            }
        }

        const header = colCats.flatMap(col => valSpecs.map(spec => {
            if (!colSpecs.length) return `<th>${esc(spec.label || spec.property)}</th>`;
            return `<th>${esc(col.label)}${valSpecs.length > 1 ? `<br><small>${esc(spec.label || spec.property)}</small>` : ''}</th>`;
        })).join('');

        host.innerHTML = visualTitleHtml(visual, '') + `<div class="csr-table-wrap"><table class="csr-table csr-pivot csr-hierarchy-matrix"><thead><tr><th>${esc(rowSpecs.map(x => x.label || x.property).join(' ') || 'Category')}</th>${header}</tr></thead><tbody>${body.join('')}</tbody></table></div>`;
        host.querySelectorAll('[data-matrix-toggle]').forEach(button => {
            button.addEventListener('click', () => {
                const key = `${visual.id}|${button.dataset.matrixToggle || ''}`;
                if (state.matrixCollapsed.has(key)) state.matrixCollapsed.delete(key);
                else state.matrixCollapsed.add(key);
                renderPage(state);
            });
        });
    }

    function renderPivot(state, visual, host) {
        if (visual.options?.hierarchicalRows === true && getRoles(visual, 'Rows').length > 1) {
            renderHierarchicalPivot(state, visual, host);
            return;
        }
        const rowSpecs = getRoles(visual, 'Rows');
        const colSpecs = getRoles(visual, 'Columns');
        const valSpecs = getRoles(visual, 'Values');
        const entity = rowSpecs[0]?.entity || colSpecs[0]?.entity || valSpecs[0]?.entity || mainEntity(visual);
        const rows = applyFilters(state, datasetFor(state, entity), visual, entity);
        const rowMap = new Map(), colMap = new Map(), cells = new Map();
        rows.forEach(row => {
            const ri = categoryInfo(state, row, rowSpecs);
            const ci = categoryInfo(state, row, colSpecs);
            rowMap.set(ri.key, ri); colMap.set(ci.key, ci);
            const key = ri.key + '||' + ci.key;
            if (!cells.has(key)) cells.set(key, []);
            cells.get(key).push(row);
        });
        const rowCats = Array.from(rowMap.values()).sort((a, b) => compareValues(a.sort, b.sort));
        const colCats = Array.from(colMap.values()).sort((a, b) => compareValues(a.sort, b.sort));
        const header = colCats.flatMap(c => valSpecs.map(v => `<th>${esc(c.label)}${valSpecs.length > 1 ? `<br><small>${esc(v.label || v.property)}</small>` : ''}</th>`)).join('');
        const body = rowCats.map(r => {
            const cellsHtml = colCats.flatMap(c => {
                const group = cells.get(r.key + '||' + c.key) || [];
                return valSpecs.map(v => {
                    const value = aggregateValue(state, group, v, c);
                    return `<td>${esc(formatCell(value, v.label || v.property))}</td>`;
                });
            }).join('');
            return `<tr><th>${esc(r.label)}</th>${cellsHtml}</tr>`;
        }).join('');
        host.innerHTML = visualTitleHtml(visual, '') + `<div class="csr-table-wrap"><table class="csr-table csr-pivot"><thead><tr><th>${esc(rowSpecs.map(x => x.label || x.property).join(' / ') || 'Category')}</th>${header}</tr></thead><tbody>${body}</tbody></table></div>`;
        if (!rows.length) host.querySelector('.csr-table-wrap').innerHTML = sourceEmptyMarkup(state, visual, 'No matching data');
    }

    function renderCard(state, visual, host, multi) {
        const specs = getRoles(visual, 'Values');
        const entity = specs[0]?.entity || mainEntity(visual);
        const rows = applyFilters(state, datasetFor(state, entity), visual, entity);
        if (!rows.length && sourceMetaForEntity(state, entity)?.error) {
            host.innerHTML = visualTitleHtml(visual, '') + sourceEmptyMarkup(state, visual, 'No matching data');
            return;
        }
        const values = specs.map(s => ({ label: s.label || titleCase(s.property), value: aggregateValue(state, rows, s, null) }));
        if (multi) {
            host.innerHTML = visualTitleHtml(visual, '') + `<div class="csr-multi-card">${values.map(v => `<div class="csr-metric"><span>${esc(v.label)}</span><strong>${esc(formatCell(v.value, v.label))}</strong></div>`).join('')}</div>`;
        } else {
            const v = values[0] || { label: '', value: null };
            host.innerHTML = visualTitleHtml(visual, v.label) + `<div class="csr-card-value">${esc(formatCell(v.value, v.label))}</div>`;
        }
    }

    function renderTextbox(visual, host) {
        const text = visual.text || visual.title || '';
        host.classList.add('csr-textbox');
        host.innerHTML = `<div class="csr-text-content">${esc(text)}</div>`;
    }

    function closeSlicerPopover(state) {
        if (state.activeSlicerPopover) {
            try { state.activeSlicerPopover.remove(); } catch (_) { }
            state.activeSlicerPopover = null;
        }
        document.querySelectorAll('.csr-slicer-open').forEach(el => el.classList.remove('csr-slicer-open'));
    }

    function renderSlicer(state, visual, host) {
        const spec = getRoles(visual, 'Values')[0];
        if (!spec) { host.innerHTML = '<div class="csr-empty">No slicer field</div>'; return; }
        const entity = spec.entity;
        const key = norm(entity) + '|' + norm(spec.property);
        const ownSelection = state.slicerSelections[key];
        if (ownSelection) delete state.slicerSelections[key];
        const rows = applyFilters(state, datasetFor(state, entity), { ...visual, filters: visual.filters || [] }, entity);
        if (ownSelection) state.slicerSelections[key] = ownSelection;
        if (!rows.length && sourceMetaForEntity(state, entity)?.error) {
            host.innerHTML = sourceEmptyMarkup(state, visual, 'No slicer values');
            return;
        }

        const values = Array.from(new Set(rows.map(r => getField(state, r, entity, spec.property)).filter(v => v != null && v !== '')))
            .sort((a, b) => compareValues(sortRank(a), sortRank(b)));
        const selected = selectedValues(state.slicerSelections[key]);
        const slicerOptions = visual.options?.slicer || {};
        const singleSelect = false;
        const displayValue = !selected.length ? 'All' : selected.length === 1 ? String(selected[0]) : `${selected.length} selected`;
        const label = visual.title || spec.label || titleCase(spec.property);

        host.classList.add('csr-slicer');
        host.innerHTML = `<button type="button" class="csr-slicer-trigger" aria-haspopup="listbox" aria-expanded="false" title="${esc(label)}: ${esc(displayValue)}">
      <span class="csr-slicer-label">${esc(label)}</span>
      <span class="csr-slicer-current">${esc(displayValue)}</span>
      <span class="csr-slicer-caret" aria-hidden="true">▾</span>
    </button>`;

        const trigger = host.querySelector('.csr-slicer-trigger');
        trigger.addEventListener('click', event => {
            event.stopPropagation();
            closeSlicerPopover(state);
            const pop = document.createElement('div');
            pop.className = 'csr-slicer-popover';
            pop.setAttribute('role', 'listbox');
            const draft = new Set(selected.map(String));
            const inputType = singleSelect ? 'radio' : 'checkbox';
            pop.innerHTML = `<div class="csr-slicer-popover-head">
          <strong>${esc(label)}</strong><input type="search" placeholder="Search" aria-label="Search ${esc(label)}">
        </div>
        <div class="csr-slicer-options">
          ${(!singleSelect && slicerOptions.showSelectAll !== false) ? `<label class="csr-slicer-option csr-slicer-all"><input type="checkbox" data-all="1" ${!draft.size ? 'checked' : ''}><span>All</span></label>` : ''}
          ${values.map((v, i) => `<label class="csr-slicer-option" data-search="${esc(String(v).toLowerCase())}"><input type="${inputType}" name="csr-${esc(visual.id || key)}" value="${esc(String(v))}" ${draft.has(String(v)) ? 'checked' : ''}><span>${esc(String(v))}</span></label>`).join('')}
        </div>
        <div class="csr-slicer-actions"><button type="button" data-clear>Clear</button><button type="button" data-apply>Apply</button></div>`;
            document.body.appendChild(pop);
            state.activeSlicerPopover = pop;
            host.closest('.csr-visual')?.classList.add('csr-slicer-open');
            trigger.setAttribute('aria-expanded', 'true');
            const rect = trigger.getBoundingClientRect();
            const popWidth = Math.max(190, Math.min(360, Math.max(rect.width, 220)));
            pop.style.width = `${popWidth}px`;
            pop.style.left = `${Math.max(4, Math.min(global.innerWidth - popWidth - 4, rect.left))}px`;
            const below = rect.bottom + 4;
            pop.style.top = `${below + 280 < global.innerHeight ? below : Math.max(4, rect.top - 284)}px`;

            const commit = () => {
                const checked = Array.from(pop.querySelectorAll('.csr-slicer-options input:not([data-all]):checked')).map(x => x.value);
                const selectedForState = singleSelect ? checked.slice(0, 1) : checked;
                if (!selectedForState.length) delete state.slicerSelections[key];
                else state.slicerSelections[key] = { entity, field: spec.property, values: selectedForState };
                state.relationshipCache.clear();
                try {
                    const target = global.parent?.parent && global.parent.parent !== global.parent
                        ? global.parent.parent
                        : global.parent;
                    target?.postMessage({
                        type: 'csr-slicer-change',
                        templateId: state.cfg.key || state.cfg.templateId || '',
                        pageKey: state.cfg.pageKey || '',
                        entity,
                        field: spec.property,
                        values: selectedForState
                    }, '*');
                } catch (_) { }
                closeSlicerPopover(state);
                if (isMonthlyEbnotesPage(state)) {
                    loadMonthlyEbnotesPage(state);
                } else if (isCustomerPaymentsPage(state)) {
                    loadCustomerPaymentsPage(state);
                } else {
                    renderPage(state);
                }
            };
            pop.querySelector('input[type="search"]')?.addEventListener('input', e => {
                const q = String(e.target.value || '').trim().toLowerCase();
                pop.querySelectorAll('.csr-slicer-option[data-search]').forEach(row => { row.hidden = q && !row.dataset.search.includes(q); });
            });
            pop.querySelector('[data-all]')?.addEventListener('change', e => {
                if (e.target.checked) pop.querySelectorAll('.csr-slicer-options input:not([data-all])').forEach(x => x.checked = false);
            });
            pop.querySelectorAll('.csr-slicer-options input:not([data-all])').forEach(input => input.addEventListener('change', () => {
                const all = pop.querySelector('[data-all]'); if (all) all.checked = false;
                if (singleSelect) commit();
            }));
            pop.querySelector('[data-clear]')?.addEventListener('click', () => { pop.querySelectorAll('input').forEach(x => x.checked = false); commit(); });
            pop.querySelector('[data-apply]')?.addEventListener('click', commit);
            setTimeout(() => pop.querySelector('input[type="search"]')?.focus(), 0);
        });
    }

    function geoFieldValue(state, row, entity, spec, candidates) {
        const requested = spec?.property;
        if (requested) {
            const direct = getField(state, row, spec?.entity || entity, requested);
            if (direct !== undefined && direct !== null && direct !== '') return direct;
        }
        for (const candidate of candidates) {
            const value = getField(state, row, entity, candidate);
            if (value !== undefined && value !== null && value !== '') return value;
        }
        return null;
    }

    function mapPointRows(state, visual) {
        const xSpec = getRoles(visual, 'X')[0] || getRoles(visual, 'Longitude')[0];
        const ySpec = getRoles(visual, 'Y')[0] || getRoles(visual, 'Latitude')[0];
        const sizeSpec = getRoles(visual, 'Size')[0] || getRoles(visual, 'Values')[0] || null;
        const seriesSpec = getRoles(visual, 'Series')[0] || null;
        const tooltipSpecs = getRoles(visual, 'Tooltips');
        const entity = xSpec?.entity || ySpec?.entity || mainEntity(visual);
        const rows = applyFilters(state, datasetFor(state, entity), visual, entity);
        const points = rows.map(row => {
            const lon = num(geoFieldValue(state, row, entity, xSpec, ['longitude', 'longtitude', 'lon', 'lng', 'x']), NaN);
            const lat = num(geoFieldValue(state, row, entity, ySpec, ['latitude', 'lat', 'y']), NaN);
            if (!Number.isFinite(lat) || !Number.isFinite(lon) || lat < -90 || lat > 90 || lon < -180 || lon > 180) return null;
            const series = seriesSpec ? String(getField(state, row, seriesSpec.entity || entity, seriesSpec.property) || '') : '';
            const details = tooltipSpecs.map(spec => ({
                label: spec.label || spec.property,
                value: formatCell(getField(state, row, spec.entity || entity, spec.property), spec.property)
            }));
            const size = sizeSpec ? num(getField(state, row, sizeSpec.entity || entity, sizeSpec.property), NaN) : NaN;
            return { lat, lon, series, details, size, row };
        }).filter(Boolean);
        return { rows, points };
    }

    function renderCoordinateMapFallback(state, visual, host, points, message) {
        const tokens = themeTokens(state);
        const palette = chartPalette(state);
        host.innerHTML = visualTitleHtml(visual, 'Map') + '<div class="csr-map-fallback-note"></div><div class="csr-chart"></div>';
        const note = host.querySelector('.csr-map-fallback-note');
        if (note) note.textContent = message || 'Map tiles unavailable; showing coordinates.';
        const xs = points.map(point => point.lon);
        const ys = points.map(point => point.lat);
        const padX = Math.max(.01, (Math.max(...xs) - Math.min(...xs)) * .12);
        const padY = Math.max(.01, (Math.max(...ys) - Math.min(...ys)) * .12);
        const chart = echarts.init(host.querySelector('.csr-chart'), null, { renderer: 'canvas' });
        chart.setOption({
            animationDuration: 500,
            toolbox: standardToolbox(state, 'x'),
            tooltip: {
                confine: true,
                backgroundColor: tokens.tooltipBackground,
                borderColor: tokens.tooltipBorder,
                textStyle: { color: tokens.tooltipText, fontSize: 10 },
                formatter: params => {
                    const point = params.data?.point;
                    if (!point) return '';
                    const title = point.series || point.details?.[0]?.value || 'Location';
                    return `<b>${esc(title)}</b><br>${(point.details || []).map(item => `${esc(item.label)}: ${esc(item.value)}`).join('<br>')}<br>Latitude: ${point.lat.toFixed(5)}<br>Longitude: ${point.lon.toFixed(5)}`;
                }
            },
            grid: { left: 12, right: 12, top: 18, bottom: 12 },
            xAxis: { type: 'value', min: Math.min(...xs) - padX, max: Math.max(...xs) + padX, show: false },
            yAxis: { type: 'value', min: Math.min(...ys) - padY, max: Math.max(...ys) + padY, show: false },
            dataZoom: [
                { type: 'inside', xAxisIndex: [0], zoomOnMouseWheel: true, moveOnMouseMove: true, moveOnMouseWheel: true },
                { type: 'inside', yAxisIndex: [0], zoomOnMouseWheel: true, moveOnMouseMove: true, moveOnMouseWheel: true }
            ],
            graphic: [{ type: 'rect', left: 0, top: 0, right: 0, bottom: 0, style: { fill: new echarts.graphic.RadialGradient(.75, .2, 1, [{ offset: 0, color: tokens.mapGlowStart }, { offset: 1, color: tokens.mapGlowEnd }]) } }],
            series: [{
                type: 'scatter',
                data: points.map(point => ({ value: [point.lon, point.lat, 1], point })),
                symbolSize: 12,
                itemStyle: { color: palette[1], shadowBlur: 18, shadowColor: palette[0] },
                emphasis: { scale: 1.5, itemStyle: { color: palette[4] } },
                label: { show: points.length < 25, formatter: params => params.data.point?.series || '', position: 'right', color: tokens.mapLabel, fontSize: 8 }
            }]
        }, { notMerge: true });
        bindChartHoverTools(host.closest('.csr-visual') || host, chart);
        state.charts.push(chart);
    }

    function renderMap(state, visual, host) {
        if (!global.echarts) { host.innerHTML = '<div class="csr-empty">ECharts failed to load.</div>'; return; }
        const { rows, points } = mapPointRows(state, visual);
        host.innerHTML = visualTitleHtml(visual, 'Map') + '<div class="csr-map-shell"><div class="csr-map-canvas"></div><div class="csr-map-loading">Loading map</div></div>';
        const canvas = host.querySelector('.csr-map-canvas');
        if (!points.length) {
            host.querySelector('.csr-map-shell').innerHTML = sourceEmptyMarkup(state, visual, rows.length ? 'No geocoded rows' : 'No matching data');
            return;
        }

        const token = Symbol('csr-map');
        host._csrMapToken = token;
        ensureLeaflet().then(L => {
            if (!host.isConnected || host._csrMapToken !== token) return;
            const loading = host.querySelector('.csr-map-loading');
            loading?.remove();
            const options = visual.options || {};
            const map = L.map(canvas, {
                zoomControl: true,
                attributionControl: true,
                preferCanvas: true,
                worldCopyJump: true,
                scrollWheelZoom: true
            });
            const tileUrl = options.tileUrl || state.cfg.mapTileUrl || 'https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png';
            const attribution = options.tileAttribution || state.cfg.mapTileAttribution || '&copy; OpenStreetMap contributors';
            L.tileLayer(tileUrl, { maxZoom: Number(options.maxZoom || 19), attribution }).addTo(map);

            const palette = chartPalette(state);
            const categories = Array.from(new Set(points.map(point => point.series || '(all)')));
            const sizes = points.map(point => point.size).filter(Number.isFinite);
            const minSize = sizes.length ? Math.min(...sizes) : 0;
            const maxSize = sizes.length ? Math.max(...sizes) : 0;
            const radiusFor = point => {
                if (!Number.isFinite(point.size) || maxSize <= minSize) return 6;
                return 5 + ((point.size - minSize) / (maxSize - minSize)) * 9;
            };

            const bounds = [];
            const markerRenderer = L.canvas({ padding: .5 });
            points.forEach(point => {
                const categoryIndex = Math.max(0, categories.indexOf(point.series || '(all)'));
                const color = palette[categoryIndex % palette.length];
                const title = point.series || point.details?.[0]?.value || 'Location';
                const popup = `<div class="csr-map-popup"><strong>${esc(title)}</strong>${(point.details || []).map(item => `<div><span>${esc(item.label)}</span>${esc(item.value)}</div>`).join('')}<div><span>Latitude</span>${point.lat.toFixed(5)}</div><div><span>Longitude</span>${point.lon.toFixed(5)}</div></div>`;
                L.circleMarker([point.lat, point.lon], {
                    renderer: markerRenderer,
                    radius: radiusFor(point),
                    color,
                    weight: 2,
                    opacity: .95,
                    fillColor: color,
                    fillOpacity: .68
                }).bindTooltip(esc(title), { direction: 'top', opacity: .92 }).bindPopup(popup).addTo(map);
                bounds.push([point.lat, point.lon]);
            });

            if (bounds.length === 1) map.setView(bounds[0], Number(options.singlePointZoom || 15));
            else map.fitBounds(bounds, { padding: [22, 22], maxZoom: Number(options.fitMaxZoom || 16) });
            map._csrBounds = bounds;
            host._csrLeafletMap = map;
            state.maps.push(map);
            global.setTimeout(() => { try { map.invalidateSize(false); } catch (_) { } }, 0);
        }).catch(error => {
            if (!host.isConnected || host._csrMapToken !== token) return;
            renderCoordinateMapFallback(state, visual, host, points, error?.message || 'Map tiles unavailable; showing coordinates.');
        });
    }

    function configuredSourceAliases(state) {
        return (Array.isArray(state.cfg.sources) ? state.cfg.sources : [])
            .map(source => typeof source === 'string' ? source : (source?.alias || source?.Alias || source?.object || source?.Object || ''))
            .map(String).map(x => x.trim()).filter(Boolean);
    }

    function visualEntities(visual) {
        const out = new Set();
        Object.values(visual.roles || {}).flat().forEach(spec => { if (spec?.entity) out.add(String(spec.entity)); });
        (visual.filters || []).forEach(filter => { if (filter?.entity) out.add(String(filter.entity)); });
        return Array.from(out);
    }

    function sourceState(state, entity) {
        const key = Object.keys(state.sourceStatus || {}).find(alias => norm(alias) === norm(entity));
        return key ? state.sourceStatus[key] : null;
    }

    function visualIsLoading(state, visual) {
        if (effectiveVisualType(visual) === 'textbox' || effectiveVisualType(visual) === 'actionButton' || isRedundantFlowVisual(visual)) return false;
        const entities = visualEntities(visual);
        return entities.some(entity => sourceState(state, entity) === 'loading');
    }

    function loadingMarkup(visual) {
        const title = visualTitleHtml(visual, '');
        return title + '<div class="csr-loading"><i></i><i></i><i></i><span>Loading data</span></div>';
    }

    async function loadMonthlyEbnotesPage(state) {
        const aliases = configuredSourceAliases(state);
        aliases.forEach(alias => { state.sourceStatus[alias] = 'loading'; });
        const requestNumber = (state.monthlyRequestNumber || 0) + 1;
        state.monthlyRequestNumber = requestNumber;

        try { state.monthlyAbortController?.abort(); } catch (_) { }
        const controller = new AbortController();
        state.monthlyAbortController = controller;
        if (state.hasRendered) refreshAllVisuals(state); else renderPage(state);

        const endpoint = new URL(state.cfg.sourceEndpoint || '../Dashboard/GetCustomHtmlLiveData', global.location.href).toString();
        try {
            const response = await fetch(endpoint, {
                method: 'POST',
                credentials: 'same-origin',
                signal: controller.signal,
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    templateId: 'csr_monthly-ebnotes',
                    payloadMode: 'csrPage',
                    connectionName: 'csr_pbip_source',
                    schema: 'dbo',
                    obj: 'ns_daily_ebnotes',
                    filters: monthlyRequestFilters(state),
                    take: 100
                })
            });
            const result = await readJsonResponse(response, 'Monthly EBNotes page request');
            if (requestNumber !== state.monthlyRequestNumber) return;

            state.visualDataSets = result.visualDataSets || result.visualDatasets || {};
            state.serverFilteredVisualData = result.serverFilteredVisualData === true;
            state.pageInfoByVisual = result.pageInfoByVisual || {};
            state.queryContextByVisual = result.queryContextByVisual || {};
            state.sourceMeta = Array.isArray(result.sources) ? result.sources : [];
            state.payloadError = result.error || null;
            aliases.forEach(alias => {
                const meta = sourceMetaForEntity(state, alias);
                state.sourceStatus[alias] = meta?.error ? 'error' : 'loaded';
            });
            state.relationshipCache.clear();
        } catch (error) {
            if (error?.name === 'AbortError' || requestNumber !== state.monthlyRequestNumber) return;
            state.visualDataSets = {};
            state.serverFilteredVisualData = false;
            state.pageInfoByVisual = {};
            state.queryContextByVisual = {};
            state.payloadError = String(error?.message || error);
            state.sourceMeta = [{
                alias: 'ns_daily_ebnotes',
                semanticEntity: 'ns_daily_ebnotes',
                returnedRows: 0,
                error: state.payloadError
            }];
            aliases.forEach(alias => { state.sourceStatus[alias] = 'error'; });
        } finally {
            if (requestNumber === state.monthlyRequestNumber) {
                if (state.monthlyAbortController === controller) state.monthlyAbortController = null;
                refreshAllVisuals(state);
            }
        }
    }

    async function loadCustomerPaymentsPage(state) {
        const aliases = configuredSourceAliases(state);
        aliases.forEach(alias => { state.sourceStatus[alias] = 'loading'; });
        const requestNumber = (state.customerPaymentsRequestNumber || 0) + 1;
        state.customerPaymentsRequestNumber = requestNumber;

        try { state.customerPaymentsAbortController?.abort(); } catch (_) { }
        const controller = new AbortController();
        state.customerPaymentsAbortController = controller;
        if (state.hasRendered) refreshAllVisuals(state); else renderPage(state);

        const templateId = customerPaymentsPageKey(state);
        const sourceAlias = configuredSourceAliases(state)[0] || 'ns_daily_cash_by_cycle_view';
        const endpoint = new URL(state.cfg.sourceEndpoint || '../Dashboard/GetCustomHtmlLiveData', global.location.href).toString();
        try {
            const response = await fetch(endpoint, {
                method: 'POST',
                credentials: 'same-origin',
                signal: controller.signal,
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    templateId,
                    payloadMode: 'csrPage',
                    connectionName: 'csr_pbip_source',
                    schema: 'dbo',
                    obj: sourceAlias,
                    filters: monthlyRequestFilters(state),
                    take: 100
                })
            });
            const result = await readJsonResponse(response, `${templateId} page request`);
            if (requestNumber !== state.customerPaymentsRequestNumber) return;

            state.visualDataSets = result.visualDataSets || result.visualDatasets || {};
            state.serverFilteredVisualData = result.serverFilteredVisualData === true;
            state.pageInfoByVisual = result.pageInfoByVisual || {};
            state.queryContextByVisual = result.queryContextByVisual || {};
            state.sourceMeta = Array.isArray(result.sources) ? result.sources : [];
            state.payloadError = result.error || null;
            aliases.forEach(alias => {
                const meta = sourceMetaForEntity(state, alias);
                state.sourceStatus[alias] = meta?.error ? 'error' : 'loaded';
            });
            state.relationshipCache.clear();
        } catch (error) {
            if (error?.name === 'AbortError' || requestNumber !== state.customerPaymentsRequestNumber) return;
            state.visualDataSets = {};
            state.serverFilteredVisualData = false;
            state.pageInfoByVisual = {};
            state.queryContextByVisual = {};
            state.payloadError = String(error?.message || error);
            state.sourceMeta = [{
                alias: sourceAlias,
                semanticEntity: sourceAlias,
                returnedRows: 0,
                error: state.payloadError
            }];
            aliases.forEach(alias => { state.sourceStatus[alias] = 'error'; });
        } finally {
            if (requestNumber === state.customerPaymentsRequestNumber) {
                if (state.customerPaymentsAbortController === controller) state.customerPaymentsAbortController = null;
                refreshAllVisuals(state);
            }
        }
    }

    async function loadAgingReportPage(state) {
        const aliases = configuredSourceAliases(state);
        aliases.forEach(alias => { state.sourceStatus[alias] = 'loading'; });
        const requestNumber = (state.agingReportRequestNumber || 0) + 1;
        state.agingReportRequestNumber = requestNumber;

        try { state.agingReportAbortController?.abort(); } catch (_) { }
        const controller = new AbortController();
        state.agingReportAbortController = controller;
        if (state.hasRendered) refreshAllVisuals(state); else renderPage(state);

        const endpoint = new URL(state.cfg.sourceEndpoint || '../Dashboard/GetCustomHtmlLiveData', global.location.href).toString();
        try {
            const response = await fetch(endpoint, {
                method: 'POST',
                credentials: 'same-origin',
                signal: controller.signal,
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    templateId: 'csr_aging-report-hourly-updates',
                    payloadMode: 'csrPage',
                    connectionName: 'csr_pbip_source',
                    schema: 'dbo',
                    obj: 'aging_trans_details'
                })
            });
            const result = await readJsonResponse(response, 'Aging report page request');
            if (requestNumber !== state.agingReportRequestNumber) return;

            state.visualDataSets = result.visualDataSets || result.visualDatasets || {};
            state.serverFilteredVisualData = result.serverFilteredVisualData === true;
            state.pageInfoByVisual = result.pageInfoByVisual || {};
            state.queryContextByVisual = result.queryContextByVisual || {};
            state.sourceMeta = Array.isArray(result.sources) ? result.sources : [];
            state.payloadError = result.error || null;
            aliases.forEach(alias => {
                const meta = sourceMetaForEntity(state, alias);
                state.sourceStatus[alias] = meta?.error ? 'error' : 'loaded';
            });
            state.relationshipCache.clear();
        } catch (error) {
            if (error?.name === 'AbortError' || requestNumber !== state.agingReportRequestNumber) return;
            state.visualDataSets = {};
            state.serverFilteredVisualData = false;
            state.pageInfoByVisual = {};
            state.queryContextByVisual = {};
            state.payloadError = String(error?.message || error);
            state.sourceMeta = aliases.map(alias => ({
                alias,
                semanticEntity: alias,
                returnedRows: 0,
                error: state.payloadError
            }));
            aliases.forEach(alias => { state.sourceStatus[alias] = 'error'; });
        } finally {
            if (requestNumber === state.agingReportRequestNumber) {
                if (state.agingReportAbortController === controller) state.agingReportAbortController = null;
                refreshAllVisuals(state);
            }
        }
    }

    async function fetchCsrSource(state, alias, generation) {
        if (generation !== state.loadGeneration) return;
        state.sourceStatus[alias] = 'loading';
        const prior = state.sourceControllers.get(alias);
        try { prior?.abort(); } catch (_) { }
        const controller = new AbortController();
        state.sourceControllers.set(alias, controller);
        const endpoint = new URL(state.cfg.sourceEndpoint || '../Dashboard/GetCustomHtmlLiveData', global.location.href).toString();
        try {
            const response = await fetch(endpoint, {
                method: 'POST',
                credentials: 'same-origin',
                signal: controller.signal,
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    templateId: state.cfg.key || '',
                    payloadMode: 'csrComposite',
                    connectionName: 'csr_pbip_source',
                    schema: 'dbo',
                    obj: alias,
                    sourceAlias: alias,
                    maxCells: 200000
                })
            });
            const result = await readJsonResponse(response, 'CSR source request');
            if (generation !== state.loadGeneration) return;
            const sets = result.dataSets || result.datasets || {};
            const rows = Array.isArray(sets[alias]) ? sets[alias]
                : (Array.isArray(result.data) ? result.data : []);
            state.dataSets[alias] = rows;
            const meta = (Array.isArray(result.sources) ? result.sources : []).find(item => norm(item.alias || item.semanticEntity) === norm(alias))
                || { alias, returnedRows: rows.length, error: null };
            state.sourceMeta = sourceMetaList(state).filter(item => norm(item.alias || item.semanticEntity) !== norm(alias));
            state.sourceMeta.push(meta);
            state.sourceStatus[alias] = meta.error ? 'error' : 'loaded';
        } catch (err) {
            if (err?.name === 'AbortError' || generation !== state.loadGeneration) return;
            state.dataSets[alias] = [];
            state.sourceStatus[alias] = 'error';
            state.sourceMeta = sourceMetaList(state).filter(item => norm(item.alias || item.semanticEntity) !== norm(alias));
            state.sourceMeta.push({ alias, semanticEntity: alias, returnedRows: 0, error: String(err?.message || err) });
        } finally {
            if (state.sourceControllers.get(alias) === controller) state.sourceControllers.delete(alias);
            if (generation === state.loadGeneration) {
                state.relationshipCache.clear();
                refreshVisuals(state, visual => visualUsesAlias(visual, alias));
            }
        }
    }

    function loadAllCsrSources(state) {
        const generation = (state.loadGeneration || 0) + 1;
        state.loadGeneration = generation;
        for (const controller of state.sourceControllers.values()) {
            try { controller.abort(); } catch (_) { }
        }
        state.sourceControllers.clear();

        if (isMonthlyEbnotesPage(state)) return loadMonthlyEbnotesPage(state);
        if (isCustomerPaymentsPage(state)) return loadCustomerPaymentsPage(state);
        if (isAgingReportPage(state)) return loadAgingReportPage(state);
        const aliases = configuredSourceAliases(state);
        if (!aliases.length) return Promise.resolve([]);
        aliases.forEach(alias => { state.sourceStatus[alias] = 'loading'; });
        if (state.hasRendered) refreshAllVisuals(state); else renderPage(state);
        return Promise.allSettled(aliases.map(alias => fetchCsrSource(state, alias, generation)));
    }

    class CsrVisualComponent {
        constructor(name) {
            this.name = name;
        }

        render(_context) {
            throw new Error(`CSR component '${this.name}' does not implement render().`);
        }
    }

    class CsrChartComponent extends CsrVisualComponent {
        constructor(name, chartKind) {
            super(name);
            this.chartKind = chartKind;
        }

        finalizeOption(_context, option) {
            return option;
        }

        render({ state, visual, host }) {
            renderChart(state, visual, host, this.chartKind, this);
        }
    }

    // Base class for every chart that contains bars.  All label wrapping,
    // compact-tile geometry, zero-inclusive value axes and bar sizing live here.
    class CsrBarChartComponent extends CsrChartComponent {
        constructor(name, chartKind, options = {}) {
            super(name, chartKind);
            this.horizontal = Boolean(options.horizontal);
            this.stacked = Boolean(options.stacked);
            this.combo = Boolean(options.combo);
        }

        finalizeOption(context, option) {
            return applyBarChartClassPolicy(
                context.state,
                context.visual,
                context.host,
                option,
                this
            );
        }
    }

    class CsrColumnChartComponent extends CsrBarChartComponent {
        constructor(name = 'column-chart', chartKind = 'column', options = {}) {
            super(name, chartKind, { ...options, horizontal: false });
        }
    }

    class CsrStackedColumnChartComponent extends CsrColumnChartComponent {
        constructor(name = 'stacked-column-chart', chartKind = 'stacked-column', options = {}) {
            super(name, chartKind, { ...options, stacked: true });
        }
    }

    class CsrHorizontalBarChartComponent extends CsrBarChartComponent {
        constructor(name = 'horizontal-bar-chart', chartKind = 'horizontal-bar', options = {}) {
            super(name, chartKind, { ...options, horizontal: true });
        }
    }

    class CsrStackedHorizontalBarChartComponent extends CsrHorizontalBarChartComponent {
        constructor() {
            super('stacked-horizontal-bar-chart', 'stacked-horizontal-bar', { stacked: true });
        }
    }

    // Combo is a true stacked-column subclass; it inherits all bar behavior and
    // leaves its Y2 line series untouched.
    class CsrStackedColumnLineComboComponent extends CsrStackedColumnChartComponent {
        constructor() {
            super('stacked-column-line-combo', 'stacked-combo', { combo: true });
        }
    }

    class CsrLineChartComponent extends CsrChartComponent {
        constructor() { super('line-chart', 'line'); }
    }

    class CsrPieComponent extends CsrChartComponent {
        constructor() { super('pie-chart', 'pie'); }
    }

    class CsrDonutComponent extends CsrChartComponent {
        constructor() { super('donut-chart', 'donut'); }
    }

    class CsrTableComponent extends CsrVisualComponent {
        constructor() { super('table'); }
        render({ state, visual, host }) { renderTable(state, visual, host); }
    }

    class CsrMatrixComponent extends CsrVisualComponent {
        constructor() { super('matrix'); }
        render({ state, visual, host }) { renderPivot(state, visual, host); }
    }

    class CsrKpiComponent extends CsrVisualComponent {
        constructor(multiRow) {
            super(multiRow ? 'multi-row-kpi' : 'kpi');
            this.multiRow = multiRow;
        }
        render({ state, visual, host }) { renderCard(state, visual, host, this.multiRow); }
    }

    class CsrSlicerComponent extends CsrVisualComponent {
        constructor() { super('slicer'); }
        render({ state, visual, host }) { renderSlicer(state, visual, host); }
    }

    class CsrMapComponent extends CsrVisualComponent {
        constructor() { super('map'); }
        render({ state, visual, host }) { renderMap(state, visual, host); }
    }

    class CsrTextComponent extends CsrVisualComponent {
        constructor() { super('text'); }
        render({ visual, host }) { renderTextbox(visual, host); }
    }

    class CsrActionComponent extends CsrVisualComponent {
        constructor() { super('action'); }
        render({ visual, host }) {
            host.classList.add('csr-action');
            host.innerHTML = `<div>${esc(visual.title || visual.text || 'Details')}</div>`;
            if (String(visual.options?.action || '').toLowerCase() === 'back') {
                host.addEventListener('click', () => global.history.back(), { once: true });
            }
        }
    }

    class CsrUnsupportedComponent extends CsrVisualComponent {
        constructor() { super('unsupported'); }
        render({ visual, host }) {
            host.innerHTML = `<div class="csr-empty">Unsupported visual: ${esc(effectiveVisualType(visual) || 'unknown')}</div>`;
        }
    }

    class CsrComponentRegistry {
        constructor() {
            this.components = new Map();
            this.fallback = new CsrUnsupportedComponent();
        }

        register(types, component) {
            (Array.isArray(types) ? types : [types]).forEach(type => {
                this.components.set(String(type || '').trim().toLowerCase(), component);
            });
            return this;
        }

        resolve(type) {
            return this.components.get(String(type || '').trim().toLowerCase()) || this.fallback;
        }

        describe() {
            return Array.from(this.components.entries()).map(([type, component]) => ({
                type,
                component: component.name
            }));
        }
    }

    // A single shared column component instance serves every ordinary columnChart,
    // including both Category Balance by Aging and Overdue Balance by Category.
    const CSR_COLUMN_COMPONENT = new CsrColumnChartComponent();

    const CSR_COMPONENTS = new CsrComponentRegistry()
        .register('columnChart', CSR_COLUMN_COMPONENT)
        .register('stackedColumnChart', new CsrStackedColumnChartComponent())
        .register('barChart', new CsrHorizontalBarChartComponent())
        .register('stackedBarChart', new CsrStackedHorizontalBarChartComponent())
        .register('lineChart', new CsrLineChartComponent())
        .register('lineStackedColumnComboChart', new CsrStackedColumnLineComboComponent())
        .register(['pieChart', 'pie'], new CsrPieComponent())
        .register(['donutChart', 'donut'], new CsrDonutComponent())
        .register('tableEx', new CsrTableComponent())
        .register('pivotTable', new CsrMatrixComponent())
        .register('card', new CsrKpiComponent(false))
        .register('multiRowCard', new CsrKpiComponent(true))
        .register('slicer', new CsrSlicerComponent())
        .register('map', new CsrMapComponent())
        .register('textbox', new CsrTextComponent())
        .register('actionButton', new CsrActionComponent());


    function renderVisual(state, visual, host) {
        try {
            if (isRedundantFlowVisual(visual)) { host.innerHTML = ''; return; }
            if (visualIsLoading(state, visual)) { host.innerHTML = loadingMarkup(visual); return; }
            const visualType = effectiveVisualType(visual);
            const component = CSR_COMPONENTS.resolve(visualType);
            host.dataset.csrComponent = component.name;
            component.render({ state, visual, host, visualType });
        } catch (err) {
            console.error('CSR visual render failed', visual, err);
            host.innerHTML = `<div class="csr-error">${esc(err?.message || err)}</div>`;
        }
    }


    function visualUsesAlias(visual, alias) {
        const target = norm(alias);
        const entities = visualEntities(visual);
        if (!entities.length) return true;
        return entities.some(entity => norm(entity) === target);
    }

    function renderVisualIntoHost(state, visual, host) {
        state.activeVisualId = visual?.id || '';
        try {
            renderVisual(state, visual, host);
        } finally {
            state.activeVisualId = '';
        }
    }

    function disposeChartsInHost(state, host) {
        if (!host) return;
        host._csrMapToken = Symbol('disposed-map');
        if (host._csrLeafletMap) {
            try { host._csrLeafletMap.remove(); } catch (_) { }
            const removed = host._csrLeafletMap;
            host._csrLeafletMap = null;
            state.maps = state.maps.filter(map => map !== removed);
        }
        if (global.echarts) {
            host.querySelectorAll('.csr-chart').forEach(el => {
                try {
                    const chart = global.echarts.getInstanceByDom(el);
                    if (chart) chart.dispose();
                } catch (_) { }
            });
            state.charts = state.charts.filter(chart => {
                try { return !chart?.isDisposed?.(); } catch (_) { return false; }
            });
        }
    }

    function resizeChartsSoon(state) {
        resizeInteractiveSoon(state);
    }

    function refreshVisuals(state, predicate) {
        closeSlicerPopover(state);
        if (!state.hasRendered || !state.visualHosts || !state.visualHosts.size) {
            renderPage(state);
            return;
        }
        for (const visual of renderableVisuals(state)) {
            if (predicate && !predicate(visual)) continue;
            const host = state.visualHosts.get(String(visual.id || ''));
            if (!host) continue;
            if (String(effectiveVisualType(visual) || '').toLowerCase() === 'tableex' && state.tableObserver) {
                try { state.tableObserver.disconnect(); } catch (_) { }
                state.tableObserver = null;
            }
            disposeChartsInHost(state, host);
            host.className = 'csr-visual-inner';
            host.innerHTML = '';
            renderVisualIntoHost(state, visual, host);
        }
        resizeChartsSoon(state);
    }

    function refreshAllVisuals(state) {
        refreshVisuals(state, null);
    }

    function clampNumber(value, min, max) {
        return Math.max(min, Math.min(max, Number(value) || 0));
    }

    function normalizedVisualLayout(value) {
        const out = {};
        if (!value || typeof value !== 'object' || Array.isArray(value)) return out;
        Object.entries(value).forEach(([id, geometry]) => {
            if (!geometry || typeof geometry !== 'object') return;
            out[String(id)] = {
                x: clampNumber(geometry.x, 0, 100),
                y: clampNumber(geometry.y, 0, 100),
                w: clampNumber(geometry.w, 2, 100),
                h: clampNumber(geometry.h, 2, 100),
                z: Number.isFinite(Number(geometry.z)) ? Number(geometry.z) : 0
            };
        });
        return out;
    }

    function renderableVisuals(stateOrConfig) {
        const visuals = Array.isArray(stateOrConfig?.cfg?.visuals)
            ? stateOrConfig.cfg.visuals
            : (Array.isArray(stateOrConfig?.visuals) ? stateOrConfig.visuals : []);
        return visuals.filter(visual => !isRedundantFlowVisual(visual));
    }

    function rawVisualGeometry(visual) {
        const base = visual?.position || {};
        const w = clampNumber(base.w ?? 10, 2, 100);
        const h = clampNumber(base.h ?? 10, 2, 100);
        return {
            id: String(visual?.id || ''),
            type: effectiveVisualType(visual),
            x: clampNumber(base.x ?? 0, 0, Math.max(0, 100 - w)),
            y: clampNumber(base.y ?? 0, 0, Math.max(0, 100 - h)),
            w,
            h,
            z: Number(base.z ?? 0) || 0
        };
    }

    function normalizedDefaultVisualLayout(visuals) {
        const entries = (visuals || []).filter(visual => !isRedundantFlowVisual(visual)).map(rawVisualGeometry);
        if (!entries.length) return {};

        entries.forEach(g => {
            if (g.x <= 1.6) g.x = 0;
            if (100 - (g.x + g.w) <= 1.6) g.w = 100 - g.x;
            if (g.y <= .8) g.y = 0;
            g.x = clampNumber(g.x, 0, Math.max(0, 100 - g.w));
            g.y = clampNumber(g.y, 0, Math.max(0, 100 - g.h));
        });

        // Group by vertical overlap/centre so tiny PBIP coordinate differences do not
        // create separate rows or let dropdowns sit on top of one another.
        const rows = [];
        entries.slice().sort((a, b) => a.y - b.y || a.x - b.x).forEach(g => {
            const top = g.y, bottom = g.y + g.h, centre = (top + bottom) / 2;
            let row = rows.find(candidate => {
                const candidateHeight = Math.max(1, candidate.height || g.h);
                const overlap = Math.min(bottom, candidate.bottom) - Math.max(top, candidate.top);
                const overlapRatio = overlap > 0
                    ? overlap / Math.max(1, Math.min(g.h, candidateHeight))
                    : 0;
                const topTolerance = Math.max(1.25, Math.min(3.25, Math.min(g.h, candidateHeight) * .42));
                const centreTolerance = Math.max(2.25, Math.min(5, Math.min(g.h, candidateHeight) * .65));
                return Math.abs(top - candidate.top) <= topTolerance
                    || (overlapRatio >= .65 && Math.abs(centre - candidate.centre) <= centreTolerance);
            });
            if (!row) { row = { top, bottom, centre, height: g.h, items: [] }; rows.push(row); }
            row.items.push(g);
            row.top = Math.min(row.top, top);
            row.bottom = Math.max(row.bottom, bottom);
            row.height = row.bottom - row.top;
            row.centre = row.items.reduce((sum, item) => sum + item.y + item.h / 2, 0) / row.items.length;
        });
        rows.sort((a, b) => a.top - b.top);

        const gap = .55;
        let previousBottom = 0;
        rows.forEach((row, rowIndex) => {
            const items = row.items.sort((a, b) => a.x - b.x);
            const allSlicers = items.length > 0 && items.every(item => item.type === 'slicer');
            let rowTop = Math.max(row.top, rowIndex ? previousBottom + gap : 0);
            const verticalShift = rowTop - row.top;
            items.forEach(item => { item.y = clampNumber(item.y + verticalShift, 0, Math.max(0, 100 - item.h)); });

            if (allSlicers) {
                const commonHeight = clampNumber(items.reduce((sum, item) => sum + item.h, 0) / items.length, 4, 12);
                items.forEach(item => { item.y = rowTop; item.h = commonHeight; });
            } else {
                items.forEach(item => { item.y = rowTop; });
            }

            if (items.length > 1) {
                const available = Math.max(10, 100 - gap * (items.length - 1));
                const weights = allSlicers ? items.map(() => 1) : items.map(item => Math.max(2, item.w));
                const weightTotal = weights.reduce((sum, value) => sum + value, 0) || items.length;
                let cursor = 0;
                items.forEach((item, index) => {
                    item.x = cursor;
                    item.w = index === items.length - 1
                        ? 100 - cursor
                        : Math.max(2, available * (weights[index] / weightTotal));
                    cursor = item.x + item.w + gap;
                });
            } else if (items.length === 1) {
                const item = items[0];
                if (item.w >= 70 || ['map', 'tableEx', 'pivotTable', 'columnChart', 'barChart', 'lineChart'].includes(item.type)) {
                    item.x = 0; item.w = 100;
                }
            }

            previousBottom = Math.max(previousBottom, ...items.map(item => item.y + item.h));
        });

        const out = {};
        entries.forEach(g => { out[g.id] = { x: g.x, y: g.y, w: g.w, h: g.h, z: g.z }; });
        return out;
    }

    function canEditVisualLayout(state) {
        return state?.cfg?.enableVisualLayoutEdit !== false && renderableVisuals(state).length > 1;
    }

    function visualGeometry(state, visual) {
        const id = String(visual?.id || '');
        const base = state.normalizedDefaultLayout?.[id] || rawVisualGeometry(visual);
        const override = state.visualLayoutOverrides?.[id] || {};
        const w = clampNumber(override.w ?? base.w ?? 10, 2, 100);
        const h = clampNumber(override.h ?? base.h ?? 10, 2, 100);
        return {
            x: clampNumber(override.x ?? base.x ?? 0, 0, Math.max(0, 100 - w)),
            y: clampNumber(override.y ?? base.y ?? 0, 0, Math.max(0, 100 - h)),
            w,
            h,
            z: Number(override.z ?? base.z ?? 0) || 0
        };
    }

    function applyVisualGeometry(section, geometry) {
        if (!section || !geometry) return;
        section.style.left = `${geometry.x}%`;
        section.style.top = `${geometry.y}%`;
        section.style.width = `${geometry.w}%`;
        section.style.height = `${geometry.h}%`;
        section.style.zIndex = String(geometry.z || 0);
    }

    function publishVisualLayout(state) {
        try {
            global.parent.postMessage({
                type: 'csr-dashboard-visual-layout:changed',
                templateId: state?.cfg?.key || '',
                visualLayout: state.visualLayoutOverrides || {}
            }, global.location.origin);
        } catch (_) { }
    }

    function applyExternalVisualLayout(state, value) {
        state.visualLayoutOverrides = normalizedVisualLayout(value);
        for (const visual of renderableVisuals(state)) {
            const section = state.visualSections?.get(String(visual.id || ''));
            if (section) applyVisualGeometry(section, visualGeometry(state, visual));
        }
        resizeChartsSoon(state);
    }

    function enableVisualLayoutHandles(state, page, section, visual) {
        if (!canEditVisualLayout(state) || !page || !section) return;
        const visualId = String(visual?.id || '');
        const move = document.createElement('button');
        move.type = 'button';
        move.className = 'csr-layout-move';
        move.setAttribute('aria-label', `Move ${visual?.title || 'visual'}`);
        move.title = 'Drag visual. Double-click to reset position.';
        move.innerHTML = '<span aria-hidden="true">⠿</span>';
        section.appendChild(move);
        const resize = document.createElement('span');
        resize.className = 'csr-layout-resize';
        resize.title = 'Resize visual';
        section.appendChild(resize);

        const begin = (event, mode) => {
            if (event.button !== 0) return;
            event.preventDefault();
            event.stopPropagation();
            const pageRect = page.getBoundingClientRect();
            if (!pageRect.width || !pageRect.height) return;
            const start = visualGeometry(state, visual);
            const pointerX = event.clientX;
            const pointerY = event.clientY;
            state.layoutZ = Math.max(state.layoutZ || 0, start.z || 0) + 1;
            start.z = state.layoutZ;
            applyVisualGeometry(section, start);
            section.classList.add('csr-layout-active');
            page.classList.add('csr-layout-changing');
            try { event.currentTarget.setPointerCapture(event.pointerId); } catch (_) { }

            const onMove = moveEvent => {
                const dx = ((moveEvent.clientX - pointerX) / pageRect.width) * 100;
                const dy = ((moveEvent.clientY - pointerY) / pageRect.height) * 100;
                const next = { ...start };
                if (mode === 'move') {
                    next.x = clampNumber(start.x + dx, 0, Math.max(0, 100 - start.w));
                    next.y = clampNumber(start.y + dy, 0, Math.max(0, 100 - start.h));
                } else {
                    next.w = clampNumber(start.w + dx, 4, Math.max(4, 100 - start.x));
                    next.h = clampNumber(start.h + dy, 4, Math.max(4, 100 - start.y));
                }
                applyVisualGeometry(section, next);
                resizeChartsSoon(state);
            };

            const finish = () => {
                global.removeEventListener('pointermove', onMove, true);
                global.removeEventListener('pointerup', finish, true);
                global.removeEventListener('pointercancel', finish, true);
                section.classList.remove('csr-layout-active');
                page.classList.remove('csr-layout-changing');
                const pageBox = page.getBoundingClientRect();
                const box = section.getBoundingClientRect();
                const geometry = {
                    x: clampNumber(((box.left - pageBox.left) / pageBox.width) * 100, 0, 100),
                    y: clampNumber(((box.top - pageBox.top) / pageBox.height) * 100, 0, 100),
                    w: clampNumber((box.width / pageBox.width) * 100, 4, 100),
                    h: clampNumber((box.height / pageBox.height) * 100, 4, 100),
                    z: Number(section.style.zIndex || start.z || 0)
                };
                geometry.x = clampNumber(geometry.x, 0, Math.max(0, 100 - geometry.w));
                geometry.y = clampNumber(geometry.y, 0, Math.max(0, 100 - geometry.h));
                state.visualLayoutOverrides[visualId] = geometry;
                publishVisualLayout(state);
                resizeChartsSoon(state);
            };

            global.addEventListener('pointermove', onMove, true);
            global.addEventListener('pointerup', finish, true);
            global.addEventListener('pointercancel', finish, true);
        };

        move.addEventListener('pointerdown', event => begin(event, 'move'));
        resize?.addEventListener('pointerdown', event => begin(event, 'resize'));
        move.addEventListener('dblclick', event => {
            event.preventDefault();
            event.stopPropagation();
            delete state.visualLayoutOverrides[visualId];
            applyVisualGeometry(section, visualGeometry(state, visual));
            publishVisualLayout(state);
            resizeChartsSoon(state);
        });
    }


    function toggleInternalVisualFullscreen(state, section) {
        if (!section) return;
        const page = section.parentElement;
        const currentlyFullscreen = section.classList.contains('csr-visual-fullscreen');
        page?.querySelectorAll('.csr-visual-fullscreen').forEach(other => {
            if (other !== section) other.classList.remove('csr-visual-fullscreen');
        });
        section.classList.toggle('csr-visual-fullscreen', !currentlyFullscreen);
        page?.classList.toggle('csr-has-fullscreen-visual', !currentlyFullscreen);
        const button = section.querySelector('[data-csr-visual-act="fullscreen"]');
        if (button) {
            button.title = currentlyFullscreen ? 'Maximize visual' : 'Restore visual';
            button.innerHTML = currentlyFullscreen ? '<span aria-hidden="true">⛶</span>' : '<span aria-hidden="true">↙</span>';
        }
        resizeInteractiveSoon(state);
    }

    function addInternalVisualMenu(state, page, section, visual) {
        if (norm(effectiveVisualType(visual)) === 'slicer') return;
        if (!section || section.querySelector('.csr-visual-menu')) return;
        const menu = document.createElement('div');
        menu.className = 'csr-visual-menu';
        const fullscreen = document.createElement('button');
        fullscreen.type = 'button';
        fullscreen.className = 'csr-visual-menu-btn';
        fullscreen.dataset.csrVisualAct = 'fullscreen';
        fullscreen.title = 'Maximize visual';
        fullscreen.setAttribute('aria-label', `Maximize ${visual?.title || 'visual'}`);
        fullscreen.innerHTML = '<span aria-hidden="true">⛶</span>';
        fullscreen.addEventListener('click', event => {
            event.preventDefault();
            event.stopPropagation();
            toggleInternalVisualFullscreen(state, section);
        });
        menu.appendChild(fullscreen);
        section.appendChild(menu);
    }

    function updateStandaloneThemeButton(state) {
        const button = state?.root?.querySelector('.csr-theme-toggle');
        if (!button) return;
        const dark = normalizeTheme(state.theme) === 'dark';
        button.setAttribute('aria-label', dark ? 'Switch CSR dashboard to light theme' : 'Switch CSR dashboard to dark theme');
        button.setAttribute('title', dark ? 'Light theme' : 'Dark theme');
        button.innerHTML = dark
            ? '<span aria-hidden="true">☀</span>'
            : '<span aria-hidden="true">☾</span>';
    }

    function persistTheme(theme) {
        try { global.localStorage?.setItem(CSR_THEME_STORAGE_KEY, normalizeTheme(theme)); } catch (_) { }
    }

    function applyTheme(state, theme, options = {}) {
        const normalized = normalizeTheme(theme);
        const changed = state.theme !== normalized;
        state.theme = normalized;
        document.documentElement.dataset.csrTheme = normalized;
        document.documentElement.style.colorScheme = normalized;
        if (document.body) document.body.dataset.csrTheme = normalized;
        if (state.root) state.root.dataset.csrTheme = normalized;

        if (options.persist) persistTheme(normalized);
        updateStandaloneThemeButton(state);

        if (options.notifyParent) {
            try {
                global.parent.postMessage({
                    type: 'csr-dashboard-theme:set',
                    theme: normalized,
                    templateId: state?.cfg?.key || ''
                }, global.location.origin);
            } catch (_) { }
        }

        if (changed && state.hasRendered) renderPage(state);
    }

    function addStandaloneThemeButton(state, page) {
        if (global.self !== global.top || !page || page.querySelector('.csr-theme-toggle')) return;
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'csr-theme-toggle';
        button.addEventListener('click', () => {
            const next = normalizeTheme(state.theme) === 'dark' ? 'light' : 'dark';
            applyTheme(state, next, { persist: true, notifyParent: false });
        });
        page.appendChild(button);
        updateStandaloneThemeButton(state);
    }

    function renderPage(state) {
        state.charts.forEach(c => { try { c.dispose(); } catch (_) { } });
        state.charts = [];
        state.maps.forEach(map => { try { map.remove(); } catch (_) { } });
        state.maps = [];
        if (state.tableObserver) {
            try { state.tableObserver.disconnect(); } catch (_) { }
            state.tableObserver = null;
        }
        closeSlicerPopover(state);
        const root = state.root;
        root.innerHTML = '<div class="csr-page"></div>';
        const page = root.firstElementChild;
        state.pageElement = page;
        state.visualHosts = new Map();
        state.visualSections = new Map();
        state.hasRendered = true;
        if (canEditVisualLayout(state)) page.classList.add('csr-layout-editable');
        addStandaloneThemeButton(state, page);
        const failedSources = sourceMetaList(state).filter(item => item?.error);
        const truncatedSources = sourceMetaList(state).filter(item => item?.truncated && !item?.error);
        if (failedSources.length || truncatedSources.length) {
            const banner = document.createElement('div');
            banner.className = 'csr-source-banner';
            const details = [
                ...failedSources.map(item => `${item.alias || item.object}: ${item.error}`),
                ...truncatedSources.map(item => `${item.alias || item.object}: row cap ${item.requestedTop} reached`)
            ];
            banner.title = details.join('\n');
            banner.textContent = failedSources.length
                ? `${failedSources.length} PBIP source${failedSources.length === 1 ? '' : 's'} failed — hover for connector details`
                : `${truncatedSources.length} PBIP source${truncatedSources.length === 1 ? '' : 's'} reached the row cap`;
            page.appendChild(banner);
        }
        state.layoutZ = 0;
        for (const visual of renderableVisuals(state)) {
            const visualType = norm(effectiveVisualType(visual));

            // CSR banner switch. When the csr-root element has this class,
            // textbox visuals are not added to the DOM at all.
            if (root.classList.contains('csr-hide-textbox-banners') && visualType === 'textbox') {
                continue;
            }

            const id = String(visual.id || '');
            const geometry = visualGeometry(state, visual);
            state.layoutZ = Math.max(state.layoutZ, Number(geometry.z || 0));
            const section = document.createElement('section');
            section.className = `csr-visual csr-type-${visualType}`;
            section.dataset.visualId = id;
            applyVisualGeometry(section, geometry);
            const inner = document.createElement('div');
            inner.className = 'csr-visual-inner';
            section.appendChild(inner);
            page.appendChild(section);
            state.visualHosts.set(id, inner);
            state.visualSections.set(id, section);
            addInternalVisualMenu(state, page, section, visual);
            renderVisualIntoHost(state, visual, inner);
            enableVisualLayoutHandles(state, page, section, visual);
        }
        resizeChartsSoon(state);
    }

    function extractPayloadData(state, incoming) {
        const envelope = incoming?.payload || incoming || {};
        const result = envelope.result || incoming?.result || {};
        const sets = result.dataSets || result.datasets || envelope.dataSets || incoming?.dataSets || {};
        state.payloadError = result.error || envelope.error || incoming?.error || null;
        state.serverFilteredVisualData = result.serverFilteredVisualData === true ||
            envelope.serverFilteredVisualData === true || incoming?.serverFilteredVisualData === true;
        state.sourceMeta = result.sources || envelope.sources || incoming?.sources || [];
        state.pageInfo = result.pageInfo || envelope.pageInfo || incoming?.pageInfo || null;
        state.queryContext = result.queryContext || envelope.queryContext || incoming?.queryContext || null;

        const finishPayload = () => {
            configuredSourceAliases(state).forEach(alias => {
                const meta = sourceMetaForEntity(state, alias);
                state.sourceStatus[alias] = meta?.error ? 'error' : 'loaded';
            });
            state.relationshipCache.clear();
        };

        if (sets && Object.keys(sets).length) {
            state.dataSets = {};
            Object.keys(sets).forEach(key => {
                const value = sets[key];
                state.dataSets[key] = Array.isArray(value)
                    ? value
                    : (Array.isArray(value?.rows) ? value.rows : []);
            });
            finishPayload();
            return;
        }

        const rows = Array.isArray(envelope.data)
            ? envelope.data
            : (Array.isArray(result.data) ? result.data : []);
        const sources = Array.isArray(state.cfg.sources) ? state.cfg.sources : [];
        const key = sources[0]?.alias || sources[0] || 'data';
        state.dataSets = { [key]: rows };
        finishPayload();
    }

    function injectStyles() {
        if (document.querySelector('link[data-csr-runtime-style]')) return;
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.dataset.csrRuntimeStyle = '1';
        const script = document.currentScript || Array.from(document.scripts).find(item => /csr-dashboard-runtime\.js/i.test(item.src));
        const base = script?.src || global.location.href;
        link.href = new URL('../css/csr-dashboard-runtime.css?v=20260721-csr-components-v4-force-bars', base).toString();
        document.head.appendChild(link);
    }

    function start(root, cfg) {
        if (!root) throw new Error('CSR root element is required.');
        injectStyles();
        const state = {
            root,
            cfg: cfg || {},
            dataSets: {},
            visualDataSets: {},
            pageInfoByVisual: {},
            queryContextByVisual: {},
            activeVisualId: '',
            serverFilteredVisualData: false,
            monthlyRequestNumber: 0,
            customerPaymentsRequestNumber: 0,
            sourceMeta: [],
            payloadError: null,
            slicerSelections: {},
            relationshipCache: new Map(),
            sourceStatus: {},
            activeSlicerPopover: null,
            refreshHandle: null,
            directSourceLoading: cfg?.directSourceLoading !== false,
            charts: [],
            maps: [],
            matrixCollapsed: new Set(),
            pageInfo: null,
            queryContext: null,
            tableLoading: false,
            tableObserver: null,
            theme: preferredTheme(),
            hasRendered: false,
            visualHosts: new Map(),
            visualSections: new Map(),
            visualLayoutOverrides: normalizedVisualLayout(cfg?.visualLayout || cfg?.visualLayouts || cfg?.layoutOverrides || {}),
            normalizedDefaultLayout: normalizedDefaultVisualLayout(cfg?.visuals || []),
            layoutZ: 0,
            sourceControllers: new Map(),
            monthlyAbortController: null,
            customerPaymentsAbortController: null,
            loadGeneration: 0
        };

        applyTheme(state, state.theme, { persist: false, notifyParent: false });
        configuredSourceAliases(state).forEach(alias => { state.sourceStatus[alias] = 'loading'; });
        renderPage(state);
        if (state.directSourceLoading) {
            loadAllCsrSources(state);
            const refreshSeconds = Math.max(0, Number(state.cfg.refreshSeconds || 0) || 0);
            if (refreshSeconds > 0) {
                state.refreshHandle = global.setInterval(() => loadAllCsrSources(state), refreshSeconds * 1000);
            }
        }

        const onMessage = event => {
            const msg = event?.data;
            if (!msg) return;

            if (msg.type === 'csr-dashboard-theme:apply' || msg.type === 'its-dashboard-theme') {
                applyTheme(state, msg.theme, { persist: false, notifyParent: false });
                return;
            }

            if (msg.type === 'dashboard-custom-html:layout') {
                applyExternalVisualLayout(state, msg?.payload?.visualLayout || msg?.visualLayout || {});
                return;
            }
            if (String(msg.type || '').endsWith(':resize')) {
                resizeChartsSoon(state);
                return;
            }
            if (msg.type && !String(msg.type).startsWith('dashboard-custom-html:')) return;
            const incomingLayout = msg?.payload?.config?.visualLayout || msg?.config?.visualLayout;
            if (incomingLayout) applyExternalVisualLayout(state, incomingLayout);
            if (state.directSourceLoading) return;
            extractPayloadData(state, msg);
            renderPage(state);
        };
        global.addEventListener('message', onMessage);

        const onKeyDown = event => {
            if (event.key !== 'Escape') return;
            const section = state.root?.querySelector('.csr-visual-fullscreen');
            if (section) toggleInternalVisualFullscreen(state, section);
        };
        global.addEventListener('keydown', onKeyDown);

        const onStorage = event => {
            if (event.key === CSR_THEME_STORAGE_KEY && (event.newValue === 'light' || event.newValue === 'dark')) {
                applyTheme(state, event.newValue, { persist: false, notifyParent: false });
            }
        };
        global.addEventListener('storage', onStorage);

        const ro = new ResizeObserver(() => {
            resizeInteractiveSoon(state);
        });
        ro.observe(root);

        try {
            global.parent.postMessage({
                type: 'csr-dashboard-theme:request',
                templateId: cfg?.key || ''
            }, global.location.origin);
            global.parent.postMessage({
                type: 'dashboard-custom-html:ready',
                templateId: cfg?.key || ''
            }, '*');
        } catch (_) { }

        return {
            state,
            render: () => renderPage(state),
            setTheme: theme => applyTheme(state, theme, { persist: true, notifyParent: true }),
            dispose: () => {
                ro.disconnect();
                if (state.tableObserver) {
                    try { state.tableObserver.disconnect(); } catch (_) { }
                    state.tableObserver = null;
                }
                closeSlicerPopover(state);
                if (state.refreshHandle) global.clearInterval(state.refreshHandle);
                try { state.monthlyAbortController?.abort(); } catch (_) { }
                for (const controller of state.sourceControllers.values()) { try { controller.abort(); } catch (_) { } }
                state.sourceControllers.clear();
                state.maps.forEach(map => { try { map.remove(); } catch (_) { } });
                state.maps = [];
                global.removeEventListener('message', onMessage);
                global.removeEventListener('keydown', onKeyDown);
                global.removeEventListener('storage', onStorage);
            }
        };
    }

    global.CsrDashboardRuntime = {
        start,
        components: CSR_COMPONENTS.describe(),
        __debug: {
            normalizeChartKind,
            wrapCategoryLabel,
            createChartOption,
            componentFor: type => CSR_COMPONENTS.resolve(type).name
        }
    };
})(window);