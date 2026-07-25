# ITS independent MVC clone rebuild

This package removes the Power BI runtime path. It uses the dashboard constructor layout/appsettings machinery and the original PBIP semantic model as the blueprint only.

Live upstreams wired:

- Ticket/request visuals: direct ServiceDesk Plus Cloud request API equivalent of PBIP `SDPCloud.Contents("sdpondemand.manageengine.com") -> itdesk -> request -> request`. Configure `ServiceDeskPlus:AuthHeaderValue` or environment variable `SDP_AUTH_HEADER_VALUE`.
- KB4 Phish MoM: `its_dashboard.rpt.vw_kb4_phish_failure_mom`.
- KB4 Failure PPP: `its_dashboard.rpt.vw_kb4_phish_failure_monthly`.
- KB4 Training: `its_dashboard.rpt.vw_kb4_training_completion_department_monthly`.
- UptimeRobot SLA: `its_dashboard.rpt.vw_uptime_sla_monthly`.
- OCSF: `corporate_dashboards.dbo.vw_OCSF_Risks_Current`.

Removed/absent:

- No PowerBI settings.
- No ExecuteQueries.
- No guessed `dbo.vw_ITS_*` views.
- No static preview as the live route.
- No layout seeder required. Use your existing `DashboardLayoutVersion` / `DashboardLayoutState` rows.

Open the existing constructor route:

`/Dashboard/Multi?layoutTitle=ITS%20Dash`
