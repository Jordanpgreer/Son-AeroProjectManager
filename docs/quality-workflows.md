# Quality Assurance workflows

The workflow map replaces the Assignment Rules editor. Definitions currently belong only to `quality-assurance`. The module key reserves a boundary for later expansion; other modules do not execute this engine.

## Drafts and publishing

- Opening the editor and testing a path are read-only operations. The first draft previews the existing enabled creation rules in priority order and the configured QA Complete shipping queue, when that queue is eligible.
- Saving a draft persists its graph and an audit snapshot. Drafts can be incomplete and do not change live assignment behavior.
- Publishing validates the whole graph and current destination eligibility, copies the saved draft to the published definition, and increments the published revision. The caller must supply the current document version when saving or publishing; stale writes return HTTP 409.
- Publishing changes future actions only. Existing assignments and legacy rule rows are retained. Published workflows replace legacy assignment-rule evaluation; the old rules remain available as historical source data.
- The initial **Shipment updated** branch keeps the current assignment. This deliberately differs from the legacy rule service, which reran matching rules after customer/task-type changes on unassigned records. Administrators can configure the updated branch explicitly before publishing.
- Each saved draft and publication records the actor, timestamp, revision, and full definition. Shipment execution audits record the published revision, trigger, exact path, previous assignment, and resulting assignment in the same save as the action.

## Supported actions

| Trigger | Native action |
| --- | --- |
| `shipment-created` | Create a shipment |
| `shipment-updated` | Save changed shipment fields |
| `assignment-changed` | Save a changed or confirmed assignment |
| `qa-completed` | QA Complete; status becomes Ready to Ship |
| `shipment-shipped` | Mark Shipped; closes the shipment |
| `shipment-imported` | Import a newly created workbook row |

Unchanged requests do not rerun action triggers. Reimported matching rows do not execute routing, but the `shipment-imported` access restriction applies to the entire import request before any existing-row reconciliation. Workflows do not call external APIs, send messages, or grant account permissions. A trigger missing from the published graph leaves that action's built-in behavior in place, except that publishing globally supersedes legacy assignment-rule evaluation.

QA Complete uses its configured Shipper queue when there is no published QA Complete trigger. With a trigger, routing follows that path; an End step keeps the current assignment while the native status transition still occurs. Mark Shipped remains a completed shipment even when a route changes its recorded owner.

## Graph and access rules

Graphs permit triggers, conditions, queue routes, and End steps, with up to 100 nodes and 200 connections. Every node must be reachable, paths must finish, and cycles are rejected. Each action has at most one trigger. Conditions require exactly one Yes and one No path. A route either terminates or connects to one End step.

Conditions use Customer, Task type, Status, or Hold reason, with Equals, Contains, Starts with, or Is empty. Comparisons trim text and ignore case. Conditions see the shipment after the native action's changes, including Ready to Ship or Shipped status.

The path tester applies the same automatic status changes for QA Complete and Mark Shipped. Those status inputs are fixed to the action's resulting status, so the preview and real execution take the same branch.

Routes target a currently eligible Quality Responsible Group. Specific-user routes also require an active eligible member of that group. Least-loaded selection considers eligible group members' open assigned shipments, including earlier unsaved rows in the same import, and breaks ties by user ID; the decision is made at execution time. Separate concurrent requests may observe the same queue counts. If a least-loaded queue has no eligible people, work remains in that group queue. Simulation previews the branch and assignment mode without reserving a recipient or altering data.

All native endpoint permissions, field edit checks, record visibility, and optimistic concurrency checks remain in force. Optional trigger access groups further restrict an action; they cannot make an otherwise forbidden action available. The API rechecks these restrictions at execution. `/api/me` returns only the caller's `workflowRestrictedActions`, and dashboard assignment flags and editable field metadata also reflect current restrictions. The editor and its endpoints require the existing Quality Rules Manage permission.

## Database and validation

Migration `20260910165911_AddQualityWorkflows` adds `QualityWorkflows` and `QualityWorkflowAuditEntries`. It supports SQLite and SQL Server. Follow the normal Hub release process to apply this schema change; a local build is not a deployment.

Backend checks:

```powershell
& 'C:\Users\USER\.dotnet\dotnet.exe' test apps/quality-assurance/tests/QualityAssurance.Tests/QualityAssurance.Tests.csproj --no-restore -p:SkipClientBuild=true
```

The tests cover graph branching and rejection, draft isolation, stale writes, management authorization, destination eligibility, native action routing, trigger restrictions, least-loaded assignment, execution auditing, legacy-assignment protection, and both database providers' migration contracts.

Frontend validation covers safe graph connections and branch replacement, drag undo, permission gating, and existing Portal and QA regressions. The editor was browser-checked with a disposable QA database for saved drafts, publish review, Yes/No traces, desktop and mobile themes, navigation protection, and a clean console after dragging.

## Local preview

`ClientApp/tests/workflow-preview.config.ts` can run the Portal editor on port 5240 with `npm run dev -- --config tests/workflow-preview.config.ts`. It points QA requests to a separately started QA API on port 5271. Start that API in Development with an explicit disposable `ConnectionStrings__QualityStore`, `Portal__Url=http://localhost:5240`, and disabled integration/push workers. Authentication uses the existing local Hub services; only the QA database is isolated. The normal production build does not load this preview configuration.
