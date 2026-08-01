/*
  echarts-theme.js — Registers 'vivid' (light) and 'vivid-dark' ECharts themes.
  Loaded after echarts.min.js. The theme-toggle.js swaps which theme is active.
*/
(function () {
  if (typeof echarts === 'undefined') return;

  // ═══════════════════════════════════════════════════════════════════════════
  //  LIGHT THEME — 'vivid'
  // ═══════════════════════════════════════════════════════════════════════════
  echarts.registerTheme('vivid', {
    color: [
      '#6366F1', '#06B6D4', '#EC4899', '#10B981', '#F59E0B',
      '#818CF8', '#0E7490', '#BE185D', '#34D399', '#FBBF24'
    ],
    backgroundColor: 'transparent',
    textStyle: {
      fontFamily: 'Inter, system-ui, -apple-system, sans-serif',
      color: '#1e293b'
    },
    title: {
      textStyle: { color: '#171777', fontWeight: 700, fontSize: 13 },
      subtextStyle: { color: '#64748b', fontSize: 11 }
    },
    legend: {
      textStyle: { color: '#334155', fontSize: 11 },
      pageTextStyle: { color: '#475569' },
      icon: 'circle', itemWidth: 8, itemHeight: 8
    },
    tooltip: {
      backgroundColor: 'rgba(255,255,255,0.97)',
      borderColor: 'rgba(99,102,241,0.25)',
      borderWidth: 1,
      textStyle: { color: '#1e293b', fontSize: 12 },
      extraCssText: 'backdrop-filter:blur(10px); border-radius:10px; box-shadow:0 8px 32px rgba(0,0,0,0.12);'
    },
    axisPointer: {
      lineStyle: { color: 'rgba(99,102,241,0.35)', width: 1 },
      crossStyle: { color: 'rgba(99,102,241,0.25)' }
    },
    categoryAxis: {
      axisLine: { lineStyle: { color: 'rgba(0,0,0,0.12)' } },
      axisTick: { show: false },
      axisLabel: { color: '#475569', fontSize: 11 },
      splitLine: { show: false }
    },
    valueAxis: {
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: '#475569', fontSize: 11 },
      splitLine: { lineStyle: { color: 'rgba(0,0,0,0.10)', type: 'dashed' } }
    },
    line: {
      lineStyle: { width: 2.5, shadowColor: 'rgba(99,102,241,0.4)', shadowBlur: 6 },
      symbolSize: 6, symbol: 'circle', smooth: true
    },
    bar: {
      barMaxWidth: 32,
      itemStyle: { borderRadius: [4, 4, 0, 0], shadowColor: 'rgba(99,102,241,0.15)', shadowBlur: 4 }
    },
    pie: {
      itemStyle: { borderWidth: 2, borderColor: '#fff' }
    },
    radar: {
      axisName: { color: '#64748b' },
      splitLine: { lineStyle: { color: 'rgba(0,0,0,0.06)' } },
      splitArea: { areaStyle: { color: ['rgba(99,102,241,0.04)', 'transparent'] } }
    }
  });

  // ═══════════════════════════════════════════════════════════════════════════
  //  DARK THEME — 'vivid-dark'
  // ═══════════════════════════════════════════════════════════════════════════
  echarts.registerTheme('vivid-dark', {
    color: [
      '#818CF8', '#22D3EE', '#F472B6', '#34D399', '#FBBF24',
      '#A78BFA', '#06B6D4', '#EC4899', '#2DD4BF', '#FCD34D'
    ],
    backgroundColor: 'transparent',
    textStyle: {
      fontFamily: 'Inter, system-ui, -apple-system, sans-serif',
      color: '#f1f5f9'
    },
    title: {
      textStyle: { color: '#f1f5f9', fontWeight: 700, fontSize: 13 },
      subtextStyle: { color: '#94a3b8', fontSize: 11 }
    },
    legend: {
      textStyle: { color: '#dce4f0', fontSize: 11 },
      pageTextStyle: { color: '#cbd5e1' },
      icon: 'circle', itemWidth: 8, itemHeight: 8
    },
    tooltip: {
      backgroundColor: 'rgba(15,23,42,0.95)',
      borderColor: 'rgba(129,140,248,0.40)',
      borderWidth: 1,
      textStyle: { color: '#f8fafc', fontSize: 12 },
      extraCssText: 'backdrop-filter:blur(10px); border-radius:10px; box-shadow:0 8px 32px rgba(0,0,0,0.5);'
    },
    axisPointer: {
      lineStyle: { color: 'rgba(129,140,248,0.35)', width: 1 },
      crossStyle: { color: 'rgba(129,140,248,0.25)' }
    },
    categoryAxis: {
      axisLine: { lineStyle: { color: 'rgba(255,255,255,0.10)' } },
      axisTick: { show: false },
      axisLabel: { color: '#b0bcd4', fontSize: 11 },
      splitLine: { show: false }
    },
    valueAxis: {
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: '#b0bcd4', fontSize: 11 },
      splitLine: { lineStyle: { color: 'rgba(255,255,255,0.06)', type: 'dashed' } }
    },
    line: {
      lineStyle: { width: 2.5, shadowColor: 'rgba(129,140,248,0.5)', shadowBlur: 8 },
      symbolSize: 6, symbol: 'circle', smooth: true
    },
    bar: {
      barMaxWidth: 32,
      itemStyle: { borderRadius: [4, 4, 0, 0], shadowColor: 'rgba(129,140,248,0.25)', shadowBlur: 6 }
    },
    pie: {
      itemStyle: { borderWidth: 2, borderColor: 'rgba(15,23,42,0.6)' }
    },
    radar: {
      axisName: { color: '#94a3b8' },
      splitLine: { lineStyle: { color: 'rgba(255,255,255,0.08)' } },
      splitArea: { areaStyle: { color: ['rgba(129,140,248,0.06)', 'transparent'] } }
    }
  });

})();
