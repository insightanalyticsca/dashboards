'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');

const root = path.resolve(__dirname, '..');
const settings = JSON.parse(fs.readFileSync(path.join(root, 'appsettings.json'), 'utf8').replace(/^\uFEFF/, ''));
const runtimeCode = fs.readFileSync(path.join(root, 'wwwroot', 'js', 'csr-dashboard-runtime.js'), 'utf8');
const echarts = { graphic: { LinearGradient: function () { return { type: 'gradient' }; } } };
const sandbox = {
  window: {}, echarts, console, setTimeout, clearTimeout, URL, URLSearchParams,
  AbortController, Map, Set, WeakMap, Date, Intl
};
sandbox.window.window = sandbox.window;
sandbox.window.echarts = echarts;
sandbox.window.setTimeout = setTimeout;
sandbox.window.clearTimeout = clearTimeout;
vm.createContext(sandbox);
vm.runInContext(runtimeCode, sandbox, { filename: 'csr-dashboard-runtime.js' });

const runtime = sandbox.window.CsrDashboardRuntime;
if (!runtime || !runtime.__debug) throw new Error('CSR runtime debug API is unavailable.');
const debug = runtime.__debug;
const templates = settings.Dashboard.CustomHtml.Templates;
const visuals = templates.filter(item => String(item.Role || '').toLowerCase() === 'csr-visual');
const chartTypes = new Set([
  'columnChart', 'stackedColumnChart', 'barChart', 'stackedBarChart',
  'lineChart', 'lineStackedColumnComboChart', 'pieChart', 'pie', 'donutChart', 'donut'
]);
const longLabels = ['Residential', 'Small Commercial', 'Large Commercial', 'Water', 'Municipal Services'];

function roleEntries(roles) {
  return Object.entries(roles || {}).flatMap(([role, specs]) => (specs || []).map(spec => ({ role, spec })));
}

function isCategoryRole(role) {
  return ['Category', 'X', 'Rows', 'Columns', 'Legend', 'Details'].includes(role);
}

function fieldValue(role, spec, categoryIndex, seriesIndex) {
  const property = String(spec.property || 'value');
  const lower = property.toLowerCase();
  if (role === 'Series') return ['Email', 'Phone', 'Other'][seriesIndex % 3];
  if (isCategoryRole(role)) {
    if (lower === 'year') return 2026;
    if (lower === 'month' || lower.includes('month_name')) return ['May', 'June', 'July'][categoryIndex % 3];
    if (lower === 'date' || lower.includes('selecteddate') || lower.includes('trans_date')) return `2026-0${(categoryIndex % 3) + 5}-01`;
    return longLabels[categoryIndex % longLabels.length];
  }
  if (lower.includes('percent') || lower.includes('pct') || property.includes('%')) return 72 + categoryIndex * 3 + seriesIndex;
  if (lower.includes('latitude')) return 43.2 + categoryIndex * .01;
  if (lower.includes('longitude') || lower.includes('longtitude')) return -79.8 - categoryIndex * .01;
  return (categoryIndex + 1) * 10 + seriesIndex * 3;
}

