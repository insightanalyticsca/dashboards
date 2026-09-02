(function () {
    "use strict";

    function ready(fn) {
        if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", fn, { once: true });
        else fn();
    }

    ready(function () {
        const root = document.getElementById("dashboardAssistant");
        if (!root) return;

        const endpoints = {
            bootstrap: root.dataset.bootstrapUrl,
            suggestions: root.dataset.suggestionsUrl,
            plan: root.dataset.planUrl,
            aggregate: root.dataset.aggregateUrl,
            executive: root.dataset.executiveUrl,
            narrate: root.dataset.narrateUrl
        };

        const launcher = root.querySelector("[data-da-launcher]");
        const backdrop = root.querySelector("[data-da-backdrop]");
        const closeButton = root.querySelector("[data-da-close]");
        const resetButton = root.querySelector("[data-da-reset]");
        const contextTitle = root.querySelector("[data-da-context-title]");
        const contextDetail = root.querySelector("[data-da-context-detail]");
        const contextState = root.querySelector("[data-da-context-state]");
        const semanticScopeHost = root.querySelector("[data-da-semantic-scope]");
        const messagesHost = root.querySelector("[data-da-messages]");
        const examplesHost = root.querySelector("[data-da-examples]");
        const suggestionsHost = root.querySelector("[data-da-suggestions]");
        const resultsHost = root.querySelector("[data-da-results]");
        const input = root.querySelector("[data-da-input]");
        const micButton = root.querySelector("[data-da-mic]");
        const sendButton = root.querySelector("[data-da-send]");
        const status = root.querySelector("[data-da-status]");

        const expectedBuildId = "assistant-v8-direct-v217-contract";

        const state = {
            bootstrap: null,
            context: null,
            layoutVersionId: 0,
            datasetKey: "",
            measure: "",
            dimensions: [],
            chartType: "",
            lastQuestion: "",
            lastPlan: null,
            lastRows: [],
            chart: null,
            recognition: null,
            listening: false,
            suggestionTimer: null
        };

        function appBase() {
            const path = window.location.pathname;
            const marker = "/Dashboard/";
            const index = path.toLowerCase().indexOf(marker.toLowerCase());
            return index >= 0 ? path.slice(0, index) : "";
        }

        function absoluteUrl(value) {
            const raw = String(value || "").trim();
            if (!raw) return raw;
            if (/^https?:\/\//i.test(raw)) return raw;
            const base = appBase();
            if (raw.startsWith(base + "/")) return raw;
            if (raw.startsWith("/")) return base + raw;
            return base + "/" + raw;
        }

        async function getJson(url) {
            const response = await fetch(absoluteUrl(url), {
                credentials: "same-origin",
                cache: "no-store",
                headers: { "Accept": "application/json" }
            });
            if (!response.ok) throw new Error(await response.text() || `Request failed (${response.status})`);
            return await response.json();
        }

        async function postJson(url, body) {
            const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
            const response = await fetch(absoluteUrl(url), {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Content-Type": "application/json",
                    "Accept": "application/json",
                    ...(token ? { "RequestVerificationToken": token } : {})
                },
                body: JSON.stringify(body)
            });
            if (!response.ok) {
                const raw = await response.text();
                let message = `Request failed (${response.status})`;
                try {
                    const problem = JSON.parse(raw);
                    message = problem.detail || problem.title || message;
                } catch {
                    if (raw && raw.length < 500 && !raw.includes(" at ")) message = raw;
                }
                throw new Error(message);
            }
            return await response.json();
        }

        function escapeHtml(value) {
            return String(value ?? "")
                .replaceAll("&", "&amp;")
                .replaceAll("<", "&lt;")
                .replaceAll(">", "&gt;")
                .replaceAll('"', "&quot;")
                .replaceAll("'", "&#039;");
        }

        function setStatus(text, mode) {
            status.textContent = text || "";
            status.className = "dashboard-assistant-status" + (mode ? ` is-${mode}` : "");
        }

        function setBusy(busy) {
            sendButton.disabled = !!busy;
            if (busy) setStatus("Validating the semantic plan…");
        }

        async function openAssistant() {
            backdrop.hidden = false;
            document.body.style.overflow = "hidden";
            await refreshContext(false);
            setTimeout(() => input.focus(), 30);
        }

        function closeAssistant() {
            backdrop.hidden = true;
            document.body.style.removeProperty("overflow");
            stopListening();
        }

        function addMessage(kind, text) {
            const node = document.createElement("div");
            node.className = `dashboard-assistant-message ${kind}`;
            node.textContent = text;
            messagesHost.appendChild(node);
            messagesHost.scrollTop = messagesHost.scrollHeight;
        }

        function renderEmptyResults() {
            disposeChart();
            resultsHost.innerHTML = `
                <div class="dashboard-assistant-empty">
                    <div>
                        <i class="fa-solid fa-chart-column"></i>
                        <strong>Your generated visual appears here</strong>
                        <span>The assistant validates the active version, its approved datasets, fields, period and requested visual before it sends an aggregate request.</span>
                    </div>
                </div>`;
        }

        function renderContext() {
            const context = state.context;
            if (!context) {
                contextTitle.textContent = "Resolving current version…";
                contextDetail.textContent = "Reading the selected SQL layout and its templates";
                contextState.textContent = "Checking";
                contextState.className = "dashboard-assistant-context-state";
                if (semanticScopeHost) {
                    semanticScopeHost.innerHTML = "";
                    semanticScopeHost.hidden = true;
                }
                return;
            }

            contextTitle.textContent = context.contextLabel || `Version ${context.layoutVersionId || ""}`;
            const buildSuffix = state.buildId ? ` · ${state.buildId}` : "";
            contextDetail.textContent = (context.contextDetail || context.message || "Current screen only") + buildSuffix;
            contextState.textContent = context.resolved ? "Current screen only" : "Unavailable";
            contextState.className = "dashboard-assistant-context-state" + (context.resolved ? " is-ready" : " is-error");

            if (semanticScopeHost) {
                const facts = Array.isArray(state.bootstrap?.facts) ? state.bootstrap.facts : [];
                const dimensions = Array.isArray(state.bootstrap?.dimensions) ? state.bootstrap.dimensions : [];
                semanticScopeHost.innerHTML = [
                    ...facts.map(label => `<span class="dashboard-assistant-semantic-chip is-fact"><b>Fact</b>${escapeHtml(label)}</span>`),
                    ...dimensions.map(label => `<span class="dashboard-assistant-semantic-chip is-dimension"><b>Dimension</b>${escapeHtml(label)}</span>`)
                ].join("");
                semanticScopeHost.hidden = !context.resolved || (!facts.length && !dimensions.length);
            }
        }

        function loadExamples() {
            const examples = Array.isArray(state.bootstrap?.examples) ? state.bootstrap.examples : [];
            examplesHost.innerHTML = examples.map(text => `
                <button type="button" class="dashboard-assistant-chip" data-example="${escapeHtml(text)}">${escapeHtml(text)}</button>`).join("");
            examplesHost.querySelectorAll("[data-example]").forEach(button => {
                button.addEventListener("click", () => {
                    input.value = button.dataset.example || "";
                    autoSizeInput();
                    submitQuestion();
                });
            });
        }

        function currentLayoutVersionId() {
            const datasetValue = Number(root.dataset.layoutVersionId || 0);
            if (datasetValue > 0) return datasetValue;

            const params = new URLSearchParams(window.location.search);
            const names = [
                "layoutVersionId", "layoutversionid", "layoutId", "layoutid",
                "currentLayoutId", "currentlayoutid", "versionId", "versionid", "id"
            ];
            for (const name of names) {
                const value = Number(params.get(name) || 0);
                if (value > 0) return value;
            }
            return 0;
        }

        function addContextQuery(url) {
            const versionId = currentLayoutVersionId();
            if (versionId > 0) url.searchParams.set("layoutVersionId", String(versionId));
            collectCurrentTemplates().forEach(key => url.searchParams.append("currentTemplateKeys", key));
            return versionId;
        }

        async function refreshContext(force) {
            const versionId = currentLayoutVersionId();
            if (!force && state.context?.resolved && state.layoutVersionId === versionId) return true;

            renderContext();
            setStatus("Resolving the current dashboard version…");

            try {
                const url = new URL(absoluteUrl(endpoints.bootstrap), window.location.origin);
                addContextQuery(url);
                const previousVersionId = state.layoutVersionId;
                state.bootstrap = await getJson(url.toString());
                if (state.bootstrap?.buildId !== expectedBuildId) {
                    throw new Error(`Dashboard assistant build mismatch. Expected ${expectedBuildId}, received ${state.bootstrap?.buildId || "none"}. Stop the older app instance and run this solution on its assigned port.`);
                }
                if (!state.bootstrap.enabled) {
                    root.hidden = true;
                    return false;
                }

                state.context = state.bootstrap.context || null;
                state.buildId = state.bootstrap.buildId || "";
                document.documentElement.dataset.dashboardAssistantBuild = state.buildId;
                if (state.buildId) console.info("Dashboard assistant build:", state.buildId);
                state.layoutVersionId = Number(state.context?.layoutVersionId || versionId || 0);
                root.dataset.layoutVersionId = state.layoutVersionId > 0 ? String(state.layoutVersionId) : "";
                root.dataset.layoutTitle = state.context?.layoutTitle || "";
                // A selected dataset/measure from a prior or stale assistant build must never
                // override the semantic contract returned for the current screen.
                state.datasetKey = "";
                state.measure = "";
                state.dimensions = [];
                state.chartType = "";
                renderContext();
                loadExamples();

                const resolved = !!state.context?.resolved;
                input.disabled = !resolved;
                sendButton.disabled = !resolved;
                if (micButton) micButton.disabled = !resolved;

                if (!resolved) {
                    setStatus(state.context?.message || "Current version context could not be resolved.", "error");
                    return false;
                }

                if (previousVersionId > 0 && previousVersionId !== state.layoutVersionId) {
                    state.datasetKey = "";
                    state.measure = "";
                    state.dimensions = [];
                    state.chartType = "";
                    state.lastPlan = null;
                    state.lastRows = [];
                    messagesHost.innerHTML = "";
                    suggestionsHost.innerHTML = "";
                    renderEmptyResults();
                    addMessage("assistant", `Context changed to ${state.context.contextLabel}. Questions are now limited to this screen.`);
                }

                setStatus(`${state.context.contextLabel} is active. Current-screen data only.`);
                return true;
            } catch (error) {
                console.error("Dashboard assistant context failed", error);
                state.context = null;
                renderContext();
                input.disabled = true;
                sendButton.disabled = true;
                if (micButton) micButton.disabled = true;
                setStatus(error.message || "Version context could not be resolved.", "error");
                return false;
            }
        }

        function collectCurrentTemplates() {
            return Array.from(document.querySelectorAll(".tile"))
                .map(tile => String(tile?._state?.customHtmlTemplate || "").trim())
                .filter(Boolean)
                .filter((value, index, array) => array.indexOf(value) === index);
        }

        function currentLayoutTitle() {
            return state.context?.layoutTitle || root.dataset.layoutTitle || document.title || "";
        }

        async function submitQuestion() {
            const question = input.value.trim();
            if (!question) return;
            if (!await refreshContext(false)) {
                addMessage("assistant", "The current SQL dashboard version could not be resolved, so no query was executed.");
                return;
            }

            state.lastQuestion = question;
            addMessage("user", question);
            suggestionsHost.innerHTML = "";
            setBusy(true);

            try {
                const plan = await postJson(endpoints.plan, {
                    layoutVersionId: state.layoutVersionId,
                    layoutVersionTitle: state.context?.layoutTitle || null,
                    question,
                    datasetKey: state.datasetKey || null,
                    measure: state.measure || null,
                    dimensions: state.dimensions,
                    chartType: state.chartType || null,
                    currentLayoutTitle: currentLayoutTitle(),
                    currentTemplateKeys: collectCurrentTemplates()
                });

                if (!plan.ready) {
                    handleClarification(plan);
                    return;
                }

                state.lastPlan = plan;
                addMessage("assistant", `Validated ${plan.dataset.title} for ${plan.plan.periodLabel}. Rendering ${visualLabel(plan.visual.type)}.`);
                await executePlan(plan);
            } catch (error) {
                console.error("Dashboard assistant query failed", error);
                addMessage("assistant", error?.message || "The validated dashboard request could not be completed.");
                setStatus("The request failed safely. No raw exception details are displayed here; use the server trace ID for diagnostics.", "error");
            } finally {
                setBusy(false);
            }
        }

        function handleClarification(plan) {
            const clarification = plan.clarification;
            const message = clarification?.prompt || plan.message || "More information is required.";
            addMessage("assistant", message);

            if (plan.outOfScope) {
                setStatus("Current-version boundary enforced. No out-of-scope SQL was executed.");
                return;
            }

            setStatus(clarification?.choices?.length
                ? "Waiting for one exact choice."
                : "The request needs more detail before it can run.");

            if (!clarification?.choices?.length) return;

            const wrap = document.createElement("div");
            wrap.className = "dashboard-assistant-choices";
            clarification.choices.forEach(choice => {
                const button = document.createElement("button");
                button.type = "button";
                button.className = "dashboard-assistant-chip";
                button.innerHTML = `<strong>${escapeHtml(choice.label)}</strong>${choice.detail ? ` · ${escapeHtml(choice.detail)}` : ""}`;
                button.addEventListener("click", async () => {
                    if (clarification.kind === "dataset") state.datasetKey = choice.value;
                    if (clarification.kind === "measure") state.measure = choice.value;
                    if (clarification.kind === "visual") state.chartType = choice.value;
                    wrap.remove();
                    await submitQuestion();
                });
                wrap.appendChild(button);
            });
            messagesHost.appendChild(wrap);
            messagesHost.scrollTop = messagesHost.scrollHeight;
        }

        async function executePlan(plan) {
            disposeChart();
            const executiveMode = String(plan.executionMode || "").toLowerCase() === "executivesuite";
            resultsHost.innerHTML = `<div class="dashboard-assistant-loading"><div><i class="fa-solid fa-circle-notch fa-spin"></i>${executiveMode ? "Reading the validated screen payload" : "Running validated aggregate"}…</div></div>`;
            setStatus(executiveMode
                ? "Using the same normalized payload that renders the current executive screen…"
                : "Running the approved dataset and fields…");

            const aggregate = executiveMode
                ? await postJson(endpoints.executive, plan.executiveRequest)
                : await postJson(endpoints.aggregate, plan.aggregateRequest);
            const rows = Array.isArray(aggregate?.data) ? aggregate.data : [];
            state.lastRows = rows;

            const singlePoint = isSinglePointResult(plan, aggregate, rows);
            renderResult(plan, aggregate, rows, singlePoint);
            const warning = String(aggregate?.warning || "").trim();
            const resultStatus = singlePoint
                ? "Returned one data point; chart omitted and the result is being narrated."
                : `Rendered ${rows.length.toLocaleString()} grouped row${rows.length === 1 ? "" : "s"}.`;
            setStatus(warning || resultStatus, warning ? "warning" : "");
            loadNarrative(plan, rows).catch(error => console.debug("Narrative unavailable", error));
        }

        function renderResult(plan, aggregate, rows, singlePoint) {
            const resultMarkup = singlePoint
                ? `
                    <div class="dashboard-assistant-single-result" data-single-result>
                        <div class="dashboard-assistant-single-kicker">Direct answer</div>
                        <div data-single-value></div>
                    </div>`
                : `
                    <div class="dashboard-assistant-viz-card">
                        <div class="dashboard-assistant-viz-head">
                            <div class="dashboard-assistant-viz-title">${escapeHtml(plan.visual.title)}</div>
                            <div class="dashboard-assistant-viz-subtitle">${escapeHtml(plan.visual.subtitle)}</div>
                        </div>
                        <div data-viz-host></div>
                    </div>`;

            resultsHost.innerHTML = `
                <div class="dashboard-assistant-plan">
                    <div class="dashboard-assistant-plan-title">Validated interpretation</div>
                    <div class="dashboard-assistant-plan-chips" data-plan-chips></div>
                </div>
                <div class="dashboard-assistant-narrative" data-narrative>${singlePoint
                    ? "One data point was returned. Preparing a direct narrative from that computed fact."
                    : "Computed result is ready. Narrative is being prepared from returned facts only."}</div>
                ${resultMarkup}`;

            const chips = [
                `Version ${plan.plan.layoutVersionId}`,
                plan.dataset.title,
                plan.plan.aggregation,
                plan.plan.measure || "Row count",
                ...(plan.plan.dimensions || []),
                plan.plan.periodLabel,
                visualLabel(plan.visual.type),
                `Source: ${plan.dataset.title || 'data'}`
            ];
            const chipHost = resultsHost.querySelector("[data-plan-chips]");
            chipHost.innerHTML = chips.map(value => `<span class="dashboard-assistant-chip">${escapeHtml(value)}</span>`).join("");

            if (singlePoint) {
                renderSinglePoint(
                    resultsHost.querySelector("[data-single-value]"),
                    plan,
                    aggregate,
                    rows);
            } else {
                renderVisual(resultsHost.querySelector("[data-viz-host]"), plan, aggregate, rows);
            }
        }

        function isSinglePointResult(plan, aggregate, rows) {
            if (!Array.isArray(rows) || rows.length !== 1) return false;
            const valueField = plan?.visual?.measureField || aggregate?.valueFields?.[0] || "Value";
            return Object.prototype.hasOwnProperty.call(rows[0] || {}, valueField) ||
                Object.prototype.hasOwnProperty.call(rows[0] || {}, "Value") ||
                Object.keys(rows[0] || {}).some(key => isNumber(rows[0][key]));
        }

        function renderSinglePoint(host, plan, aggregate, rows) {
            if (!host) return;
            const row = rows[0] || {};
            const preferredField = plan?.visual?.measureField || aggregate?.valueFields?.[0] || "Value";
            const valueField = Object.prototype.hasOwnProperty.call(row, preferredField)
                ? preferredField
                : Object.prototype.hasOwnProperty.call(row, "Value")
                    ? "Value"
                    : Object.keys(row).find(key => isNumber(row[key]));
            const value = valueField ? numeric(row[valueField]) : 0;
            const dimensions = (plan?.visual?.dimensionFields || plan?.plan?.dimensions || [])
                .map(field => row[field])
                .filter(value => value !== null && value !== undefined && String(value).trim() !== "")
                .map(value => String(value));
            const detail = dimensions.length
                ? `${dimensions.join(" · ")} · ${plan?.plan?.periodLabel || ""}`
                : (plan?.plan?.periodLabel || plan?.visual?.subtitle || "");

            host.innerHTML = `
                <div class="dashboard-assistant-single-value">${escapeHtml(formatValue(value, plan?.visual?.valueFormat || "number"))}</div>
                <div class="dashboard-assistant-single-label">${escapeHtml(plan?.visual?.title || plan?.plan?.measure || "Result")}</div>
                ${detail ? `<div class="dashboard-assistant-single-detail">${escapeHtml(detail)}</div>` : ""}`;
        }

        async function loadNarrative(plan, rows) {
            const target = resultsHost.querySelector("[data-narrative]");
            if (!target) return;
            const response = await postJson(endpoints.narrate, {
                plan: plan.plan,
                visual: plan.visual,
                rows: rows.slice(0, 500)
            });
            if (target.isConnected) target.textContent = response.narrative || "No narrative was generated.";
        }

        function visualLabel(type) {
            const labels = {
                metric: "Metric card",
                table: "Table",
                matrix: "Matrix",
                bar: "Bar chart",
                hbar: "Horizontal bar",
                line: "Line chart",
                area: "Area chart",
                combo: "Bar chart with total line",
                stackedBar: "Stacked bar",
                stacked100: "100% stacked bar",
                pie: "Pie chart",
                donut: "Donut chart",
                heatmap: "Heat map",
                scatter: "Scatter plot"
            };
            return labels[type] || type;
        }

        function renderVisual(host, plan, aggregate, rows) {
            const type = plan.visual.type;
            const valueField = plan.visual.measureField || aggregate?.valueFields?.[0] || "Count";
            const rowFields = plan.aggregateRequest?.rows || aggregate?.rowFields || [];
            const colFields = plan.aggregateRequest?.cols || aggregate?.colFields || [];
            const dimensions = [...rowFields, ...colFields].filter((value, index, array) => array.indexOf(value) === index);

            if (!rows.length) {
                host.innerHTML = `<div class="dashboard-assistant-empty"><div><i class="fa-solid fa-circle-info"></i><strong>No matching rows</strong><span>The semantic plan was valid, but the selected period and source returned no data.</span></div></div>`;
                return;
            }

            if (type === "metric") return renderMetric(host, rows, valueField, plan.visual.valueFormat, plan.visual.title);
            if (type === "table") return renderTable(host, rows);
            if (type === "matrix") return renderMatrix(host, rows, rowFields, colFields, valueField, plan.visual.valueFormat);

            host.innerHTML = `<div class="dashboard-assistant-chart" data-chart></div>`;
            const chartHost = host.querySelector("[data-chart]");
            if (!window.echarts) {
                chartHost.innerHTML = "ECharts is not available.";
                return;
            }

            const chart = window.echarts.init(chartHost);
            state.chart = chart;
            const option = buildChartOption(type, rows, dimensions, valueField, plan.visual);
            chart.setOption(option, { notMerge: true });
            setTimeout(() => chart.resize(), 0);
        }

        function renderMetric(host, rows, valueField, format, label) {
            const total = rows.reduce((sum, row) => sum + numeric(row[valueField]), 0);
            host.innerHTML = `
                <div class="dashboard-assistant-metric">
                    <div>
                        <div class="dashboard-assistant-metric-value">${escapeHtml(formatValue(total, format))}</div>
                        <div class="dashboard-assistant-metric-label">${escapeHtml(label)}</div>
                    </div>
                </div>`;
        }

        function renderTable(host, rows) {
            const columns = Object.keys(rows[0] || {});
            host.innerHTML = `
                <div class="dashboard-assistant-table-wrap">
                    <table class="dashboard-assistant-table">
                        <thead><tr>${columns.map(column => `<th>${escapeHtml(column)}</th>`).join("")}</tr></thead>
                        <tbody>${rows.slice(0, 500).map(row => `<tr>${columns.map(column => {
                            const value = row[column];
                            const number = isNumber(value);
                            return `<td class="${number ? "is-number" : ""}">${escapeHtml(number ? formatValue(numeric(value), "number") : value)}</td>`;
                        }).join("")}</tr>`).join("")}</tbody>
                    </table>
                </div>`;
        }

        function renderMatrix(host, rows, rowFields, colFields, valueField, format) {
            const rowKeys = unique(rows.map(row => keyFrom(row, rowFields.length ? rowFields : [Object.keys(row)[0]])));
            const effectiveCols = colFields.length ? colFields : [Object.keys(rows[0]).find(key => !rowFields.includes(key) && key !== valueField)].filter(Boolean);
            const colKeys = unique(rows.map(row => keyFrom(row, effectiveCols)));
            const map = new Map();
            rows.forEach(row => {
                const rk = keyFrom(row, rowFields.length ? rowFields : [Object.keys(row)[0]]);
                const ck = keyFrom(row, effectiveCols);
                map.set(`${rk}||${ck}`, (map.get(`${rk}||${ck}`) || 0) + numeric(row[valueField]));
            });

            host.innerHTML = `
                <div class="dashboard-assistant-table-wrap">
                    <table class="dashboard-assistant-table">
                        <thead><tr><th>${escapeHtml(rowFields.join(" / ") || "Rows")}</th>${colKeys.map(key => `<th>${escapeHtml(key)}</th>`).join("")}<th>Total</th></tr></thead>
                        <tbody>${rowKeys.map(rk => {
                            const values = colKeys.map(ck => map.get(`${rk}||${ck}`) || 0);
                            return `<tr><th>${escapeHtml(rk)}</th>${values.map(value => `<td class="is-number">${escapeHtml(formatValue(value, format))}</td>`).join("")}<td class="is-number"><strong>${escapeHtml(formatValue(values.reduce((a, b) => a + b, 0), format))}</strong></td></tr>`;
                        }).join("")}</tbody>
                    </table>
                </div>`;
        }

        function buildChartOption(type, rows, dimensions, valueField, visual) {
            const dateField = visual.dateField && dimensions.includes(visual.dateField) ? visual.dateField : null;
            const xField = dateField || dimensions[0] || null;
            const seriesFields = dimensions.filter(field => field !== xField);
            const xValues = unique(rows.map(row => xField ? String(row[xField] ?? "(blank)") : "Total"));
            const seriesKeys = unique(rows.map(row => seriesFields.length ? keyFrom(row, seriesFields) : valueField));
            const map = new Map();

            rows.forEach(row => {
                const x = xField ? String(row[xField] ?? "(blank)") : "Total";
                const series = seriesFields.length ? keyFrom(row, seriesFields) : valueField;
                const key = `${x}||${series}`;
                map.set(key, (map.get(key) || 0) + numeric(row[valueField]));
            });

            const tooltip = { trigger: type === "pie" || type === "donut" ? "item" : "axis", axisPointer: { type: "shadow" } };
            const base = {
                animationDuration: 500,
                color: ["#0808ee", "#09c698", "#171777", "#4f63f7", "#12ddb8", "#635bcb", "#bbff05", "#d4d4d9"],
                tooltip,
                legend: { type: "scroll", top: 8, left: 12, right: 12, textStyle: { fontSize: 10 } },
                grid: { left: 52, right: 24, top: 54, bottom: 58, containLabel: true },
                xAxis: { type: "category", data: xValues, axisLabel: { fontSize: 10, rotate: xValues.length > 14 ? 35 : 0 } },
                yAxis: { type: "value", axisLabel: { fontSize: 10, formatter: compactNumber } },
                series: []
            };

            if (type === "pie" || type === "donut") {
                const data = seriesKeys.map(series => ({
                    name: series,
                    value: xValues.reduce((sum, x) => sum + (map.get(`${x}||${series}`) || 0), 0)
                }));
                return {
                    color: base.color,
                    tooltip: { trigger: "item" },
                    legend: base.legend,
                    series: [{
                        type: "pie",
                        radius: type === "donut" ? ["42%", "70%"] : "70%",
                        center: ["50%", "57%"],
                        data,
                        label: { formatter: "{b}\n{d}%", fontSize: 10 }
                    }]
                };
            }

            if (type === "heatmap") {
                const yField = dimensions.find(field => field !== xField);
                const yValues = unique(rows.map(row => String(row[yField] ?? "(blank)")));
                const heat = [];
                rows.forEach(row => {
                    const x = xValues.indexOf(String(row[xField] ?? "(blank)"));
                    const y = yValues.indexOf(String(row[yField] ?? "(blank)"));
                    heat.push([x, y, numeric(row[valueField])]);
                });
                return {
                    tooltip: { position: "top" },
                    grid: base.grid,
                    xAxis: base.xAxis,
                    yAxis: { type: "category", data: yValues, axisLabel: { fontSize: 10 } },
                    visualMap: { min: 0, max: Math.max(1, ...heat.map(item => item[2])), calculable: true, orient: "horizontal", left: "center", bottom: 4 },
                    series: [{ type: "heatmap", data: heat, label: { show: heat.length < 120, fontSize: 9 } }]
                };
            }

            if (type === "scatter") {
                return {
                    ...base,
                    xAxis: { type: "value", axisLabel: { formatter: compactNumber } },
                    series: seriesKeys.map((series, index) => ({
                        name: series,
                        type: "scatter",
                        data: xValues.map((x, xIndex) => [xIndex, map.get(`${x}||${series}`) || 0]),
                        symbolSize: 9
                    }))
                };
            }

            const series = seriesKeys.map(series => ({
                name: series,
                type: type === "line" || type === "area" ? "line" : "bar",
                data: xValues.map(x => map.get(`${x}||${series}`) || 0),
                smooth: type === "line" || type === "area" ? .3 : undefined,
                areaStyle: type === "area" ? {} : undefined,
                stack: type === "stackedBar" || type === "stacked100" ? "total" : undefined,
                emphasis: { focus: "series" }
            }));

            if (type === "stacked100") {
                xValues.forEach((x, index) => {
                    const total = series.reduce((sum, item) => sum + numeric(item.data[index]), 0);
                    series.forEach(item => item.data[index] = total ? numeric(item.data[index]) / total * 100 : 0);
                });
                base.yAxis = { type: "value", min: 0, max: 100, axisLabel: { formatter: "{value}%" } };
            }

            if (type === "combo") {
                const total = xValues.map((x, index) => series.reduce((sum, item) => sum + numeric(item.data[index]), 0));
                series.push({
                    name: "Total",
                    type: "line",
                    data: total,
                    smooth: .3,
                    symbolSize: 7,
                    lineStyle: { width: 3 },
                    z: 8
                });
            }

            if (type === "hbar") {
                base.xAxis = { type: "value", axisLabel: { formatter: compactNumber } };
                base.yAxis = { type: "category", data: xValues, axisLabel: { fontSize: 10 } };
            }

            base.series = series;
            return base;
        }

        function keyFrom(row, fields) {
            return fields.map(field => String(row[field] ?? "(blank)")).join(" / ");
        }

        function unique(values) {
            return values.filter((value, index, array) => array.indexOf(value) === index);
        }

        function isNumber(value) {
            return value !== null && value !== "" && Number.isFinite(Number(value));
        }

        function numeric(value) {
            const number = Number(value);
            return Number.isFinite(number) ? number : 0;
        }

        function compactNumber(value) {
            const number = Number(value);
            if (!Number.isFinite(number)) return String(value ?? "");
            const abs = Math.abs(number);
            if (abs >= 1e9) return (number / 1e9).toFixed(1).replace(/\.0$/, "") + "B";
            if (abs >= 1e6) return (number / 1e6).toFixed(1).replace(/\.0$/, "") + "M";
            if (abs >= 1e3) return (number / 1e3).toFixed(1).replace(/\.0$/, "") + "K";
            return number.toLocaleString(undefined, { maximumFractionDigits: 1 });
        }

        function formatValue(value, format) {
            const number = Number(value);
            if (!Number.isFinite(number)) return String(value ?? "");
            if (format === "currency") return number.toLocaleString(undefined, { style: "currency", currency: "CAD", maximumFractionDigits: 0 });
            if (format === "percent") return number.toLocaleString(undefined, { maximumFractionDigits: 2 }) + "%";
            return number.toLocaleString(undefined, { maximumFractionDigits: 2 });
        }

        function disposeChart() {
            if (state.chart) {
                try { state.chart.dispose(); } catch { }
                state.chart = null;
            }
        }

        function autoSizeInput() {
            input.style.height = "auto";
            input.style.height = Math.min(input.scrollHeight, 108) + "px";
        }

        function queueSuggestions() {
            clearTimeout(state.suggestionTimer);
            state.suggestionTimer = setTimeout(loadSuggestions, 180);
        }

        async function loadSuggestions() {
            if (!state.context?.resolved) {
                suggestionsHost.innerHTML = "";
                return;
            }
            const text = input.value.trim();
            const prefix = text.split(/\s+/).slice(-3).join(" ");
            try {
                const url = new URL(absoluteUrl(endpoints.suggestions), window.location.origin);
                addContextQuery(url);
                if (state.datasetKey) url.searchParams.set("datasetKey", state.datasetKey);
                url.searchParams.set("prefix", prefix);
                const suggestions = await getJson(url.toString());
                suggestionsHost.innerHTML = suggestions.slice(0, 8).map(item => `
                    <button type="button" class="dashboard-assistant-chip" data-insert="${escapeHtml(item.insertText)}">
                        ${escapeHtml(item.label)}
                    </button>`).join("");
                suggestionsHost.querySelectorAll("[data-insert]").forEach(button => {
                    button.addEventListener("click", () => {
                        const insert = button.dataset.insert || "";
                        input.value = input.value.trim() ? `${input.value.trim()} ${insert}` : insert;
                        autoSizeInput();
                        input.focus();
                    });
                });
            } catch {
                suggestionsHost.innerHTML = "";
            }
        }

        function initSpeechRecognition() {
            const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
            if (!SpeechRecognition) {
                micButton.hidden = true;
                return;
            }

            const recognition = new SpeechRecognition();
            recognition.lang = "en-CA";
            recognition.continuous = true;
            recognition.interimResults = true;

            let finalText = "";
            recognition.onstart = function () {
                state.listening = true;
                finalText = input.value.trim();
                micButton.classList.add("is-listening");
                setStatus("Listening — say the metric, period, grouping and visual.", "listening");
            };
            recognition.onresult = function (event) {
                let interim = "";
                for (let i = event.resultIndex; i < event.results.length; i++) {
                    const transcript = event.results[i][0].transcript;
                    if (event.results[i].isFinal) finalText = `${finalText} ${transcript}`.trim();
                    else interim += transcript;
                }
                input.value = `${finalText} ${interim}`.trim();
                autoSizeInput();
            };
            recognition.onerror = function (event) {
                setStatus(`Voice input: ${event.error || "recognition error"}.`, "error");
            };
            recognition.onend = function () {
                state.listening = false;
                micButton.classList.remove("is-listening");
                if (!status.classList.contains("is-error")) setStatus("Voice transcription ready. Review or run it.");
            };
            state.recognition = recognition;
        }

        function toggleListening() {
            if (!state.recognition) initSpeechRecognition();
            if (!state.recognition) return;
            if (state.listening) stopListening();
            else {
                try { state.recognition.start(); }
                catch (error) { setStatus(error.message || "Voice input could not start.", "error"); }
            }
        }

        function stopListening() {
            if (!state.recognition || !state.listening) return;
            try { state.recognition.stop(); } catch { }
        }

        function resetAssistant() {
            state.datasetKey = "";
            state.measure = "";
            state.dimensions = [];
            state.chartType = "";
            state.lastPlan = null;
            state.lastRows = [];
            input.value = "";
            messagesHost.innerHTML = "";
            suggestionsHost.innerHTML = "";
            renderEmptyResults();
            addMessage("assistant", `Ask about ${state.context?.contextLabel || "the current dashboard"}. Include the measure, period, grouping and exact visual when relevant.`);
            setStatus(state.context?.resolved
                ? `${state.context.contextLabel} is active. Current-screen data only.`
                : "Open a saved dashboard version first.");
        }

        async function initialize() {
            renderContext();
            renderEmptyResults();
            addMessage("assistant", "Open me on any saved dashboard version. I will use only the datasets and business vocabulary on that screen.");
            initSpeechRecognition();
        }

        launcher.addEventListener("click", () => { openAssistant().catch(error => console.error(error)); });
        closeButton.addEventListener("click", closeAssistant);
        resetButton.addEventListener("click", resetAssistant);
        backdrop.addEventListener("click", event => { if (event.target === backdrop) closeAssistant(); });
        micButton.addEventListener("click", toggleListening);
        sendButton.addEventListener("click", submitQuestion);
        input.addEventListener("input", function () { autoSizeInput(); queueSuggestions(); });
        input.addEventListener("keydown", function (event) {
            if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                submitQuestion();
            }
        });
        window.addEventListener("dashboard-version-context:changed", event => {
            const detail = event.detail || {};
            if (Number(detail.layoutVersionId) > 0) {
                root.dataset.layoutVersionId = String(detail.layoutVersionId);
                root.dataset.layoutTitle = String(detail.layoutTitle || "");
            }
            state.context = null;
            renderContext();
            if (!backdrop.hidden) {
                refreshContext(true).catch(error => console.error("Assistant context refresh failed", error));
            }
        });
        window.addEventListener("popstate", () => {
            state.context = null;
            renderContext();
            if (!backdrop.hidden) refreshContext(true);
        });
        window.addEventListener("resize", () => { try { state.chart?.resize(); } catch { } });
        document.addEventListener("keydown", event => {
            if (event.key === "Escape" && !backdrop.hidden) closeAssistant();
        });

        initialize();
    });
})();
