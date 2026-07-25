USE [its_dashboard];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/*
    Inserts the 21 CSR/PBIP tab versions into the existing
    dbo.DashboardLayoutVersion table using IDs 192-212.

    The rows are shared through UserName = __csr_pbip__, which matches
    Dashboard:CsrPbipImport:SharedUserName in appsettings.json.

    This script does not create or alter dbo.DashboardLayoutVersion.
    It is idempotent for the CSR-owned rows and refuses to overwrite
    any of IDs 192-212 if another owner/page already uses them.
*/

IF OBJECT_ID(N'dbo.DashboardLayoutVersion', N'U') IS NULL
    THROW 51000, 'dbo.DashboardLayoutVersion does not exist in its_dashboard.', 1;

IF COL_LENGTH(N'dbo.DashboardLayoutVersion', N'LayoutVersionId') IS NULL
    THROW 51001, 'dbo.DashboardLayoutVersion is missing required column LayoutVersionId.', 1;

IF COL_LENGTH(N'dbo.DashboardLayoutVersion', N'UserName') IS NULL
    THROW 51001, 'dbo.DashboardLayoutVersion is missing required column UserName.', 1;

IF COL_LENGTH(N'dbo.DashboardLayoutVersion', N'Page') IS NULL
    THROW 51001, 'dbo.DashboardLayoutVersion is missing required column Page.', 1;

IF COL_LENGTH(N'dbo.DashboardLayoutVersion', N'Title') IS NULL
    THROW 51001, 'dbo.DashboardLayoutVersion is missing required column Title.', 1;

IF COL_LENGTH(N'dbo.DashboardLayoutVersion', N'CreatedUtc') IS NULL
    THROW 51001, 'dbo.DashboardLayoutVersion is missing required column CreatedUtc.', 1;

IF COL_LENGTH(N'dbo.DashboardLayoutVersion', N'LayoutJson') IS NULL
    THROW 51001, 'dbo.DashboardLayoutVersion is missing required column LayoutJson.', 1;

IF COL_LENGTH(N'dbo.DashboardLayoutVersion', N'Favorite') IS NULL
    THROW 51001, 'dbo.DashboardLayoutVersion is missing required column Favorite.', 1;

DECLARE @SharedUserName nvarchar(256) = N'__csr_pbip__';
DECLARE @Page nvarchar(128) = N'Multi';
DECLARE @CreatedBase datetime2(3) = SYSUTCDATETIME();

DECLARE @Versions TABLE
(
    LayoutVersionId bigint NOT NULL PRIMARY KEY,
    Title nvarchar(256) NOT NULL,
    LayoutJson nvarchar(max) NOT NULL,
    CreatedOffsetSeconds int NOT NULL
);

