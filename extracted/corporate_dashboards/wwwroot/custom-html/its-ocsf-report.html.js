

let donutChart = null;
let barChart = null;

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

function firstNonEmpty(row, names){
  const value = ciGet(row, names);
  return value == null ? '' : String(value);
}
function pctValue(raw){
  const value = num(raw);
  if (Math.abs(value) <= 1 && String(raw).indexOf('%') === -1) return value * 100;
  return value;
}
function summarize(rows){
  const out = { total:0, remediated:0, fail:0, soft:0, pass:0, completion:0, categories:{} };
  rows.forEach(row => {
    const status = firstNonEmpty(row, ['Status','RiskStatus','ComplianceStatus']) || 'OEB-PASS';
    const rem = firstNonEmpty(row, ['RemediationStatus','Remediation','State']).toLowerCase();
    const completion = pctValue(ciGet(row, ['CompletionPct','CompletionPercent','Completion','PctComplete']));
    const category = firstNonEmpty(row, ['Category','RiskCategory','OCSFCategory']) || 'Uncategorized';
    out.total += 1;
    if (rem === 'remediated') out.remediated += 1;
    if (status === 'OEB-FAIL') out.fail += 1;
    else if (status === 'OEB-SoftFail') out.soft += 1;
    else out.pass += 1;
    out.completion += completion;
    out.categories[category] = out.categories[category] || { fail:0, soft:0 };
    if (status === 'OEB-FAIL') out.categories[category].fail += 1;
    if (status === 'OEB-SoftFail') out.categories[category].soft += 1;
  });
  out.avgCompletion = Math.round(out.completion / Math.max(out.total, 1));
  out.failPct = Math.round(out.fail / Math.max(out.total, 1) * 100);
  out.softPct = Math.round(out.soft / Math.max(out.total, 1) * 100);
  return out;
}
function brandMarkup(){
  return `<div class="brand-lockup">
    <div class="brand-mark">
      <svg viewBox="0 0 100 100" aria-hidden="true">
        <circle cx="50" cy="50" r="46" fill="#171777"/>
        <path d="M50 10c12 10 20 24 20 40S62 80 50 90C38 80 30 66 30 50s8-30 20-40z" fill="#09C698"/>
        <circle cx="50" cy="50" r="13" fill="#BBFF05"/>
      </svg>
    </div>
    <div class="brand-text">
      <span>GrandBridge</span>
      <em>ENERGY</em>
    </div>
  </div>`;
}
function render(source){
  const rows = rowsOf(source);
  const root = document.getElementById('root');
  if (!rows.length){
    root.innerHTML = '<div class="ocsf-report"><div class="empty">No rows returned from vw_OCSF_Risks_Current.</div></div>';
    return;
  }
  const model = summarize(rows);
  root.innerHTML = `
    <div class="ocsf-report">
      <div class="report-header">
        <div>
          <div class="report-title">Ontario Cyber Security Framework Report</div>
          <div class="report-subtitle">ITS-OCSF Gap Assessment</div>
          <div class="snapshot-line">Snapshot Date: ${dateLabel(new Date())}</div>
        </div>
        ${brandMarkup()}
      </div>
      <div class="header-rule"></div>
      <div class="kpi-grid">
        <div class="kpi-card" style="--accent:#171777">
          <div class="kpi-icon">Σ</div>
          <div class="kpi-value">${fmtInt(model.total)}</div>
          <div class="kpi-label">Total Risks</div>
        </div>
        <div class="kpi-card" style="--accent:#09C698">
          <div class="kpi-icon">✓</div>
          <div class="kpi-value">${fmtInt(model.remediated)}</div>
          <div class="kpi-label">Remediated Items</div>
        </div>
        <div class="kpi-card" style="--accent:#0808EE">
          <div class="kpi-icon">!</div>
          <div class="kpi-value">${fmtInt(model.fail)}</div>
          <div class="kpi-label">OEB-FAIL (RRR Compliance)</div>
          <div class="progress-line"><span style="width:${model.failPct}%"></span></div>
          <div class="progress-text">${model.failPct}% of total</div>
        </div>
        <div class="kpi-card" style="--accent:#4F63F7">
          <div class="kpi-icon">△</div>
          <div class="kpi-value">${fmtInt(model.soft)}</div>
          <div class="kpi-label">OEB-SoftFail (Stretch)</div>
          <div class="progress-line"><span style="width:${model.softPct}%"></span></div>
          <div class="progress-text">${model.softPct}% of total</div>
        </div>
        <div class="kpi-card" style="--accent:#12DDB8">
          <div class="kpi-icon">◎</div>
          <div class="kpi-value">${model.avgCompletion}%</div>
          <div class="kpi-label">Average Completion</div>
        </div>
      </div>
      <div class="section-title">Overview</div>
      <div class="chart-grid">
        <div class="chart-panel">
          <div class="chart-title">Risk Distribution</div>
          <div class="chart-subtitle">Breakdown of cybersecurity risks by status</div>
          <div id="donut" class="pie-chart"></div>
          <div class="legend">
            <span><i class="dot" style="background:#0808EE"></i>OEB-FAIL</span>
            <span><i class="dot" style="background:#4F63F7"></i>OEB-SoftFail</span>
            <span><i class="dot" style="background:#09C698"></i>OEB-PASS</span>
          </div>
        </div>
        <div class="chart-panel">
          <div class="chart-title">Risk by Category</div>
          <div class="chart-subtitle">Open risks by OCSF category</div>
          <div id="bars" class="bar-chart"></div>
          <div class="legend">
            <span><i class="dot" style="background:#0808EE"></i>OEB-FAIL</span>
            <span><i class="dot" style="background:#4F63F7"></i>OEB-SoftFail</span>
          </div>
        </div>
      </div>
    </div>`;
  const donutEl = document.getElementById('donut');
  const barEl = document.getElementById('bars');
  donutChart && donutChart.dispose();
  barChart && barChart.dispose();
  donutChart = echarts.init(donutEl);
  barChart = echarts.init(barEl);
  donutChart.setOption({
    animationDuration: 300,
    color:['#0808EE','#4F63F7','#09C698'],
    tooltip:{trigger:'item',backgroundColor:'#fff',borderColor:'#c9c9c9',textStyle:{color:'#171777',fontWeight:700}},
    series:[{
      type:'pie',
      radius:['58%','78%'],
      center:['50%','48%'],
      label:{show:true,color:'#171777',fontWeight:800,fontSize:11,formatter:'{b}\n{c}'},
      labelLine:{length:10,length2:8},
      itemStyle:{borderColor:'#fff',borderWidth:2},
      data:[
        {name:'OEB-FAIL', value:model.fail},
        {name:'OEB-SoftFail', value:model.soft},
        {name:'OEB-PASS', value:model.pass}
      ]
    }]
  }, true);
  const categories = Object.keys(model.categories);
  const failData = categories.map(key => model.categories[key].fail || 0);
  const softData = categories.map(key => model.categories[key].soft || 0);
  barChart.setOption({
    animationDuration: 300,
    color:['#0808EE','#4F63F7'],
    tooltip:{trigger:'axis',axisPointer:{type:'shadow'},backgroundColor:'#fff',borderColor:'#c9c9c9',textStyle:{color:'#171777',fontWeight:700}},
    legend:{show:false},
    grid:{left:36,right:10,top:10,bottom:40,containLabel:true},
    xAxis:{
      type:'category',
      data:categories,
      axisTick:{show:false},
      axisLine:{lineStyle:{color:'#c9c9c9'}},
      axisLabel:{color:'#6f7890',fontSize:10,fontWeight:700,interval:0,rotate:26}
    },
    yAxis:{
      type:'value',
      axisLine:{show:false},
      axisTick:{show:false},
      splitLine:{lineStyle:{color:'#ececf1'}},
      axisLabel:{color:'#6f7890',fontSize:10,fontWeight:700}
    },
    series:[
      {name:'OEB-FAIL', type:'bar', stack:'risk', barWidth:18, data:failData, itemStyle:{borderRadius:[3,3,0,0]}},
      {name:'OEB-SoftFail', type:'bar', stack:'risk', barWidth:18, data:softData, itemStyle:{borderRadius:[3,3,0,0]}}
    ]
  }, true);
  donutChart.resize();
  barChart.resize();
}
dashboardBridge(render, () => { donutChart && donutChart.resize(); barChart && barChart.resize(); });
