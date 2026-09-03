using System.Data;
using JJKeller.SdsManagementDashboard.Models;
using Microsoft.Data.SqlClient;

namespace JJKeller.SdsManagementDashboard.Services;

/// <summary>
/// Runs the read-only "SDS requests" report against the SDS database. The SELECT/FROM is fixed;
/// only the date predicate varies, and it is built from a whitelisted column + operator with all
/// user-supplied dates passed as parameters, so there is no SQL-injection surface.
/// </summary>
public sealed class SdsRequestQueryService
{
    // Verbatim report projection + joins supplied by the business. Read-only. The WHERE clause
    // (date predicate + IsDeleted = 0) is appended per request. Intentionally cross-tenant (all
    // companies) — this is an internal admin/ops view, not a customer-facing one.
    private const string SelectFrom = """
        SELECT R.CompanyName as 'Requesting Company'
        ,RS.[Name] as 'Status'
        ,R.ProductName as 'Product Name'
        ,R.ManufacturerName as 'Manufacturer'
        ,R.ManufacturerPartNumber as 'Manufacturer Part Number'
        ,FORMAT(R.CreatedDate, 'MM/dd/yyyy') as 'Requested On'
        ,FORMAT(R.ModifiedDate, 'MM/dd/yyyy') as 'Fulfilled on'
        ,CONCAT(UDP.[FirstName],' ', UDP.LastName) as 'Fulfilled By'
        ,(CASE
            WHEN R.AiAcquisitionAttempted = 1 OR R.AiAcquisitionAttempted = 0 THEN 'No'
            ELSE 'Yes'
        END) as 'AI Attempted'
        ,APS.[Name] as 'AI Processing Status'
        FROM SDS.Request R
        join sds.RequestStatus RS
        on RS.Id = R.RequestStatusId
        Join JJK.[User] U
        On R.ModifiedByUserId = U.Id
        Join JJK.[UserDataProfile] UDP
        On U.Id = UDP.UserId
        Join SDS.AiProcessingStatus APS
        ON R.AiProcessingStatusId = APS.Id
        """;

    private readonly string _connectionString;

    public SdsRequestQueryService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("SdsProdDb")
            ?? throw new InvalidOperationException(
                "Missing connection string 'ConnectionStrings:SdsProdDb'. Provision it in Key Vault " +
                "as secret 'ConnectionStrings--SdsProdDb' (see README).");
    }

    public async Task<QueryResult> GetRequestsAsync(SdsRequestFilter filter, CancellationToken cancellationToken)
    {
        // Column comes from a fixed whitelist keyed by the enum — never interpolated from raw input.
        var column = filter.Field switch
        {
            DateField.RequestedOn => "R.CreatedDate",
            _ => "R.ModifiedDate"
        };

        var parameters = new List<SqlParameter>();
        var predicate = BuildDatePredicate(column, filter, parameters);
        var sql = $"{SelectFrom}\r\nWHERE {predicate}\r\n  AND R.IsDeleted = 0;";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddRange(parameters.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns[i] = reader.GetName(i);
        }

        var rows = new List<string?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : reader.GetValue(i).ToString();
            }

            rows.Add(row);
        }

        return new QueryResult(columns, rows, DateTimeOffset.Now);
    }

    /// <summary>
    /// Builds a parameterized date predicate for the whitelisted <paramref name="column"/>.
    /// Half-open intervals [start, end) throughout. Relative "Last N Days" windows are the N full
    /// days before today (today excluded), consistent with "Yesterday".
    /// </summary>
    private static string BuildDatePredicate(string column, SdsRequestFilter filter, List<SqlParameter> parameters)
    {
        void AddDate(string name, DateOnly value) =>
            parameters.Add(new SqlParameter(name, SqlDbType.Date) { Value = value.ToDateTime(TimeOnly.MinValue) });

        switch (filter.Operator)
        {
            case DateOperator.Equals:
                AddDate("@from", filter.From!.Value);
                return $"({column} >= @from AND {column} < DATEADD(day, 1, @from))";

            case DateOperator.NotEqual:
                AddDate("@from", filter.From!.Value);
                return $"({column} < @from OR {column} >= DATEADD(day, 1, @from))";

            case DateOperator.After:
                AddDate("@from", filter.From!.Value);
                return $"({column} >= DATEADD(day, 1, @from))";

            case DateOperator.Before:
                AddDate("@from", filter.From!.Value);
                return $"({column} < @from)";

            case DateOperator.Between:
                AddDate("@from", filter.From!.Value);
                AddDate("@to", filter.To!.Value);
                return $"({column} >= @from AND {column} < DATEADD(day, 1, @to))";

            case DateOperator.Blank:
                return $"({column} IS NULL)";

            case DateOperator.NotBlank:
                return $"({column} IS NOT NULL)";

            case DateOperator.Today:
                return $"({column} >= CAST(GETDATE() AS DATE) AND {column} < DATEADD(day, 1, CAST(GETDATE() AS DATE)))";

            case DateOperator.Yesterday:
                return $"({column} >= CAST(DATEADD(day, -1, GETDATE()) AS DATE) AND {column} < CAST(GETDATE() AS DATE))";

            case DateOperator.Last7Days:
                return $"({column} >= CAST(DATEADD(day, -7, GETDATE()) AS DATE) AND {column} < CAST(GETDATE() AS DATE))";

            case DateOperator.Last30Days:
                return $"({column} >= CAST(DATEADD(day, -30, GETDATE()) AS DATE) AND {column} < CAST(GETDATE() AS DATE))";

            case DateOperator.ThisMonth:
                return $"({column} >= DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1) " +
                       $"AND {column} < DATEADD(month, 1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)))";

            case DateOperator.LastMonth:
                return $"({column} >= DATEADD(month, -1, DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)) " +
                       $"AND {column} < DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1))";

            case DateOperator.ThisYear:
                return $"({column} >= DATEFROMPARTS(YEAR(GETDATE()), 1, 1) " +
                       $"AND {column} < DATEFROMPARTS(YEAR(GETDATE()) + 1, 1, 1))";

            case DateOperator.LastYear:
                return $"({column} >= DATEFROMPARTS(YEAR(GETDATE()) - 1, 1, 1) " +
                       $"AND {column} < DATEFROMPARTS(YEAR(GETDATE()), 1, 1))";

            case DateOperator.YearToDate:
                return $"({column} >= DATEFROMPARTS(YEAR(GETDATE()), 1, 1) " +
                       $"AND {column} < DATEADD(day, 1, CAST(GETDATE() AS DATE)))";

            default:
                // Unreachable — SdsRequestFilter.TryCreate validates the operator before we get here.
                throw new ArgumentOutOfRangeException(nameof(filter), filter.Operator, "Unhandled date operator.");
        }
    }
}

public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<string?[]> Rows,
    DateTimeOffset GeneratedAt);
