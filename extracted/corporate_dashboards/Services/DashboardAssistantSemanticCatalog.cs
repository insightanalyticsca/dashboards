using corporate_dashboards.Models;

namespace corporate_dashboards.Services;

/// <summary>
/// Business-semantic contracts for the executive screens.
/// These definitions mirror the facts and dimensions exposed by
/// DashboardController.ExecutiveVersions rather than raw SQL columns.
/// </summary>
internal static class DashboardAssistantSemanticCatalog
{
    public static IReadOnlyList<AssistantColumnDto> GetFields(string? templateKey)
    {
        var key = (templateKey ?? string.Empty).Trim().ToLowerInvariant();

        return key switch
        {
            "executive-ebill-performance" => Ebill(),
            "executive-ar-portfolio" => ArPortfolio(),
            "executive-disconnects-bankruptcies" => Disconnects(),
            "executive-final-bill-recovery" => FinalBill(),
            "executive-customer-payments" => CustomerPayments(),
            _ => Array.Empty<AssistantColumnDto>()
        };
    }


    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetDimensionValueAliases(
        string? templateKey,
        string? dimensionName)
    {
        var template = (templateKey ?? string.Empty).Trim().ToLowerInvariant();
        var dimension = (dimensionName ?? string.Empty).Trim().ToLowerInvariant();

        if (template == "executive-customer-payments" && dimension == "payment_type")
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Credit Card"] = new[]
                {
                    "credit card", "credit cards", "card payment", "card payments",
                    "visa", "mastercard", "master card", "amex", "american express"
                },
                ["Debit Card"] = new[]
                {
                    "debit card", "debit cards", "interac", "bank card"
                },
                ["Cash"] = new[]
                {
                    "cash", "cash payment", "cash payments"
                },
                ["Cheque"] = new[]
                {
                    "cheque", "cheques", "check", "checks", "bank cheque"
                },
                ["Online Banking"] = new[]
                {
                    "online banking", "internet banking", "web banking", "online payment"
                },
                ["Pre-Authorized Payment"] = new[]
                {
                    "pre authorized payment", "pre-authorized payment", "preauthorized payment",
                    "automatic payment", "auto payment", "pap"
                },
                ["Electronic Funds Transfer"] = new[]
                {
                    "electronic funds transfer", "eft", "bank transfer", "wire transfer"
                },
                ["Other"] = new[]
                {
                    "other", "other payment", "other payments"
                }
            };
        }

        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AssistantColumnDto> CustomerPayments()
        => new List<AssistantColumnDto>
        {
            Fact(
                name: "payment_value",
                label: "Payment Value",
                valueFormat: "currency",
                isDefault: true,
                semanticPriority: 100,
                allowedDimensions: new[] { "period", "payment_type" },
                aliases: new[]
                {
                    "amount", "amounts", "value", "values", "payment", "payments",
                    "paid amount", "amount paid", "how much", "how much paid",
                    "how much was paid", "payment value", "payment amount", "payments amount",
                    "collection amount", "collections amount", "amount collected", "collected amount",
                    "collections", "collection", "cash collected", "cash amount",
                    "dollars collected", "money collected", "trans amt", "trans_amt"
                }),
            Fact(
                name: "transactions",
                label: "Transactions",
                valueFormat: "number",
                isDefault: false,
                semanticPriority: 95,
                allowedDimensions: new[] { "period" },
                aliases: new[]
                {
                    "transaction", "transactions", "count", "counts", "number", "volume",
                    "transaction count", "transactions count", "number of transactions",
                    "payment count", "payments count", "number of payments",
                    "payment volume", "transaction volume", "count of payments"
                }),
            DateDimension(
                "period",
                "Period",
                "date", "dates", "period", "periods", "month", "months",
                "year", "years", "reporting month", "payment month",
                "transaction month", "monthly", "over time", "trend"),
            Dimension(
                "payment_type",
                "Payment Type",
                "payment type", "payment types", "payment method", "payment methods",
                "method", "methods", "collection type", "collection types",
                "channel", "channels", "tender type", "description", "category", "type")
        };

    private static IReadOnlyList<AssistantColumnDto> Ebill()
        => new List<AssistantColumnDto>
        {
            Fact(
                "total_ebill_customers",
                "Total E-Bill Customers",
                "number",
                true,
                100,
                new[] { "period", "customer_type" },
                true,
                "total ebills", "total e bills", "total e-bills", "total ebill customers",
                "total e bill customers", "total customers", "ebill customers",
                "e-bill customers", "paperless customers", "enrolled customers",
                "customer total", "customers"),
            Fact(
                "new_ebill_customers",
                "New E-Bill Customers",
                "number",
                false,
                95,
                new[] { "period", "customer_type" },
                false,
                "new ebills", "new e bills", "new e-bills", "new customers",
                "new ebill customers", "new e-bill customers", "new enrollments",
                "new enrolments", "newly enrolled", "net new customers"),
            Fact(
                "total_ebill_percent",
                "Monthly E-Bill % (Total)",
                "percent",
                false,
                90,
                new[] { "period" },
                false,
                "ebill adoption", "e-bill adoption", "total ebill percent",
                "total e-bill percent", "monthly ebill percent", "total adoption rate",
                "ebill percentage", "e-bill percentage"),
            Fact(
                "new_ebill_percent",
                "Monthly E-Bill % (New)",
                "percent",
                false,
                85,
                new[] { "period" },
                false,
                "new ebill percent", "new e-bill percent", "new adoption percent",
                "new customer percentage", "new enrollment rate"),
            DateDimension(
                "period", "Period", "date", "period", "month", "year",
                "reporting month", "billing month", "monthly", "over time", "trend"),
            Dimension(
                "customer_type", "Customer Type", "customer type", "customer types",
                "category", "customer category", "segment", "residential",
                "commercial", "large commercial", "small commercial", "other", "unmetered")
        };

    private static IReadOnlyList<AssistantColumnDto> ArPortfolio()
        => new List<AssistantColumnDto>
        {
            Fact(
                "residential_arrears", "Residential Arrears", "currency", true, 100,
                new[] { "period", "aging_bucket" }, false,
                "residential arrears", "residential balance", "residential ar",
                "residential debt", "residential overdue", "residential amount"),
            Fact(
                "commercial_arrears", "Commercial Arrears", "currency", false, 95,
                new[] { "period", "aging_bucket" }, false,
                "commercial arrears", "commercial balance", "commercial ar",
                "commercial debt", "commercial overdue", "commercial amount"),
            Fact(
                "total_arrears_customers", "Total Arrears Customers", "number", false, 90,
                new[] { "period" }, true,
                "arrears customers", "customers in arrears", "overdue customers",
                "customer count", "number of customers"),
            Fact(
                "average_bill", "Average Bill", "currency", false, 85,
                new[] { "period" }, false,
                "average bill", "average arrears", "average balance", "avg bill",
                "mean bill", "mean balance"),
            DateDimension(
                "period", "Period", "date", "period", "month", "year",
                "reporting month", "monthly", "over time", "trend"),
            Dimension(
                "aging_bucket", "Aging Bucket", "aging bucket", "aging buckets",
                "bucket", "buckets", "age bucket", "age band", "0 30", "31 60",
                "61 90", "over 90", "90 plus")
        };

    private static IReadOnlyList<AssistantColumnDto> Disconnects()
        => new List<AssistantColumnDto>
        {
            Fact(
                "disconnected_accounts", "Disconnected Accounts", "number", true, 100,
                new[] { "period" }, false,
                "disconnects", "disconnected", "disconnected accounts",
                "accounts disconnected", "disconnect count"),
            Fact(
                "reconnected_accounts", "Reconnected Accounts", "number", false, 95,
                new[] { "period" }, false,
                "reconnects", "reconnected", "reconnected accounts",
                "accounts reconnected", "reconnect count"),
            Fact(
                "bankruptcy_accounts", "Bankruptcy Accounts YTD", "number", false, 90,
                new[] { "period", "customer_type" }, false,
                "bankruptcies", "bankruptcy customers", "bankruptcy accounts",
                "bankrupt accounts", "bankruptcy count"),
            Fact(
                "bankruptcy_amount", "Bankruptcy Amount YTD", "currency", false, 85,
                new[] { "period", "customer_type" }, false,
                "bankruptcy balance", "bankruptcy amount", "bankruptcy amount in",
                "bankrupt amount", "amount in"),
            DateDimension(
                "period", "Period", "date", "period", "month", "year",
                "reporting month", "monthly", "over time", "trend"),
            Dimension(
                "customer_type", "Customer Type", "customer type", "customer types",
                "account class", "category", "segment", "commercial", "residential")
        };

    private static IReadOnlyList<AssistantColumnDto> FinalBill()
        => new List<AssistantColumnDto>
        {
            Fact(
                "accounts", "Current-Year Accounts", "number", true, 100,
                new[] { "period", "customer_type" }, false,
                "accounts", "account count", "number of accounts", "final bill accounts",
                "collection accounts", "customer accounts"),
            Fact(
                "balance", "Current-Year Balance", "currency", false, 95,
                new[] { "period", "customer_type" }, false,
                "balance", "balances", "amount", "final bill balance",
                "collection balance", "outstanding balance"),
            Fact(
                "post_paid", "Current-Year Post Paid", "currency", false, 90,
                new[] { "period", "customer_type" }, false,
                "post paid", "post-paid", "paid after final bill", "collections",
                "recovered amount", "amount recovered", "recovery amount"),
            Fact(
                "paid_ratio", "Current-Year Paid Ratio", "percent", false, 85,
                new[] { "period", "customer_type" }, false,
                "paid ratio", "paid percent", "paid percentage", "recovery ratio",
                "collection rate", "recovery rate"),
            DateDimension(
                "period", "Period", "date", "date in", "period", "month", "year",
                "reporting month", "monthly", "over time", "trend"),
            Dimension(
                "customer_type", "Customer Type", "customer type", "customer types",
                "category", "segment", "commercial", "residential")
        };

    private static AssistantColumnDto Fact(
        string name,
        string label,
        string valueFormat,
        bool isDefault,
        int semanticPriority,
        string[] allowedDimensions,
        bool isSnapshot = false,
        params string[] aliases)
        => new()
        {
            Name = name,
            Label = label,
            DataType = "decimal",
            Category = "measure",
            Nullable = false,
            DefaultAggregation = "Sum",
            ValueFormat = valueFormat,
            IsDefault = isDefault,
            IsSnapshot = isSnapshot,
            SemanticPriority = semanticPriority,
            AllowedDimensions = allowedDimensions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Aliases = new[] { name, label }
                .Concat(aliases)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

    private static AssistantColumnDto Dimension(
        string name,
        string label,
        params string[] aliases)
        => new()
        {
            Name = name,
            Label = label,
            DataType = "nvarchar",
            Category = "dimension",
            Nullable = false,
            SemanticPriority = 100,
            Aliases = new[] { name, label }
                .Concat(aliases)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

    private static AssistantColumnDto DateDimension(
        string name,
        string label,
        params string[] aliases)
        => new()
        {
            Name = name,
            Label = label,
            DataType = "date",
            Category = "date",
            Nullable = false,
            SemanticPriority = 100,
            Aliases = new[] { name, label }
                .Concat(aliases)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
}
