/*
Validates the latest stored Call Handling abandoned percentage.
The expected formula is abandoned-within-30-seconds / calls-answered * 100.
This query does not invent an abandoned count from offered minus answered.
*/

SELECT
    visual_key,
    snapshot_date,
    row_label,
    current_month_label,
    current_month_value AS stored_current_abandoned_pct,
    ytd_value AS stored_ytd_abandoned_pct,
    target_value,
    status_current,
    status_ytd,
    source_name,
    loaded_at_utc
FROM its_dashboard_dev.cx.original_composition_table_row
WHERE visual_key = N'call_handling'
  AND ISNULL(is_sample_data, 0) = 0
  AND snapshot_date =
  (
      SELECT MAX(snapshot_date)
      FROM its_dashboard_dev.cx.original_composition_table_row
      WHERE visual_key = N'call_handling'
        AND ISNULL(is_sample_data, 0) = 0
  )
  AND
  (
      TRY_CONVERT(int, row_sort) = 4
      OR LOWER(COALESCE(row_label, N'')) LIKE N'%abandon%'
  );

-- Reference calculation supplied for June:
SELECT
    CAST(77 AS decimal(19, 6)) / NULLIF(CAST(6743 AS decimal(19, 6)), 0) * 100.0
        AS expected_abandoned_pct; -- 1.141924...; UI displays 1% at zero decimals