function syntheticState(visual) {
  const roles = visual.roles || {};
  const entries = roleEntries(roles);
  const entities = [...new Set(entries.map(item => item.spec.entity).filter(Boolean))];
  const dataSets = {};
  const aliases = {};
  for (const entity of entities) {
    const entityEntries = entries.filter(item => item.spec.entity === entity);
    aliases[entity] = {};
    entityEntries.forEach(({ spec }) => { aliases[entity][spec.property] = [spec.property]; });
    const rows = [];
    const hasSeries = entityEntries.some(item => item.role === 'Series');
    for (let categoryIndex = 0; categoryIndex < 5; categoryIndex++) {
      for (let seriesIndex = 0; seriesIndex < (hasSeries ? 3 : 1); seriesIndex++) {
        const row = {};
        for (const { role, spec } of entityEntries) {
          row[spec.property] = fieldValue(role, spec, categoryIndex, seriesIndex);
        }
        // Support the special measures retained from the PBIP conversion.
        row['E-Bill %'] ??= 42 + categoryIndex;
        row['Post Paid'] ??= -40 - categoryIndex;
        row['Balance'] ??= 100 + categoryIndex;
        rows.push(row);
      }
    }
    dataSets[entity] = rows;
  }
  return {
    theme: 'light',
    cfg: {
      width: 1280,
      height: 720,
      aliases,
      paletteLight: ['#845EF7', '#00D4FF', '#00E6A8', '#38BDF8', '#FFD166', '#4C6FFF', '#B7F34A', '#22D3EE', '#2DD4BF', '#7DD3FC']
    },
    dataSets,
    slicerSelections: {},
    relationshipCache: new Map(),
    sourceMeta: [],
    serverFilteredVisualData: false
  };
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const componentMap = new Map(runtime.components.map(item => [item.type, item.component]));
const wrappedProbe = debug.wrapCategoryLabel('Small Commercial Accounts', 10, 3);
assert(wrappedProbe.includes('\n'), 'wrapCategoryLabel does not split labels at spaces');
const results = [];
for (const template of visuals) {
  const visual = JSON.parse(JSON.stringify(template.VisualConfig || {}));
  const type = visual.type || template.VisualType;
  const component = debug.componentFor(type);
  assert(component !== 'unsupported', `${template.Key}: unsupported type ${type}`);
  if (!chartTypes.has(type)) continue;

  visual.filters = [];
  const state = syntheticState(visual);
  const host = {
    clientWidth: 430,
    clientHeight: 260,
    getBoundingClientRect() { return { width: 430, height: 260 }; }
  };
  const output = debug.createChartOption(state, visual, host);
  assert(output && output.option, `${template.Key}: no chart option`);
  const option = debug.finalizeChartOption(state, visual, host, output.option);
  const kind = debug.normalizeChartKind(type);

  if (!['pie', 'donut'].includes(kind)) {
    const categoryAxis = kind.includes('horizontal') ? option.yAxis : option.xAxis;
    assert(categoryAxis.axisLabel.rotate === 0, `${template.Key}: category labels rotate`);
    assert(Array.isArray(categoryAxis.data), `${template.Key}: category data missing`);
    const bars = option.series.filter(item => item.type === 'bar');
    if (bars.length) {
      const valueAxis = kind.includes('horizontal') ? option.xAxis : (Array.isArray(option.yAxis) ? option.yAxis[0] : option.yAxis);
      assert(Number(valueAxis.min) <= 0 && Number(valueAxis.max) >= 0, `${template.Key}: bar axis does not include zero`);
      assert(bars.every(item => Number(item.barMinHeight) > 0), `${template.Key}: barMinHeight missing`);
    }
  }

  if (type === 'lineStackedColumnComboChart') {
    const bars = option.series.filter(item => item.type === 'bar');
    const lines = option.series.filter(item => item.type === 'line');
    assert(bars.length > 0, `${template.Key}: combo has no bars`);
    assert(bars.every(item => item.stack === 'csr-primary-stack'), `${template.Key}: combo bars are not stacked`);
    assert(lines.every(item => Number(item.yAxisIndex) === 1), `${template.Key}: combo lines are not on Y2`);
  }

  const xTitle = String(visual.options?.xTitle || '').trim();
  const yTitle = String(visual.options?.yTitle || '').trim();
  if (xTitle && !kind.includes('horizontal')) assert(option.xAxis.name === xTitle, `${template.Key}: xTitle not applied`);
  if (yTitle && !kind.includes('horizontal')) {
    const yAxis = Array.isArray(option.yAxis) ? option.yAxis[0] : option.yAxis;
    assert(yAxis.name === yTitle, `${template.Key}: yTitle not applied`);
  }
  results.push({ key: template.Key, type, component, series: option.series.length });
}



// Exact regression for the recreated Overdue Balance by Category visual.
const recreatedKey = 'csr-v192-4f0c9a7b2e6d8c153a91';
const recreatedTemplate = visuals.find(item => item.Key === recreatedKey);
assert(recreatedTemplate, `${recreatedKey}: recreated template missing`);
const recreatedVisual = JSON.parse(JSON.stringify(recreatedTemplate.VisualConfig));
assert(debug.componentFor(recreatedVisual.type) === 'column-chart', `${recreatedKey}: not using shared column-chart class`);
const recreatedRows = [
  { CategoryGroup: 'Residential', Series: 'Balance Overdue', Amount: -26500000 },
  { CategoryGroup: 'Small Commercial', Series: 'Balance Overdue', Amount: -3200000 },
  { CategoryGroup: 'Large Commercial', Series: 'Balance Overdue', Amount: -5100000 },
  { CategoryGroup: 'Water', Series: 'Balance Overdue', Amount: -1300000 },
  { CategoryGroup: 'Wastewater', Series: 'Balance Overdue', Amount: -900000 }
];
const recreatedState = {
  theme: 'light',
  cfg: {
    width: 1280,
    height: 720,
    aliases: {
      agingcube_net: {
        CategoryGroup: ['CategoryGroup'],
        Series: ['Series'],
        Amount: ['Amount']
      }
    },
    paletteLight: ['#845EF7', '#00D4FF', '#00E6A8', '#38BDF8', '#FFD166']
  },
  dataSets: { agingcube_net: recreatedRows },
  visualDataSets: {},
  activeVisualId: '',
  slicerSelections: {},
  relationshipCache: new Map(),
  sourceMeta: [],
  serverFilteredVisualData: false
};
const compactHost = {
  clientWidth: 430,
  clientHeight: 136,
  getBoundingClientRect() { return { width: 430, height: 136 }; }
};
const recreatedOutput = debug.createChartOption(recreatedState, recreatedVisual, compactHost);
const recreatedOption = debug.finalizeChartOption(recreatedState, recreatedVisual, compactHost, recreatedOutput.option);
const recreatedAxis = recreatedOption.xAxis;
const recreatedValueAxis = Array.isArray(recreatedOption.yAxis) ? recreatedOption.yAxis[0] : recreatedOption.yAxis;
assert(recreatedOption.series.length === 1, `${recreatedKey}: expected one series`);
assert(recreatedOption.legend && recreatedOption.legend.show === false, `${recreatedKey}: single-series legend is visible`);
assert(recreatedAxis.axisLabel.rotate === 0, `${recreatedKey}: category labels rotate`);
assert(recreatedAxis.data.includes('Small\nCommercial'), `${recreatedKey}: Small Commercial is not wrapped`);
assert(recreatedAxis.data.includes('Large\nCommercial'), `${recreatedKey}: Large Commercial is not wrapped`);
assert(Number(recreatedOption.series[0].barWidth) >= 24, `${recreatedKey}: bar width was not applied`);
assert(Number(recreatedOption.series[0].barMinHeight) > 0, `${recreatedKey}: barMinHeight missing`);
assert(Number(recreatedValueAxis.min) < 0 && Number(recreatedValueAxis.max) === 0, `${recreatedKey}: negative balance axis is incorrect`);
console.log(`Recreated aging category visual validation passed (${recreatedKey}).`);

console.log(`CSR runtime chart validation passed (${results.length} chart visuals).`);
console.log(JSON.stringify(results, null, 2));
