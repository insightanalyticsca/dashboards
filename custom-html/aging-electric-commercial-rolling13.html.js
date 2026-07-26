

const TITLE = "Electric Commercial - Rolling 13 Months - EoM, True Debt, Active Accounts";
let chart = null;

function rowsOf(source){
  if (Array.isArray(source)) return source;
  if (!source || typeof source !== 'object') return [];
  return rowsOf(
    source.rows ??
    source.model ??
    source.payload ??
    source.data ??
    source.result ??
    []
  );
}
function ciGet(row, keys){
  if (!row || typeof row !== 'object') return undefined;
  const map = row.__ci || (row.__ci = Object.fromEntries(Object.keys(row).map(k => [String(k).toLowerCase(), row[k]])));
  for (const key of keys){
    const hit = map[String(key).toLowerCase()];
    if (hit !== undefined && hit !== null && hit !== '') return hit;
  }
  return undefined;
}
function num(value){
  const n = Number(String(value ?? '').replace(/[,\s%]/g,''));
  return Number.isFinite(n) ? n : 0;
}
function fmtInt(value){
  return new Intl.NumberFormat('en-US',{maximumFractionDigits:0}).format(num(value));
}
function fmtPct(value){
  const n = num(value);
  return `${n >= 0 ? '' : ''}${n.toFixed(2)}%`;
}
function fmtShort(value){
  const n = num(value);
  const abs = Math.abs(n);
  if (abs >= 1000000) return (n/1000000).toFixed(abs >= 10000000 ? 0 : 1) + 'M';
  if (abs >= 1000) return (n/1000).toFixed(abs >= 100000 ? 0 : 1) + 'K';
  return String(Math.round(n));
}
function monthLabel(value){
  const d = new Date(value);
  if (Number.isFinite(d.getTime())) {
    return d.toLocaleString('en-US',{month:'short'});
  }
  const s = String(value ?? '');
  return s.length > 3 ? s.slice(0,3) : s;
}
function dateLabel(value){
  const d = new Date(value);
  if (Number.isFinite(d.getTime())) {
    const yyyy = d.getFullYear();
    const mm = String(d.getMonth()+1).padStart(2,'0');
    const dd = String(d.getDate()).padStart(2,'0');
    return `${yyyy}-${mm}-${dd}`;
  }
  return String(value ?? '');
}
function dashboardBridge(render, resize){
  const invoke = source => { try { render(source || {}); } catch (err) { console.warn('custom html render failed', err); } };
  window.addEventListener('message', ev => {
    const type = String(ev?.data?.type || '');
    if (!type.startsWith('dashboard-custom-html:')) return;
    if (type.endsWith(':resize')) { if (resize) resize(); return; }
    if (type.endsWith(':init') || type.endsWith(':update')) invoke(ev.data);
  });
  window.addEventListener('resize', () => { if (resize) resize(); });
  invoke(window.DashVisualContext || {});
}

