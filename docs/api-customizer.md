# Admin API Customizer

Open **Arda Admin > API Customizer** (`#/admin/api-customizer/overview`).
This separate, read-only Fulcrum report builder requires the Arda **Admin** role
on both the page and every backend endpoint. It does not import records into
Engineering, Quality, Estimating, or Project Tracker, and does not change their
existing integration rules or scheduled syncs.

## Build A Workbook

For the supplied inventory BOM layout, choose **Inventory BOM** on the starting
screen. Its 32 column headings and their order are preconfigured. Enter item
numbers, a prefix, or Fulcrum IDs, or leave the item search blank for all matching
items. Item status and revision filters are immediately available. Preview and
download directly, or use **Customize Columns** to rename, reorder, remove, or
remap columns. **Add Fulcrum Fields** searches the prepared fields and original
item, routing-step, and input-item fields. Saved reports retain custom mappings.
The preview hides unmapped template placeholders by default, with a toggle to
show them. Excel still includes all selected columns. **Remove Unmapped Columns**
in the customization dialog reduces the layout to the mapped fields in one click.
**Report Options > Connect Other Fulcrum Records** opens the general relationship
builder if additional related API sources are needed.

To create another kind of report:

1. Choose **Build A Different Report** and search for any starting record type. No categories or example worksheets are imposed on custom reports.
2. Choose the fields you want to see. Use **Filter Records** to limit the data, such as item IDs, item numbers, or status. Applied and required filters appear first; other filters expand only when selected.
3. Choose **Add Related Information** on a selected record type. Arda offers compatible relationships and automatically connects their IDs. No manual parent-ID mapping is needed for suggested relationships.
4. Choose **Each Row Represents**: starting records for a summary with related lists in cells, or a related record type for a detailed report. Drag fields or use the arrows to reorder. Heading/number format edits are under **Rename Or Format Columns**. Widths and heights adjust automatically, with long values wrapping.
5. Choose **Try Sample Data** to test without a token or API calls. Samples are invented records, not a simulation of your filters.
6. For real data, save the **Fulcrum Public API** credential under **Admin > API Keys**, then choose **Preview Fulcrum Data**. Your token must have permission for each selected source.
7. Review the combined report and choose **Download Excel**. Every row in the snapshot is included, not just the 50-row preview page. **Report Options** offers separate Excel tables, record limits, and sorting. No column dimensions need to be entered.
8. **Save Report** makes the definition reusable by Arda administrators. **Report Activity** lists saves/deletions, successful pulls, samples, and downloads with actor and time. Existing saved definitions remain available; concurrent edits are rejected rather than overwritten.

## Combining Related Data

Each selected source reads a collection or specific record. A linked source is
read for each parent record. Relationships use matching resource IDs, not names
or a generic ID from an unrelated resource. Advanced specific-record sources
remain selectable and require fixed IDs in their filters.

Combined reports have one row per chosen record type. Related ancestor fields
repeat on detail rows. Other related branches are collected in multiline cells
under their shared parent, not cross-multiplied or summed. Selecting the starting
record as the row type gives a summary. Selecting routing steps, for example,
gives individual step rows with matching item data. Empty related records are
retained when the source's keep-empty setting is enabled. Source arrays can
remain JSON or be displayed through their nested fields.

For current item routing/BOM data, Items defaults to **Is Archived = No** and
**Latest Revision = Yes**. These are item filters, not a separate active-BOM
approval/status: the bundled API does not document such a flag. Use the precise
item revision/ID required for your report. API include flags for selected item
vendor/customer/tier/usage fields are added automatically by the field picker.

Calculations, pivot tables, arbitrary unrelated joins, scheduled reports, and
write-back are not part of this page. Separate-table exports remain supported.

## Inventory BOM Mapping

The ready report composes only documented read endpoints: the items v2 list,
each item's routing operations list, and each item's routing input-items list.
It uses exact item IDs and exact `routingStepId` matches, orders steps by their
numeric order, and outputs one row per inventory input. Steps without inputs,
items without routings, unassigned inputs, and inputs with missing operation
references are retained. Missing operation references produce a warning. No
cross-product or name-based join is used. Operation fields normally appear only
on the first input row of a step, matching the example's continuation layout.
**Repeat Operation Details On Every Row** is available for flat-data analysis.
Sorting or connecting additional records automatically repeats operation
details so continuation lines cannot become detached from their context.

