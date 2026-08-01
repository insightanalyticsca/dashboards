/* ════════════════════════════════════════════════════════════════════════════
   charts.js — Dazzle ECharts suite
   - Custom dark theme 'docchat' registered globally
   - 5 visuals:
     1. Liquid-fill gauge (system health)
     2. 3D-ish rose (document types)
     3. Animated area (uploads trend)
     4. Heatmap (query activity by hour)
     5. Radial bar (chunks per doc)
   Plus hero canvas: animated node network
   ════════════════════════════════════════════════════════════════════════════ */

(function (global) {
  'use strict';

  if (typeof echarts === 'undefined') {
    console.warn('ECharts not loaded — chart suite will be skipped.');
    return;
  }

  // ─── Palette (mirrors css :root) ─────────────────────────────────────────
  const PAL = {
    primary: '#6366F1',
    primaryDeep: '#4338CA',
    primarySoft: '#818CF8',
    accent: '#06B6D4',
    accentDeep: '#0E7490',
    hot: '#EC4899',
    success: '#10B981',
    warning: '#F59E0B',
    danger: '#EF4444',
    text: '#F1F5F9',
    textSoft: '#CBD5E1',
    muted: '#94A3B8',
    bg: 'transparent'
  };

  // ─── Register global theme ───────────────────────────────────────────────
  echarts.registerTheme('docchat', {
    color: [
      PAL.primary, PAL.accent, PAL.hot, PAL.success, PAL.warning,
      PAL.primarySoft, PAL.accentDeep, '#7C3AED', '#F472B6', '#34D399'
    ],
    backgroundColor: PAL.bg,
    textStyle: {
      fontFamily: 'Inter, system-ui, -apple-system, sans-serif',
      color: PAL.textSoft
    },
    title: {
      textStyle: { color: PAL.text, fontWeight: 700, fontSize: 13 },
      subtextStyle: { color: PAL.muted, fontSize: 11 }
    },
    legend: {
      textStyle: { color: PAL.muted, fontSize: 11 },
      pageTextStyle: { color: PAL.muted },
      icon: 'circle',
      itemWidth: 8,
      itemHeight: 8
    },
    tooltip: {
      backgroundColor: 'rgba(15, 23, 42, 0.92)',
      borderColor: 'rgba(255,255,255,0.12)',
      borderWidth: 1,
      textStyle: { color: '#F1F5F9', fontSize: 12 },
      extraCssText: 'backdrop-filter: blur(10px); border-radius: 10px; box-shadow: 0 8px 32px rgba(0,0,0,0.5);'
    },
    axisPointer: {
      lineStyle: { color: 'rgba(99, 102, 241, 0.35)', width: 1 },
      crossStyle: { color: 'rgba(99, 102, 241, 0.25)' }
    },
    categoryAxis: {
      axisLine: { lineStyle: { color: 'rgba(255,255,255,0.10)' } },
      axisTick: { show: false },
      axisLabel: { color: PAL.muted, fontSize: 11 },
      splitLine: { show: false }
    },
    valueAxis: {
      axisLine: { show: false },
      axisTick: { show: false },
      axisLabel: { color: PAL.muted, fontSize: 11 },
      splitLine: { lineStyle: { color: 'rgba(255,255,255,0.06)', type: 'dashed' } }
    },
    line: {
      lineStyle: { width: 2.5, shadowColor: 'rgba(99,102,241,0.6)', shadowBlur: 8 },
      symbolSize: 6,
      symbol: 'circle',
      smooth: true
    },
    bar: {
      barMaxWidth: 32,
      itemStyle: {
        borderRadius: [6, 6, 0, 0],
        shadowColor: 'rgba(99,102,241,0.25)',
        shadowBlur: 6
      }
    },
    pie: {
      itemStyle: {
        borderWidth: 2,
        borderColor: 'rgba(15,23,42,0.6)'
      }
    },
    radar: {
      axisName: { color: PAL.muted },
      splitLine: { lineStyle: { color: 'rgba(255,255,255,0.08)' } },
      splitArea: { areaStyle: { color: ['rgba(99,102,241,0.04)', 'transparent'] } }
    }
  });

  // ─── Chart instances (so app can dispose) ─────────────────────────────────
  const instances = [];

  function init(host, opts) {
    const chart = echarts.init(host, 'docchat', { renderer: 'canvas' });
    chart.setOption(opts);
    instances.push(chart);
    return chart;
  }

  // ─── Helper: animated gradient bar/area ──────────────────────────────────
  function linearGradVertical(topColor, bottomColor = 'transparent', topOpacity = 0.85, bottomOpacity = 0.05) {
    return new echarts.graphic.LinearGradient(0, 0, 0, 1, [
      { offset: 0, color: withAlpha(topColor, topOpacity) },
      { offset: 1, color: withAlpha(bottomColor === 'transparent' ? topColor : bottomColor, bottomOpacity) }
    ]);
  }
  function withAlpha(hex, a) {
    const m = hex.match(/^#([0-9a-f]{6})$/i);
    if (!m) return hex;
    const r = parseInt(m[1].slice(0,2), 16);
    const g = parseInt(m[1].slice(2,4), 16);
    const b = parseInt(m[1].slice(4,6), 16);
    return `rgba(${r},${g},${b},${a})`;
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  Chart 1 — Liquid-fill gauge (system health)
  // ══════════════════════════════════════════════════════════════════════════
  function renderHealthGauge(host, value) {
    const data = [];
    for (let i = 0; i < 3; i++) {
      data.push({
        value: value - i * 5,
        itemStyle: {
          color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
            { offset: 0, color: i === 0 ? PAL.primary : (i === 1 ? PAL.accent : PAL.primarySoft) },
            { offset: 1, color: i === 0 ? PAL.accent : (i === 1 ? PAL.hot : PAL.primary) }
          ])
        }
      });
    }

    init(host, {
      series: [{
        type: 'liquidFill',
        data,
        radius: '78%',
        center: ['50%', '50%'],
        amplitude: 6,
        waveLength: '70%',
        phase: 'auto',
        period: 2200,
        backgroundStyle: {
          color: 'rgba(99, 102, 241, 0.06)',
          borderColor: 'rgba(99, 102, 241, 0.30)',
          borderWidth: 1
        },
        outline: {
          itemStyle: {
            borderColor: 'rgba(99, 102, 241, 0.50)',
            borderWidth: 2,
            shadowBlur: 18,
            shadowColor: 'rgba(99, 102, 241, 0.55)'
          },
          borderDistance: 4
        },
        label: {
          formatter: () => `${Math.round(value * 100)}%\nHealth`,
          fontSize: 26,
          fontWeight: 800,
          color: PAL.text,
          insideColor: '#fff',
          textShadowColor: 'rgba(99,102,241,0.7)',
          textShadowBlur: 12
        }
      }]
    });
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  Chart 2 — Rose (document types)
  // ══════════════════════════════════════════════════════════════════════════
  function renderDocTypesRose(host, data) {
    const series = data.map(d => ({
      value: d.count,
      name: d.type,
      itemStyle: {
        color: new echarts.graphic.LinearGradient(0, 0, 1, 1, [
          { offset: 0, color: d.color },
          { offset: 1, color: withAlpha(d.color, 0.55) }
        ]),
        shadowColor: withAlpha(d.color, 0.6),
        shadowBlur: 14
      }
    }));

    init(host, {
      tooltip: { trigger: 'item', formatter: '{b}: {c} docs ({d}%)' },
      legend: {
        bottom: 0,
        left: 'center',
        data: data.map(d => d.type)
      },
      series: [{
        type: 'pie',
        radius: ['22%', '78%'],
        center: ['50%', '46%'],
        roseType: 'area',
        itemStyle: { borderRadius: 8, borderColor: 'rgba(15,23,42,0.4)', borderWidth: 2 },
        label: { color: PAL.textSoft, fontSize: 11 },
        labelLine: { lineStyle: { color: PAL.muted } },
        data: series,
        animationType: 'scale',
        animationEasing: 'elasticOut',
        animationDuration: 800,
        animationDelay: idx => idx * 80
      }]
    });
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  Chart 3 — Animated area (uploads trend over the week)
  // ══════════════════════════════════════════════════════════════════════════
  function renderUploadsTrend(host, data) {
    init(host, {
      tooltip: { trigger: 'axis' },
      grid: { top: 18, left: 38, right: 18, bottom: 26 },
      xAxis: {
        type: 'category',
        boundaryGap: false,
        data: data.map(d => d.day)
      },
      yAxis: { type: 'value', splitLine: { lineStyle: { color: 'rgba(255,255,255,0.06)' } } },
      series: [{
        type: 'line',
        smooth: true,
        symbol: 'circle',
        symbolSize: 8,
        data: data.map(d => d.count),
        lineStyle: { width: 3, color: PAL.primary, shadowColor: 'rgba(99,102,241,0.6)', shadowBlur: 10 },
        itemStyle: { color: PAL.accent, borderColor: '#fff', borderWidth: 2 },
        areaStyle: {
          color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
            { offset: 0, color: withAlpha(PAL.primary, 0.55) },
            { offset: 0.5, color: withAlpha(PAL.primary, 0.20) },
            { offset: 1, color: withAlpha(PAL.primary, 0) }
          ])
        },
        emphasis: {
          itemStyle: { borderColor: PAL.hot, borderWidth: 3 }
        },
        animationDuration: 1200,
        animationEasing: 'cubicOut'
      }]
    });
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  Chart 4 — Heatmap (query activity by hour)
  // ══════════════════════════════════════════════════════════════════════════
  function renderQueryHeatmap(host, data) {
    const hours = data.map(d => d.hour + ':00');
    const values = data.map(d => [d.hour, d.count]);
    const max = Math.max(...data.map(d => d.count));

    init(host, {
      tooltip: {
        formatter: p => `Hour ${p.data[0]}:00<br/>Queries: <b>${p.data[1]}</b>`
      },
      grid: { top: 14, left: 48, right: 14, bottom: 26 },
      xAxis: {
        type: 'category',
        data: hours,
        axisLabel: { color: PAL.muted, fontSize: 10 },
        splitArea: { show: false }
      },
      yAxis: {
        type: 'value',
        axisLabel: { color: PAL.muted, fontSize: 10 }
      },
      visualMap: {
        show: false,
        min: 0,
        max,
        inRange: { color: [withAlpha(PAL.primary, 0.10), PAL.accent, PAL.primary, PAL.hot] }
      },
      series: [{
        type: 'heatmap',
        data: values,
        renderItem: (params, api) => {
          const start = api.coord([api.value(0), 0]);
          const end = api.coord([api.value(0), api.value(1)]);
          const width = api.size([0, 0])[0] - 4;
          const height = Math.max(2, start[1] - end[1] - 2);
          const val = api.value(1);
          const t = max ? val / max : 0;
          return {
            type: 'rect',
            shape: { x: start[0] - width / 2, y: end[1], width, height },
            style: {
              fill: api.visual('color'),
              shadowColor: withAlpha(PAL.primary, 0.4),
              shadowBlur: t > 0.7 ? 12 : 0
            }
          };
        }
      }]
    });
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  Chart 5 — Radial bar (chunks per doc)
  // ══════════════════════════════════════════════════════════════════════════
  function renderChunksRadial(host, data) {
    const max = Math.max(...data.map(d => d.chunks));
    const colors = [PAL.primary, PAL.accent, PAL.hot, PAL.success, PAL.warning, PAL.primarySoft, PAL.accentDeep, '#7C3AED'];

    init(host, {
      tooltip: { trigger: 'item', formatter: '{b}: {c} chunks' },
      legend: { show: false },
      polar: { radius: ['18%', '85%'] },
      angleAxis: {
        max,
        startAngle: 90,
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: { show: false },
        splitLine: { show: false }
      },
      radiusAxis: {
        type: 'category',
        data: data.map(d => d.doc),
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: { color: PAL.muted, fontSize: 10 }
      },
      series: [{
        type: 'bar',
        coordinateSystem: 'polar',
        data: data.map((d, i) => ({
          value: d.chunks,
          itemStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 1, 0, [
              { offset: 0, color: withAlpha(colors[i % colors.length], 0.4) },
              { offset: 1, color: colors[i % colors.length] }
            ]),
            borderRadius: 8,
            shadowColor: withAlpha(colors[i % colors.length], 0.6),
            shadowBlur: 10
          }
        })),
        barWidth: 6,
        animationDelay: idx => idx * 70,
        animationEasing: 'elasticOut'
      }]
    });
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  Chart 6 — Stacked area (weekly queries & tokens)
  // ══════════════════════════════════════════════════════════════════════════
  function renderWeeklyTrend(host, data) {
    init(host, {
      tooltip: { trigger: 'axis' },
      legend: {
        top: 0, right: 0,
        data: ['Queries', 'Tokens (÷100)']
      },
      grid: { top: 32, left: 42, right: 16, bottom: 26 },
      xAxis: {
        type: 'category',
        boundaryGap: false,
        data: data.map(d => d.week)
      },
      yAxis: [
        { type: 'value', name: 'Queries', axisLabel: { color: PAL.muted }, splitLine: { lineStyle: { color: 'rgba(255,255,255,0.06)' } } },
        { type: 'value', name: 'Tokens', axisLabel: { color: PAL.muted } }
      ],
      series: [
        {
          name: 'Queries',
          type: 'line',
          smooth: true,
          symbol: 'circle',
          symbolSize: 6,
          data: data.map(d => d.queries),
          lineStyle: { width: 2.5, color: PAL.primary },
          itemStyle: { color: PAL.primary },
          areaStyle: { color: linearGradVertical(PAL.primary, PAL.primary, 0.40, 0.05) },
          animationDuration: 1200
        },
        {
          name: 'Tokens (÷100)',
          type: 'line',
          yAxisIndex: 1,
          smooth: true,
          symbol: 'diamond',
          symbolSize: 7,
          data: data.map(d => Math.round(d.tokens / 100)),
          lineStyle: { width: 2.5, color: PAL.hot, type: 'dashed' },
          itemStyle: { color: PAL.hot },
          areaStyle: { color: linearGradVertical(PAL.hot, PAL.hot, 0.30, 0.05) },
          animationDuration: 1400
        }
      ]
    });
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  Chart 7 — Gauge (avg confidence)
  // ══════════════════════════════════════════════════════════════════════════
  function renderConfidenceGauge(host, value) {
    init(host, {
      series: [{
        type: 'gauge',
        startAngle: 220,
        endAngle: -40,
        min: 0,
        max: 1,
        radius: '92%',
        center: ['50%', '55%'],
        progress: {
          show: true,
          width: 14,
          itemStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 1, 0, [
              { offset: 0, color: PAL.primary },
              { offset: 0.5, color: PAL.accent },
              { offset: 1, color: PAL.success }
            ])
          }
        },
        axisLine: {
          lineStyle: { width: 14, color: [[1, 'rgba(255,255,255,0.06)']] }
        },
        pointer: {
          length: '60%', width: 4,
          itemStyle: { color: PAL.hot, shadowColor: PAL.hot, shadowBlur: 12 }
        },
        anchor: { show: true, size: 12, itemStyle: { color: PAL.text } },
        axisTick: { show: false },
        splitLine: { show: false },
        axisLabel: { show: false },
        detail: {
          valueAnimation: true,
          formatter: v => `${(v * 100).toFixed(1)}%`,
          color: PAL.text,
          fontSize: 26,
          fontWeight: 800,
          offsetCenter: [0, '32%']
        },
        title: {
          offsetCenter: [0, '70%'],
          color: PAL.muted,
          fontSize: 11
        },
        data: [{ value, name: 'Avg Confidence' }]
      }]
    });
  }

  // ══════════════════════════════════════════════════════════════════════════
  //  Hero canvas — animated node network
  // ══════════════════════════════════════════════════════════════════════════
  function startHeroNetwork(canvas) {
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    let W = canvas.width = canvas.offsetWidth * devicePixelRatio;
    let H = canvas.height = canvas.offsetHeight * devicePixelRatio;
    ctx.scale(devicePixelRatio, devicePixelRatio);

    const nodes = [];
    const NODE_COUNT = 28;
    const colors = [PAL.primary, PAL.accent, PAL.hot, PAL.success];

    for (let i = 0; i < NODE_COUNT; i++) {
      nodes.push({
        x: Math.random() * canvas.offsetWidth,
        y: Math.random() * canvas.offsetHeight,
        vx: (Math.random() - 0.5) * 0.4,
        vy: (Math.random() - 0.5) * 0.4,
        r: 2 + Math.random() * 3,
        c: colors[Math.floor(Math.random() * colors.length)]
      });
    }

    let raf;
    function frame() {
      ctx.clearRect(0, 0, canvas.offsetWidth, canvas.offsetHeight);

      // Update positions
      for (const n of nodes) {
        n.x += n.vx;
        n.y += n.vy;
        if (n.x < 0 || n.x > canvas.offsetWidth) n.vx *= -1;
        if (n.y < 0 || n.y > canvas.offsetHeight) n.vy *= -1;
      }

      // Draw links
      for (let i = 0; i < nodes.length; i++) {
        for (let j = i + 1; j < nodes.length; j++) {
          const dx = nodes[i].x - nodes[j].x;
          const dy = nodes[i].y - nodes[j].y;
          const dist = Math.sqrt(dx * dx + dy * dy);
          if (dist < 130) {
            const alpha = (1 - dist / 130) * 0.32;
            ctx.strokeStyle = withAlpha(nodes[i].c, alpha);
            ctx.lineWidth = 0.8;
            ctx.beginPath();
            ctx.moveTo(nodes[i].x, nodes[i].y);
            ctx.lineTo(nodes[j].x, nodes[j].y);
            ctx.stroke();
          }
        }
      }

      // Draw nodes
      for (const n of nodes) {
        ctx.fillStyle = n.c;
        ctx.shadowColor = n.c;
        ctx.shadowBlur = 12;
        ctx.beginPath();
        ctx.arc(n.x, n.y, n.r, 0, Math.PI * 2);
        ctx.fill();
      }
      ctx.shadowBlur = 0;

      raf = requestAnimationFrame(frame);
    }
    frame();

    window.addEventListener('resize', () => {
      cancelAnimationFrame(raf);
      W = canvas.width = canvas.offsetWidth * devicePixelRatio;
      H = canvas.height = canvas.offsetHeight * devicePixelRatio;
      ctx.scale(devicePixelRatio, devicePixelRatio);
      frame();
    });
  }

  // ─── Resize handler ──────────────────────────────────────────────────────
  function resizeAll() {
    for (const c of instances) c.resize();
  }
  window.addEventListener('resize', resizeAll);

  // ─── Export ──────────────────────────────────────────────────────────────
  global.Dashboards StudioCharts = {
    renderHealthGauge,
    renderDocTypesRose,
    renderUploadsTrend,
    renderQueryHeatmap,
    renderChunksRadial,
    renderWeeklyTrend,
    renderConfidenceGauge,
    startHeroNetwork,
    resizeAll,
    palette: PAL
  };

})(window);