function normalizeBucket(value){
  const raw = String(value ?? '').trim().toLowerCase();
  if (!raw) return '';
  if (raw === 'current' || raw === '0-30' || raw === '0 to 30' || raw === '0_30') return '0-30';
  if (raw === '31-60' || raw === '31 to 60' || raw === '31_60') return '31-60';
  if (raw === '61-90' || raw === '61 to 90' || raw === '61_90') return '61-90';
  if (raw === '>90' || raw === '90+' || raw === '91+' || raw === '91-120' || raw === 'over 90') return '>90';
  return '';
}
function buildModel(rows){
  const dateKeys = ['SelectedDate','Date','Period','MonthEnd','AsOfDate'];
  const bucketKeys = ['AgingBucket','Bucket','BucketName','Aging Bucket'];
  const amountKeys = ['Amount','Value','Balance','TrueDebt','Total'];
  const byMonth = new Map();
  rows.forEach(row => {
    const rawDate = ciGet(row, dateKeys);
    const bucket = normalizeBucket(ciGet(row, bucketKeys));
    const amount = num(ciGet(row, amountKeys));
    const dt = new Date(rawDate);
    if (!Number.isFinite(dt.getTime()) || !bucket) return;
    const key = `${dt.getFullYear()}-${String(dt.getMonth()+1).padStart(2,'0')}`;
    if (!byMonth.has(key)) byMonth.set(key, { date: new Date(dt.getFullYear(), dt.getMonth(), 1), buckets: {'0-30':0,'31-60':0,'61-90':0,'>90':0} });
    byMonth.get(key).buckets[bucket] += amount;
  });
  const ordered = Array.from(byMonth.values()).sort((a,b) => a.date - b.date).slice(-13);
  if (!ordered.length) return null;
  const labels = ordered.map(item => item.date.toLocaleString('en-US',{month:'short'}));
  const yearMarks = [];
  ordered.forEach((item, index) => {
    const year = String(item.date.getFullYear());
    const prev = index > 0 ? String(ordered[index-1].date.getFullYear()) : '';
    if (year !== prev) yearMarks.push({index, year});
  });
  const stack030 = ordered.map(item => item.buckets['0-30']);
  const stack3160 = ordered.map(item => item.buckets['31-60']);
  const stack6190 = ordered.map(item => item.buckets['61-90']);
  const stack90 = ordered.map(item => item.buckets['>90']);
  const total = ordered.map(item => item.buckets['0-30'] + item.buckets['31-60'] + item.buckets['61-90'] + item.buckets['>90']);
  return { labels, yearMarks, stack030, stack3160, stack6190, stack90, total };
}
function buildYearGraphics(model){
  if (!model?.yearMarks?.length) return [];
  return model.yearMarks.map(mark => ({
    type:'text',
    left: 54 + ((mark.index + 0.3) / Math.max(model.labels.length,1)) * (window.innerWidth - 92),
    bottom: 2,
    style:{ text: mark.year, fill:'#7a8196', font:'700 9px Segoe UI' }
  }));
}
function render(source){
  const rows = rowsOf(source);
  const model = buildModel(rows);
  document.getElementById('root').innerHTML = `
    <div class="tile">
      <div class="head"><div class="title">${TITLE}</div></div>
      <div class="legend">
        <span><i class="dot" style="background:#0808EE"></i>0-30</span>
        <span><i class="dot" style="background:#09C698"></i>31-60</span>
        <span><i class="dot" style="background:#4F63F7"></i>61-90</span>
        <span><i class="dot" style="background:#12DDB8"></i>&gt;90</span>
        <span><i class="dot" style="background:#171777"></i>Total</span>
      </div>
      <div class="chart-wrap"><div id="chart"></div></div>
    </div>`;
  const target = document.getElementById('chart');
  if (!model) {
    target.outerHTML = '<div class="empty">No SQL data.</div>';
    if (chart) { chart.dispose(); chart = null; }
    return;
  }
  chart = chart || echarts.init(document.getElementById('chart'));
  chart.setOption({
    animationDuration: 250,
    color: ['#0808EE','#09C698','#4F63F7','#12DDB8','#171777'],
    tooltip: {
      trigger:'axis',
      axisPointer:{type:'shadow'},
      backgroundColor:'#ffffff',
      borderColor:'#c9c9c9',
      textStyle:{color:'#171777',fontWeight:700},
      valueFormatter: value => fmtInt(value)
    },
    legend: { show:false },
    grid: { left: 46, right: 12, top: 10, bottom: 38 },
    graphic: buildYearGraphics(model),
    xAxis: {
      type:'category',
      data:model.labels,
      axisTick:{show:false},
      axisLine:{lineStyle:{color:'#c9c9c9'}},
      axisLabel:{color:'#65708c',fontSize:10,fontWeight:700,margin:8}
    },
    yAxis: {
      type:'value',
      axisTick:{show:false},
      axisLine:{show:false},
      splitLine:{lineStyle:{color:'#ececf1'}},
      axisLabel:{color:'#65708c',fontSize:10,fontWeight:700,formatter: value => fmtShort(value)}
    },
    series:[
      {name:'0-30',type:'bar',stack:'aging',barWidth:'46%',data:model.stack030},
      {name:'31-60',type:'bar',stack:'aging',barWidth:'46%',data:model.stack3160},
      {name:'61-90',type:'bar',stack:'aging',barWidth:'46%',data:model.stack6190},
      {name:'>90',type:'bar',stack:'aging',barWidth:'46%',data:model.stack90},
      {name:'Total',type:'line',data:model.total,symbol:'circle',symbolSize:6,smooth:false,lineStyle:{width:2.25},itemStyle:{color:'#171777'}}
    ]
  }, true);
  chart.resize();
}
dashboardBridge(render, ()=>{ if (chart) { chart.resize(); } });
