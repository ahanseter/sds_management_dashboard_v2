namespace JJKeller.SdsManagementDashboard.Models;

/// <summary>Which date column the filter applies to. Values map to a fixed column whitelist.</summary>
public enum DateField
{
    FulfilledOn, // R.ModifiedDate
    RequestedOn  // R.CreatedDate
}

/// <summary>The date-period operators offered in the UI. Parsed from a whitelisted set.</summary>
public enum DateOperator
{
    Equals,
    NotEqual,
    After,
    Before,
    Between,
    Blank,
    NotBlank,
    Today,
    Yesterday,
    Last7Days,
    Last30Days,
    ThisMonth,
    LastMonth,
    ThisYear,
    LastYear,
    YearToDate
}

/// <summary>
/// A validated date filter. Only <see cref="TryCreate"/> can build one, so the query layer can
/// trust the field/operator are whitelisted and any required dates are present.
/// </summary>
public sealed record SdsRequestFilter(DateField Field, DateOperator Operator, DateOnly? From, DateOnly? To)
{
    /// <summary>How many date inputs an operator needs: 0 (relative), 1 (single), or 2 (Between).</summary>
    public static int RequiredDateCount(DateOperator op) => op switch
    {
        DateOperator.Equals or DateOperator.NotEqual or DateOperator.After or DateOperator.Before => 1,
        DateOperator.Between => 2,
        _ => 0
    };

    public static bool TryCreate(
        string? field,
        string? op,
        DateOnly? from,
        DateOnly? to,
        out SdsRequestFilter filter,
        out string? error)
    {
        filter = default!;
        error = null;

        // Default to the original report: Fulfilled on = Yesterday.
        var dateField = DateField.FulfilledOn;
        if (!string.IsNullOrWhiteSpace(field) && !Enum.TryParse(field, ignoreCase: true, out dateField))
        {
            error = $"Unknown date field '{field}'.";
            return false;
        }

        var dateOperator = DateOperator.Yesterday;
        if (!string.IsNullOrWhiteSpace(op) && !Enum.TryParse(op, ignoreCase: true, out dateOperator))
        {
            error = $"Unknown filter operator '{op}'.";
            return false;
        }

        var required = RequiredDateCount(dateOperator);
        if (required >= 1 && from is null)
        {
            error = "This filter requires a date.";
            return false;
        }

        if (required == 2 && to is null)
        {
            error = "The 'Between' filter requires both a start and end date.";
            return false;
        }

        if (dateOperator == DateOperator.Between && from > to)
        {
            error = "The start date must be on or before the end date.";
            return false;
        }

        filter = new SdsRequestFilter(dateField, dateOperator, from, to);
        return true;
    }
}
