USE [corporate_dashboards];
GO
SET NOCOUNT ON;
SET XACT_ABORT OFF;
GO

/*
  Validates the exact physical sources used by CSR PBIP versions 192-212.
  Run under the same Windows/SQL identity used by the IIS application pool when possible.
*/

DECLARE @Sources TABLE
(
    SchemaName sysname NOT NULL,
    ObjectName sysname NOT NULL,
    ExpectedKind nvarchar(20) NOT NULL
);

INSERT @Sources(SchemaName,ObjectName,ExpectedKind)
VALUES
(N'dbo',N'aging_trans_details',N'auto'),
(N'dbo',N'agingcube_net',N'auto'),
(N'dbo',N'agingcube_net_30daysplus',N'auto'),
(N'dbo',N'aging_param_history_sum',N'auto'),
(N'dbo',N'monthly_move_stats',N'auto'),
(N'dbo',N'mitel',N'auto'),
(N'dbo',N'queue_group_answer_spectrum',N'auto'),
(N'dbo',N'ns_daily_cash_by_cycle_view',N'auto'),
(N'dbo',N'seniorcc_emails',N'auto'),
(N'dbo',N'collections_emails_view',N'auto'),
(N'dbo',N'ns_collection_submission_accounts_pbi',N'function'),
(N'dbo',N'ns_collection_submission_accounts_bankrupt',N'function'),
(N'dbo',N'multi_user_conditions_of_service',N'auto'),
(N'dbo',N'request_service_layout_form_geo',N'auto'),
(N'dbo',N'aging_ml_predictions',N'auto'),
(N'dbo',N'ml_metrics',N'auto'),
(N'dbo',N'model_metrics',N'auto'),
(N'dbo',N'risky_comm_predictor_details',N'auto'),
(N'dbo',N'model_metrics_resid',N'auto'),
(N'dbo',N'risky_resid_predictor_details',N'auto'),
(N'dbo',N'ns_daily_ebnotes',N'function'),
(N'dbo',N'ns_total_bills_monthly',N'function'),
(N'dbo',N'AgingCompareSummary_view',N'auto');

DECLARE @Results TABLE
(
    SchemaName sysname,
    ObjectName sysname,
    ExpectedKind nvarchar(20),
    SqlObjectType nvarchar(60) NULL,
    ReadSucceeded bit NOT NULL,
    ErrorMessage nvarchar(4000) NULL
);

DECLARE @schema sysname, @object sysname, @kind nvarchar(20), @sql nvarchar(max);
DECLARE source_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT SchemaName,ObjectName,ExpectedKind FROM @Sources ORDER BY SchemaName,ObjectName;

OPEN source_cursor;
FETCH NEXT FROM source_cursor INTO @schema,@object,@kind;
WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        SET @sql = CASE WHEN @kind = N'function'
            THEN N'SELECT TOP (1) * FROM ' + QUOTENAME(@schema) + N'.' + QUOTENAME(@object) + N'();'
            ELSE N'SELECT TOP (1) * FROM ' + QUOTENAME(@schema) + N'.' + QUOTENAME(@object) + N';'
        END;
        EXEC sys.sp_executesql @sql;

        INSERT @Results
        SELECT @schema,@object,@kind,o.type_desc,1,NULL
        FROM (VALUES(1)) x(n)
        LEFT JOIN sys.objects o
          ON o.object_id = OBJECT_ID(QUOTENAME(@schema)+N'.'+QUOTENAME(@object));
    END TRY
    BEGIN CATCH
        INSERT @Results
        SELECT @schema,@object,@kind,o.type_desc,0,ERROR_MESSAGE()
        FROM (VALUES(1)) x(n)
        LEFT JOIN sys.objects o
          ON o.object_id = OBJECT_ID(QUOTENAME(@schema)+N'.'+QUOTENAME(@object));
    END CATCH;

    FETCH NEXT FROM source_cursor INTO @schema,@object,@kind;
END;
CLOSE source_cursor;
DEALLOCATE source_cursor;

SELECT *
FROM @Results
ORDER BY ReadSucceeded,SchemaName,ObjectName;
GO
