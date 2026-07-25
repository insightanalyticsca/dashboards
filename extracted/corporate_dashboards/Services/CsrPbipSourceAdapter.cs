using System.Globalization;

namespace corporate_dashboards.Services;

/// <summary>
/// Reproduces the Power Query/model shaping that sits between the physical
/// corporate_dashboards objects and the PBIP semantic entities.
///
/// Rows retain their physical SQL columns and receive semantic aliases and
/// calculated columns used by the imported report visuals. This keeps the
/// HTML runtime aligned with the PBIP field names without changing source DBs.
/// </summary>
public static class CsrPbipSourceAdapter
{
    public static List<Dictionary<string, object?>> Adapt(
        string semanticEntity,
        IEnumerable<Dictionary<string, object?>> sourceRows)
    {
        var entity = (semanticEntity ?? string.Empty).Trim();
        var result = new List<Dictionary<string, object?>>();

        foreach (var sourceRow in sourceRows)
        {
            var row = new Dictionary<string, object?>(sourceRow, StringComparer.OrdinalIgnoreCase);

            switch (entity.ToLowerInvariant())
            {
                case "agingcube_net":
                    SetAlias(row, "Account", "ACCOUNT", "Account");
                    break;

                case "aging_trans_details":
                    // PBIP M: null trans_type -> CR; exclude the single-space value.
                    var transactionType = Read(row, "trans_type");
                    if (transactionType is null || transactionType == DBNull.Value)
                    {
                        row["trans_type"] = "CR";
                    }
                    else if (string.Equals(Convert.ToString(transactionType, CultureInfo.InvariantCulture), " ", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    break;

                case "mitel":
                    // PBIP M renames for the principal Mitel table.
                    SetAlias(row, "Interval", "TimeIntervalFormatted", "Interval");
                    SetAlias(row, "Long Calls", "LongerCalls", "Long Calls");
                    SetAlias(row, "Calls", "Time", "Calls");
                    SetAlias(row, "Agent", "Full Name", "Agent");
                    SetValue(row, "Long Call Flag", LongCallFlag(row));
                    SetValue(row, "Duration_Time", DurationMinutes(Read(row, "Duration")));
                    break;

                case "mitel_dynamics":
                    // Same physical dbo.mitel object, without the Interval rename in M.
                    SetAlias(row, "Long Calls", "LongerCalls", "Long Calls");
                    SetAlias(row, "Calls", "Time", "Calls");
                    SetAlias(row, "Agent", "Full Name", "Agent");
                    SetAlias(row, "Interval", "TimeIntervalFormatted", "Interval");
                    SetValue(row, "Long Call Flag", LongCallFlag(row));
                    SetValue(row, "Duration_Time", DurationMinutes(Read(row, "Duration")));
                    break;

                case "queue_group_answer_spectrum":
                    // PBIP M: month_abbr -> m. y/m/d are date hierarchy roles.
                    SetAlias(row, "m", "month_abbr", "m", "month");
                    SetAlias(row, "y", "year", "y");
                    SetAlias(row, "d", "date", "d");
                    break;

                case "ns_daily_cash_by_cycle_view":
                    // PBIP M renames and null replacement.
                    SetAlias(row, "Amount", "TRANS_AMT", "Amount");
                    SetValue(row, "Types", Read(row, "DESCRIPTION", "Types") ?? string.Empty);
                    SetAlias(row, "Date", "TRANS_DATE", "trans_date", "Date");
                    SetAlias(row, "Month", "dc_month_name", "month_name", "Month");
                    SetAlias(row, "Year", "Year", "year");
                    SetAlias(row, "SEQUENCE_", "SEQUENCE_", "sequence_");
                    SetAlias(row, "ACCOUNT_NO", "ACCOUNT_NO", "account_no");
                    SetAlias(row, "OCCUPANT_CODE", "OCCUPANT_CODE", "occupant_code");
                    SetAlias(row, "NAME", "NAME", "name");
                    break;

                case "collection_function":
                    // PBIP semantic aliases over dbo.ns_collection_submission_accounts_pbi().
                    SetAlias(row, "Account", "AccountNumber", "Account");
                    SetAlias(row, "Occupant", "OccupantCode", "Occupant");
                    SetAlias(row, "category_code", "category", "category_code");
                    SetAlias(row, "Balance", "CurrentBalance", "Balance");
                    SetAlias(row, "Date In", "DateIn", "Date In");
                    break;

                case "ml_metrics":
                    SetValue(row, "metrics", BuildMlMetricsText(row));
                    break;

                case "ns_daily_ebnotes":
                    AddMonthKeys(row, "year", "month");
                    // The report filter treats this semantic source as e-bill rows.
                    SetValue(row, "IsEBill", "EBilling");
                    break;

                case "ns_total_bills_monthly":
                    AddMonthKeys(row, "gl_year", "gl_month");
                    break;
            }

            result.Add(row);
        }

        if (entity.Equals("ns_daily_cash_by_cycle_view", StringComparison.OrdinalIgnoreCase))
        {
            // PBIP M sorts Types descending. Preserve stable ordering client-side.
            result.Sort((left, right) => string.Compare(
                Convert.ToString(Read(right, "Types"), CultureInfo.InvariantCulture),
                Convert.ToString(Read(left, "Types"), CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase));
        }

        return result;
    }

    private static void AddMonthKeys(Dictionary<string, object?> row, string yearField, string monthField)
    {
        var year = ToInt(Read(row, yearField));
        var month = ToInt(Read(row, monthField));
        if (year <= 0 || month is < 1 or > 12) return;

        var textKey = $"{year:D4}-{month:D2}";
        var numericKey = year * 100 + month;
        SetValue(row, "month-year", textKey);
        SetValue(row, "year-month", textKey);
        SetValue(row, "Bill Month Key", numericKey);
    }

    private static string BuildMlMetricsText(Dictionary<string, object?> row)
    {
        var r2 = ToDouble(Read(row, "R2"));
        var rounded = Math.Round(r2 * 100d, 2, MidpointRounding.AwayFromZero)
            .ToString("0.################", CultureInfo.InvariantCulture);

        // Exact calculated-column behavior: LEFT(ROUND(R2 * 100, 2), 5).
        var leftFive = rounded.Length <= 5 ? rounded : rounded[..5];
        var runDate = Convert.ToString(Read(row, "RunDateTime"), CultureInfo.InvariantCulture) ?? string.Empty;

        return "Prophet Forecast with Advanced Statistical Modeling: Dynamic Seasonality and Residual Optimization" +
               "   |   Model Accuracy: " + leftFive + "%" +
               "   |   Last Retrained: " + runDate;
    }

    private static string LongCallFlag(Dictionary<string, object?> row)
    {
        return ToInt(Read(row, "Long Calls", "LongerCalls")) == 1 ? "Longer Call" : string.Empty;
    }

    private static double DurationMinutes(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var parts = text.Split(':');
        if (parts.Length == 3 &&
            double.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var hours) &&
            double.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var minutes) &&
            double.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds))
        {
            return ((hours * 3600d) + (minutes * 60d) + seconds) / 60d;
        }

        return ToDouble(value);
    }

    private static object? Read(Dictionary<string, object?> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetValue(name, out var value)) return value;
        }
        return null;
    }

    private static void SetAlias(Dictionary<string, object?> row, string target, params string[] sourceNames)
    {
        if (row.TryGetValue(target, out var existing) && existing is not null && existing != DBNull.Value) return;
        var value = Read(row, sourceNames);
        if (value is not null && value != DBNull.Value) row[target] = value;
    }

    private static void SetValue(Dictionary<string, object?> row, string target, object? value)
    {
        if (row.TryGetValue(target, out var existing) && existing is not null && existing != DBNull.Value) return;
        row[target] = value;
    }

    private static int ToInt(object? value)
    {
        if (value is null || value == DBNull.Value) return 0;
        if (value is int i) return i;
        if (value is long l && l is >= int.MinValue and <= int.MaxValue) return (int)l;
        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static double ToDouble(object? value)
    {
        if (value is null || value == DBNull.Value) return 0d;
        if (value is double d) return d;
        if (value is float f) return f;
        if (value is decimal m) return (double)m;
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d;
    }
}
