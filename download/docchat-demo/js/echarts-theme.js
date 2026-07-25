/*
  Drop this in _Layout.cshtml (or a shared bundle) AFTER echarts.min.js loads.
  Then in Multi.cshtml change:
      echarts.init(host)
  to:
      echarts.init(host, 'vivid')
  That applies the palette + series styling to every chart automatically.
*/
(function () {
  if (typeof echarts === 'undefined') return;

  echarts.registerTheme('vivid', {
    color: [
      '#0808EE',   // electric blue   (0-30d)
      '#09C698',
      '#171777',
      '#c9c9c9',
      '#d4d4d9',
      '#BBFF05',
      '#4F63F7',
      '#12DDB8',
      '#635BCB',
      '#DDF76D'
    ],

    backgroundColor: 'transparent',

    textStyle: {
      fontFamily: 'system-ui, -apple-system, sans-serif',
      color: '#111827',
    },

    title: {
      textStyle:    { color: '#111827', fontWeight: 700 },
      subtextStyle: { color: '#6b7280' },
    },

    legend: {
      textStyle: { color: '#374151' },
      pageTextStyle: { color: '#374151' },
    },

    tooltip: {
      backgroundColor: 'rgba(11,18,32,0.92)',
      borderColor: 'rgba(255,255,255,0.12)',
      borderWidth: 1,
      textStyle: { color: '#f3f4f6', fontSize: 12 },
      extraCssText: 'backdrop-filter:blur(8px); border-radius:10px; box-shadow:0 4px 24px rgba(0,0,0,0.35)',
    },

    axisPointer: {
      lineStyle:  { color: 'rgba(8,8,238,0.35)', width: 1 },
      crossStyle: { color: 'rgba(8,8,238,0.25)' },
    },

    categoryAxis: {
      axisLine:       { lineStyle: { color: 'rgba(17,24,39,0.12)' } },
      axisTick:       { lineStyle: { color: 'rgba(17,24,39,0.12)' } },
      axisLabel:      { color: '#6b7280', fontSize: 11 },
      splitLine:      { lineStyle: { color: 'rgba(17,24,39,0.06)' } },
      splitArea:      { areaStyle: { color: ['rgba(8,8,238,0.02)', 'transparent'] } },
    },

    valueAxis: {
      axisLine:  { show: false },
      axisTick:  { show: false },
      axisLabel: { color: '#6b7280', fontSize: 11 },
      splitLine: { lineStyle: { color: 'rgba(17,24,39,0.07)', type: 'dashed' } },
    },

    line: {
      lineStyle:   { width: 2.5 },
      symbolSize:  6,
      symbol:      'circle',
      smooth:      true,
      emphasis: {
        lineStyle: { width: 3.5 },
        symbolSize: 9,
      },
    },

    bar: {
      barMaxWidth:  56,
      itemStyle: {
        borderRadius: [4, 4, 0, 0],
      },
      emphasis: {
        itemStyle: {
          shadowBlur: 12,
          shadowColor: 'rgba(8,8,238,0.30)',
        },
      },
    },

    pie: {
      itemStyle: {
        borderWidth: 2,
        borderColor: '#fff',
      },
      emphasis: {
        itemStyle: {
          shadowBlur: 20,
          shadowColor: 'rgba(0,0,0,0.25)',
        },
        label: { fontSize: 14, fontWeight: 700 },
      },
    },

    scatter: {
      symbolSize: 8,
      emphasis: {
        symbolSize: 12,
        itemStyle: { shadowBlur: 10, shadowColor: 'rgba(8,8,238,0.40)' },
      },
    },

    gauge: {
      axisLine: {
        lineStyle: {
          color: [
            [0.33, '#0808EE'],
            [0.66, '#09C698'],
            [1.00, '#BBFF05'],
          ],
          width: 12,
        },
      },
      progress:  { itemStyle: { shadowBlur: 8, shadowColor: 'rgba(8,8,238,0.40)' } },
      pointer:   { itemStyle: { color: '#0808EE' } },
      detail:    { color: '#111827', fontWeight: 900 },
      title:     { color: '#6b7280' },
    },

    radar: {
      axisLine:  { lineStyle: { color: 'rgba(17,24,39,0.12)' } },
      splitLine: { lineStyle: { color: 'rgba(17,24,39,0.08)' } },
      splitArea: { areaStyle: { color: ['rgba(8,8,238,0.04)', 'transparent'] } },
    },

    sankey: {
      lineStyle: { opacity: 0.25, curveness: 0.5 },
      emphasis:  { lineStyle: { opacity: 0.6 } },
    },
  });
})();
