# Enterprise integration adapters

The applications use one provider-neutral integration boundary so module code does not depend on Fulcrum or Acumatica response formats.

## How routing works

1. An administrator selects the active provider in **Admin Hub > API keys**.
2. The selection is stored once in the shared `EnterpriseIntegrationSettings` table. Existing installations default to `Fulcrum`, and every change is retained in `EnterpriseIntegrationSettingAudits` with the administrator and timestamp.
3. A module requests business data by a stable route such as `project-quantities` or `estimating-quotes`.
4. `EnterpriseAdapterSelector` chooses the adapter matching both the active provider and the requested route.
5. The adapter handles provider authentication, endpoints, paging, and field mapping, then returns the module's existing internal data shape.
6. The module applies the data using its normal validation, permissions, concurrency checks, and audit workflow.

The Estimating job schedule is provider-neutral under `EnterpriseQuoteSync`. Existing deployments that still use schedule values under `FulcrumQuoteSync` remain compatible until their configuration file is updated.

Engineering and Quality route names are reserved in the shared contract. Their adapters can be added when the exact records to exchange are defined without changing their screens or the provider selector.

## Current adapters

| Data route | Fulcrum | Acumatica |
| --- | --- | --- |
| Estimating quotes | Live | Safe placeholder |
| Project quantities | Live | Safe placeholder |
| Engineering records | Route reserved | Route reserved |
| Quality records | Route reserved | Route reserved |

The Acumatica placeholders intentionally stop with a clear configuration message. They must not return dummy data or silently fall back to Fulcrum after Acumatica is selected.

## Completing the Acumatica connection

For each data route, add an Acumatica adapter that implements the route's internal interface. The adapter is the only code that should know Acumatica endpoint paths, authentication details, paging, or response fields.

Before activation:

1. Confirm the Acumatica tenant URL and authentication method.
2. Save the protected Acumatica credential in Admin Hub.
3. Map Acumatica fields to the existing internal route result.
4. Add contract tests using representative Acumatica responses.
5. Run a read-only comparison against Fulcrum output for the same records.
6. Activate Acumatica in Admin Hub after the comparison passes.
7. Confirm the next scheduled Estimating run and a manual Project Tracker quantity pull.

Adding future push behavior follows the same pattern: define a stable business route and internal request/result types, implement one adapter per provider, and keep module screens and workflows provider-neutral.