Mapped fields: Revision, Inventory ID (item number), Description, Operation Nbr,
Operation Descr, Setup Time, Run Units, Run Time, Machine Units, Machine Time,
Matl Inventory ID (input item number), Qty Req, and NOTES (item notes plus routing
instructions). Fulcrum material-tied input items are included exactly once as
input lines. Raw material-shape/nesting definitions are not added as duplicate
consumption rows. They remain available as separate catalogue sources.

Times are converted from each API time option into Excel elapsed durations
(`[h]:mm:ss`). Per-unit times use a quantity of 1; units-per-hour uses one hour
and the source rate in the units column. Fixed durations leave units blank.
Unsupported or invalid time bases and non-fixed setup times are left blank with
a warning; their raw time/option fields remain selectable. Qty Req uses
`valueTypeUnits` only for a `requires` basis. `creates` is not silently treated
as consumption: it remains available as Source Quantity / Quantity Basis and
the derived Qty Req is blank with a warning.

The remaining template fields, including BOM ID, Hold, Warehouse, Work Center,
UOM, Unit Cost, and backflush flags, have no verified equivalent in these three
sources. They are visibly marked **Not Mapped / Blank**, not guessed from an
unrelated field or set to false/zero. Map them to an available field or exact
Fulcrum custom-field key, or remove them. The original workbook's business data
is not bundled, imported, or uploaded. This is a customizable report, not a
validated Acumatica import or migration file.

## Storage And Safety

- Credentials stay encrypted in the existing shared credential store and are used only on the server. The browser never receives the token.
- Outbound calls are restricted to the existing Fulcrum ITAR API origin, with redirects disabled. Only catalogued GET JSON reads and documented POST list reads are allowed; users cannot enter arbitrary URLs or HTTP methods.
- Layouts and audit metadata are saved in additive `ApiCustomizerReports` and `ApiCustomizerAudits` tables in the existing Portal RoleStore. Audits retain old/new definitions, which can contain filter values. Normal module data is untouched by report runs.
- Pulled records are not persisted to the database. One in-memory snapshot per administrator lasts 15 minutes; a new completed preview or application restart removes the earlier download's availability. Downloaded Excel files remain on the user's computer.
- Limits: 8 sources, 80 combined output fields, 1-5,000 records per individual source/filter invocation, 20,000 fetched rows across sources, 500 API calls, 20 MB per response/preview, two concurrent runs, and a three-minute run timeout. Exceeding a limit fails rather than silently exporting partial data. Narrow filters for larger catalogues; reporting sources can avoid many per-record calls.
- Inventory BOM runs have bounded larger limits to accommodate inventory-sized output: 100,000 composed rows, 10,050 API calls, and ten minutes, with at most three parallel item readers per report. The 5,000-record per-read, 20 MB response/preview, and two-concurrent-report limits still apply. Large requests can still fail due to vendor rate limits or hosting proxy timeouts; use item-number prefixes for smaller reports. Every selected record must finish before a snapshot/download is made available.
- Empty/missing relationship IDs, rejected tokens, insufficient permissions, incomplete pagination, malformed responses, and Excel's cell text limit produce errors. Source strings are never evaluated as Excel formulas, and text identifiers retain leading zeros.

## Catalogue Maintenance

The catalogue is generated from the public Fulcrum ITAR OpenAPI snapshot at
`https://api.fulcrumpro.us/swagger/v1/swagger.json`, retrieved on 2026-09-09.
`Services/ApiCustomizer/FulcrumSchema.json` is bundled into the Portal assembly;
catalogue browsing and sample mode work offline. Binary endpoints, deprecated
operations, write endpoints, and responses without a documented record schema
are excluded. Availability is limited by this snapshot and the company's token;
not every field in the Fulcrum website is necessarily in its API.

To refresh, review the vendor schema changes, replace the bundled public JSON,
update the catalogue version in `FulcrumReportCatalog`, run Portal backend and
frontend tests, and rebuild/publish Portal normally. Test the relevant live
sources with company credentials before production use. This feature adds no
new external service or persistent job; ClosedXML generates Excel on the server.
