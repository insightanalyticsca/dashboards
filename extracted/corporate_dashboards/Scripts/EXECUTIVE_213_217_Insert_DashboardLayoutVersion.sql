USE [its_dashboard];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/*
    Adds five new executive dashboard versions only.

    Existing versions, templates, SQL objects, current-version state,
    favorites, titles, and saved LayoutJson rows are not updated.

    213  E-Bill Performance
    214  AR Portfolio
    215  Disconnects, Reconnects and Bankruptcies
    216  Final Bill Collections Recovery - Electric
    217  Customer Payments
*/

IF OBJECT_ID(N'dbo.DashboardLayoutVersion', N'U') IS NULL
BEGIN
    RAISERROR(N'dbo.DashboardLayoutVersion does not exist in its_dashboard.', 16, 1);
    RETURN;
END;

IF COL_LENGTH(N'dbo.DashboardLayoutVersion', N'LayoutVersionId') IS NULL
   OR COL_LENGTH(N'dbo.DashboardLayoutVersion', N'UserName') IS NULL
   OR COL_LENGTH(N'dbo.DashboardLayoutVersion', N'Page') IS NULL
   OR COL_LENGTH(N'dbo.DashboardLayoutVersion', N'Title') IS NULL
   OR COL_LENGTH(N'dbo.DashboardLayoutVersion', N'CreatedUtc') IS NULL
   OR COL_LENGTH(N'dbo.DashboardLayoutVersion', N'LayoutJson') IS NULL
   OR COL_LENGTH(N'dbo.DashboardLayoutVersion', N'Favorite') IS NULL
BEGIN
    RAISERROR(N'dbo.DashboardLayoutVersion is missing one or more required columns.', 16, 1);
    RETURN;
END;

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

INSERT INTO @Versions(LayoutVersionId, Title, LayoutJson, CreatedOffsetSeconds)
VALUES
(
    213,
    N'E-Bill Performance',
    N'{"v":1,"meta":{"preserveGridGeometry":true,"executiveVersion":true},"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"ns_daily_ebnotes"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"RawRows","chartType":"customHtml","maxCells":"","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"executive-ebill-performance","slicerSelection":"","manualTitle":"E-Bill Performance","presentMode":true}}}}',
    0
),
(
    214,
    N'AR Portfolio',
    N'{"v":1,"meta":{"preserveGridGeometry":true,"executiveVersion":true},"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"build","schema":"its","obj":"Aging History TrueDebt"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"RawRows","chartType":"customHtml","maxCells":"","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"executive-ar-portfolio","slicerSelection":"","manualTitle":"AR Portfolio","presentMode":true}}}}',
    1
),
(
    215,
    N'Disconnects, Reconnects and Bankruptcies',
    N'{"v":1,"meta":{"preserveGridGeometry":true,"executiveVersion":true},"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"build","schema":"its","obj":"Disconnects"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"RawRows","chartType":"customHtml","maxCells":"","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"executive-disconnects-bankruptcies","slicerSelection":"","manualTitle":"Disconnects, Reconnects and Bankruptcies","presentMode":true}}}}',
    2
),
(
    216,
    N'Final Bill Collections Recovery - Electric',
    N'{"v":1,"meta":{"preserveGridGeometry":true,"executiveVersion":true},"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"ns_collection_submission_accounts_pbi"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"RawRows","chartType":"customHtml","maxCells":"","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"executive-final-bill-recovery","slicerSelection":"","manualTitle":"Final Bill Collections Recovery - Electric","presentMode":true}}}}',
    3
),
(
    217,
    N'Customer Payments',
    N'{"v":1,"meta":{"preserveGridGeometry":true,"executiveVersion":true},"grid":[{"id":"1","x":0,"y":0,"w":12,"h":9,"minW":1,"minH":1}],"tiles":{"1":{"dataset":{"connection":"csr_pbip_source","schema":"dbo","obj":"ns_daily_cash_by_cycle_view"},"pivot":{"rows":[],"cols":[],"vals":[],"filters":{},"dateGroups":{}},"ui":{"agg":"RawRows","chartType":"customHtml","maxCells":"","auto":true,"autoRefreshSeconds":300,"sideHidden":true,"sideCollapsed":false,"focus":false,"customHtml":"","customHtmlTemplate":"executive-customer-payments","slicerSelection":"","manualTitle":"Customer Payments","presentMode":true}}}}',
    4
);

/* Never overwrite an existing ID, regardless of owner. */
IF EXISTS
(
    SELECT 1
    FROM dbo.DashboardLayoutVersion AS existing
    INNER JOIN @Versions AS requested
        ON requested.LayoutVersionId = existing.LayoutVersionId
    WHERE existing.UserName <> @SharedUserName
       OR existing.Page <> @Page
       OR existing.Title <> requested.Title
       OR existing.LayoutJson <> requested.LayoutJson
)
BEGIN
    SELECT
        existing.LayoutVersionId,
        existing.UserName,
        existing.Page,
        existing.Title
    FROM dbo.DashboardLayoutVersion AS existing
    INNER JOIN @Versions AS requested
        ON requested.LayoutVersionId = existing.LayoutVersionId;

    RAISERROR(N'One or more IDs from 213 through 217 are already used by a different dashboard record. Nothing was changed.', 16, 1);
    RETURN;
END;

BEGIN TRY
    BEGIN TRANSACTION;

    IF EXISTS
    (
        SELECT 1
        FROM @Versions AS requested
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM dbo.DashboardLayoutVersion AS existing
            WHERE existing.LayoutVersionId = requested.LayoutVersionId
        )
    )
    BEGIN
        SET IDENTITY_INSERT dbo.DashboardLayoutVersion ON;

        INSERT INTO dbo.DashboardLayoutVersion
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
            requested.LayoutVersionId,
            @SharedUserName,
            @Page,
            requested.Title,
            DATEADD(SECOND, requested.CreatedOffsetSeconds, @CreatedBase),
            requested.LayoutJson,
            CONVERT(bit, 0)
        FROM @Versions AS requested
        WHERE NOT EXISTS
        (
            SELECT 1
            FROM dbo.DashboardLayoutVersion AS existing
            WHERE existing.LayoutVersionId = requested.LayoutVersionId
        );

        SET IDENTITY_INSERT dbo.DashboardLayoutVersion OFF;
    END;

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

    DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE();
    RAISERROR(N'Executive version insert failed and was rolled back: %s', 16, 1, @ErrorMessage);
    RETURN;
END CATCH;
GO

SELECT
    LayoutVersionId,
    UserName,
    Page,
    Title,
    CreatedUtc,
    Favorite,
    CASE WHEN ISJSON(LayoutJson) = 1 THEN N'valid' ELSE N'invalid' END AS LayoutJsonStatus
FROM dbo.DashboardLayoutVersion
WHERE LayoutVersionId BETWEEN 213 AND 217
ORDER BY LayoutVersionId;
GO