INSERT @Versions(LayoutVersionId, Title, LayoutJson, CreatedOffsetSeconds)
VALUES
(192, N'Aging Report-Hourly Updates', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"aging_trans_details"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_aging-report-hourly-updates","slicerSelection":"","manualTitle":"Aging Report-Hourly Updates","presentMode":true}}}}', 0),
(193, N'Residential No EPP Aging - With Non-Current Debt', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"agingcube_net_30daysplus"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_residential-no-epp-aging-with-non-current-debt","slicerSelection":"","manualTitle":"Residential No EPP Aging - With Non-Current Debt","presentMode":true}}}}', 1),
(194, N'Commercial No EPP Aging - With Non-Current Debt', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"agingcube_net_30daysplus"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_commercial-no-epp-aging-with-non-current-debt","slicerSelection":"","manualTitle":"Commercial No EPP Aging - With Non-Current Debt","presentMode":true}}}}', 2),
(195, N'Aging Dynamics-Hourly Updates', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"aging_param_history_sum"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_aging-dynamics-hourly-updates","slicerSelection":"","manualTitle":"Aging Dynamics-Hourly Updates","presentMode":true}}}}', 3),
(196, N'Top Arrears Accounts-Hourly Updates', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"agingcube_net"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_top-arrears-accounts-hourly-updates","slicerSelection":"","manualTitle":"Top Arrears Accounts-Hourly Updates","presentMode":true}}}}', 4),
(197, N'Monthly Moves', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"monthly_move_stats"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_monthly-moves","slicerSelection":"","manualTitle":"Monthly Moves","presentMode":true}}}}', 5),
(198, N'Mitel Report', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"mitel"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_mitel-report","slicerSelection":"","manualTitle":"Mitel Report","presentMode":true}}}}', 6),
(199, N'Queue Spectrum Report', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"queue_group_answer_spectrum"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_queue-spectrum-report","slicerSelection":"","manualTitle":"Queue Spectrum Report","presentMode":true}}}}', 7),
(200, N'Customer Payments Daily', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"ns_daily_cash_by_cycle_view"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_customer-payments-daily","slicerSelection":"","manualTitle":"Customer Payments Daily","presentMode":true}}}}', 8),
(201, N'Customer Payments Monthly', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"ns_daily_cash_by_cycle_view"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_customer-payments-monthly","slicerSelection":"","manualTitle":"Customer Payments Monthly","presentMode":true}}}}', 9),
(202, N'Senior CC Emails', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"queue_group_answer_spectrum"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_senior-cc-emails","slicerSelection":"","manualTitle":"Senior CC Emails","presentMode":true}}}}', 10),
(203, N'Collection Emails', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"collections_emails_view"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_collection-emails","slicerSelection":"","manualTitle":"Collection Emails","presentMode":true}}}}', 11),
(204, N'Collection Report', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"ns_collection_submission_accounts_pbi"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_collection-report","slicerSelection":"","manualTitle":"Collection Report","presentMode":true}}}}', 12),
(205, N'Bankruptcies Report', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"ns_collection_submission_accounts_bankrupt"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_bankruptcies-report","slicerSelection":"","manualTitle":"Bankruptcies Report","presentMode":true}}}}', 13),
(206, N'Multi-Unit Conditions Map', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"multi_user_conditions_of_service"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_multi-unit-conditions-map","slicerSelection":"","manualTitle":"Multi-Unit Conditions Map","presentMode":true}}}}', 14),
(207, N'Request Service Layout Map', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"request_service_layout_form_geo"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_request-service-layout-map","slicerSelection":"","manualTitle":"Request Service Layout Map","presentMode":true}}}}', 15),
(208, N'Aging ML Predictor - Hourly Updates', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"aging_ml_predictions"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_aging-ml-predictor-hourly-updates","slicerSelection":"","manualTitle":"Aging ML Predictor - Hourly Updates","presentMode":true}}}}', 16),
(209, N'Risky Commercial Predictor', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"model_metrics"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_risky-commercial-predictor","slicerSelection":"","manualTitle":"Risky Commercial Predictor","presentMode":true}}}}', 17),
(210, N'Risky Residential Predictor', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"model_metrics_resid"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_risky-residential-predictor","slicerSelection":"","manualTitle":"Risky Residential Predictor","presentMode":true}}}}', 18),
(211, N'Monthly EBNotes', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"ns_daily_ebnotes"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_monthly-ebnotes","slicerSelection":"","manualTitle":"Monthly EBNotes","presentMode":true}}}}', 19),
(212, N'Aging Compared By Periods', N'{"v":1,"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"AgingCompareSummary_view"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"Sum","chartType":"customHtml","maxCells":"200000","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"csr_aging-compared-by-periods","slicerSelection":"","manualTitle":"Aging Compared By Periods","presentMode":true}}}}', 20);

IF EXISTS
(
    SELECT 1
    FROM dbo.DashboardLayoutVersion existing
    INNER JOIN @Versions requested
        ON requested.LayoutVersionId = existing.LayoutVersionId
    WHERE existing.UserName <> @SharedUserName
       OR existing.Page <> @Page
)
BEGIN
    SELECT
        existing.LayoutVersionId,
        existing.UserName,
        existing.Page,
        existing.Title
    FROM dbo.DashboardLayoutVersion existing
    INNER JOIN @Versions requested
        ON requested.LayoutVersionId = existing.LayoutVersionId
    WHERE existing.UserName <> @SharedUserName
       OR existing.Page <> @Page;

    THROW 51002, 'One or more requested version IDs are already owned by another dashboard record.', 1;
END;

BEGIN TRY
        BEGIN TRANSACTION;

    UPDATE target
    SET
        target.Title = source.Title,
        target.LayoutJson = source.LayoutJson,
        target.Favorite = COALESCE(target.Favorite, CONVERT(bit, 0))
    FROM dbo.DashboardLayoutVersion target
    INNER JOIN @Versions source
        ON source.LayoutVersionId = target.LayoutVersionId
    WHERE target.UserName = @SharedUserName
      AND target.Page = @Page;

    SET IDENTITY_INSERT dbo.DashboardLayoutVersion ON;

    INSERT dbo.DashboardLayoutVersion
    (
        LayoutVersionId,
        UserName,
        Page,
        Title,
        CreatedUtc,
        LayoutJson,
        Favorite
    )
    SELECT
        source.LayoutVersionId,
        @SharedUserName,
        @Page,
        source.Title,
        DATEADD(SECOND, source.CreatedOffsetSeconds, @CreatedBase),
        source.LayoutJson,
        CONVERT(bit, 0)
    FROM @Versions source
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.DashboardLayoutVersion existing
        WHERE existing.LayoutVersionId = source.LayoutVersionId
    );

    SET IDENTITY_INSERT dbo.DashboardLayoutVersion OFF;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    BEGIN TRY
        SET IDENTITY_INSERT dbo.DashboardLayoutVersion OFF;
    END TRY
    BEGIN CATCH
    END CATCH;

    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
GO

SELECT
    LayoutVersionId,
    UserName,
    Page,
    Title,
    CreatedUtc,
    Favorite
FROM dbo.DashboardLayoutVersion
WHERE LayoutVersionId BETWEEN 192 AND 212
ORDER BY LayoutVersionId;
GO