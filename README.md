# SDS Request Dashboard

A small, standalone, **internal-only** ASP.NET Core 8 web app that shows the SDS requests
fulfilled in the prior calendar day. It is intended to run **behind the corp firewall / VPN**
as a virtual application under the QAS web UI host:

```
https://jjkp-qas-webui-webapp.corpr.jjkeller.local/sds_management_dashboard_v2
```

## What it does

- Serves one page (`App/index.html`) protected by **Okta OIDC** — there is no anonymous access.
- Exposes one read-only endpoint, `GET /api/sds-requests`, which runs the report query in
  [`Services/SdsRequestQueryService.cs`](Services/SdsRequestQueryService.cs) and returns JSON.
- Renders the results in a table with a CSV export (formula-injection-safe).

## Date filter

The dashboard filters by a **date field** (`Fulfilled on` = `ModifiedDate`, or `Requested on` =
`CreatedDate`) and a **period operator**. The UI shows date inputs conditionally: `Between` shows
two pickers, the single-date operators (`Equals`, `Does not equal`, `After`, `Before`) show one,
and the relative periods show none.

`GET /api/sds-requests` accepts (all optional; defaults reproduce the original **Fulfilled on =
Yesterday** report):

| Param | Values |
|---|---|
| `field` | `FulfilledOn` (default) · `RequestedOn` |
| `op` | `Equals` `NotEqual` `After` `Before` `Between` `Blank` `NotBlank` `Today` `Yesterday` `Last7Days` `Last30Days` `ThisMonth` `LastMonth` `ThisYear` `LastYear` `YearToDate` |
| `from` | `yyyy-MM-dd` — required for the single-date operators and `Between` |
| `to` | `yyyy-MM-dd` — required for `Between` |

`field` and `op` are validated against a **whitelist/enum** and `from`/`to` are passed as **SQL
parameters** — there is no string concatenation of user input into the query. Invalid input
returns `400` with an `{ error }` message.

**Semantics** (half-open intervals `[start, end)`): `Last 7 Days` / `Last 30 Days` are the N full
days **before today** (today excluded), matching `Yesterday`. `Year To Date` and `Today` include
today. Adjust the predicates in `BuildDatePredicate` if your team wants different boundaries.

## Security posture (read before deploying)

- **No secrets in source.** The prod DB connection string and the Okta client secret are resolved
  at runtime from **Azure Key Vault** (`ChainedTokenCredential`/managed identity). `appsettings.json`
  holds only non-secret values and placeholders. Config key `:` maps to a KV secret name `--`.
- **Network isolation is the primary boundary.** This app is only safe because it is reachable
  only on the corp network / VPN. Do **not** expose it publicly.
- **The report is intentionally cross-tenant** (all companies). That is a deliberate admin/ops
  view and bypasses the platform's usual `SecurityContext`/`CompanyId` scoping — keep it behind
  Okta + the firewall.
- **Cross-environment reach:** the app is hosted in **QAS** but reads the **prod** SDS database.
  That is intentional but means a prod DB connection string must be made available to a
  QAS-hosted app (see hand-off below).
- Output is rendered with `textContent` (never `innerHTML`); CSV cells starting with
  `= + - @` are neutralized.

## Configuration

| Key | Source | Notes |
|---|---|---|
| `KeyVault:Uri` | `appsettings.json` | Defaults to `jjkp-kv-stage` (QAS vault). |
| `Okta:Authority` | `appsettings.json` | QAS Okta org. |
| `Okta:ClientId` | `appsettings.json` | **Replace** with the new app registration's client id. |
| `Okta:ClientSecret` | **Key Vault** | Secret name `Okta--ClientSecret`. |
| `ConnectionStrings:SdsProdDb` | **Key Vault** | Secret name `ConnectionStrings--SdsProdDb`. |
| `PathBase` | `appsettings.json` | `/sds_management_dashboard_v2`. |

## Run locally

Requires the .NET 8 SDK and `az login` (for Key Vault access via `DefaultAzureCredential`).

```bash
cd JJKeller.Portal/Utilities/SdsManagementDashboard
dotnet run
```

Then browse to `https://localhost:<port>/sds_management_dashboard_v2/`. For local dev without
Key Vault, set the two secrets with user-secrets instead of committing them:

```bash
dotnet user-secrets set "ConnectionStrings:SdsProdDb" "<prod-read-only-connection-string>"
dotnet user-secrets set "Okta:ClientSecret" "<okta-client-secret>"
```

## Deployment hand-off (owned by the platform/infra team)

This code cannot be deployed from a developer machine to the target host — it goes to a locked-down
Azure App Service in the internal ILB ASE (`ts-ilb-ase-nonprod-usc`). The following must be
provisioned by someone with the right Azure/ADO access:

1. **Okta app registration** — a new OIDC **web** (confidential) app in the QAS Okta org, with
   redirect URI `https://jjkp-qas-webui-webapp.corpr.jjkeller.local/sds_management_dashboard_v2/signin-oidc`
   and post-logout URI `.../sds_management_dashboard_v2/signout-callback-oidc`. Put its client id in
   `appsettings.json` and its secret in Key Vault as `Okta--ClientSecret`.
2. **Key Vault secrets** in `jjkp-kv-stage`:
   - `ConnectionStrings--SdsProdDb` — a **read-only** connection string to `ts-jjkp-prod-sqldb`
     (recommend a least-privilege login limited to `SELECT` on the tables the query touches, and
     `ApplicationIntent=ReadOnly` if a readable secondary exists).
   - `Okta--ClientSecret`.
3. **App identity + access** — give the App Service's managed identity **get** on those KV secrets,
   and ensure network/firewall rules allow the QAS app to reach the prod SQL server.
4. **Hosting** — create the virtual application `/sds_management_dashboard_v2` under
   `jjkp-qas-webui-webapp` (or a dedicated App Service), and an ADO pipeline that publishes this
   project to it.

Until items 1–3 exist, the app builds and starts but the data endpoint will fail fast with a clear
error about the missing connection string.
