# Estimating Dashboard calculation contract

This document records the web module's parity contract with:

- `205 Rev E Estimating Quote Worksheet.xlsx`
- `Estimating Rates.xlsx`

The source workbooks remain the business reference. The application stores a reviewed snapshot of
their active 2023–2029 annual matrix so deployed calculations do not depend on a mapped drive.

## Controlled inputs

- Estimate models: Standard (`Rev E`) and Rubber (`Rubber Breakdown`)
- Quantity tiers: 10, 25, 50, 75, 100, 250, 500, and 1,000
- NRE quantity: 1
- Rate years: 2023 through 2029
- Standard operation rows: 3 fixed NRE rows and 10 controlled production rows
- Rubber operation rows: 4 fixed NRE/tooling rows and 22 controlled production rows
- Material rows: 12
- Outside-process rows: 5

Operation lookup follows Excel exact `VLOOKUP` behavior for this data: it is case-insensitive and
returns the first matching row. Duplicate `Burn Holes` and `Heat Seal` rows are intentionally
preserved in source order.

## Formula sequence

All calculations retain full numeric precision. Rounding is presentation-only.

For quantity `q` and labor rate per minute `r`:

```text
production operation unit cost = (setup minutes / q × r) + (run minutes × r)
raw fixed NRE                 = (setup minutes + run minutes) × r
```

Rubber purchase fixtures and mold/tooling contribute raw NRE only when their amortize control is
enabled:

```text
raw conditional tooling NRE =
  (setup minutes + run minutes) × r × (1 + tooling markup)
```

The workbook loads operation NRE with labor G&A and labor profit before amortization:

```text
loaded one-time NRE = raw one-time NRE × (1 + labor G&A) × (1 + labor profit)
amortized NRE       = loaded one-time NRE / q
```

For each material row:

```text
extended material cost = parts quantity × unit price

if Amortize / Min Buy is enabled:
  material unit cost = extended material cost / q
otherwise:
  material unit cost = extended material cost × (1 + 1 / q)
```

The second branch is unusual but intentional workbook behavior.

For each outside-process row:

```text
process unit cost = setup cost / q + run cost each
```

For the quantity roll-up:

```text
basic labor      = sum of production operation unit costs
labor burden     = basic labor × annual burden rate
burdened labor   = basic labor + labor burden
raw material     = sum of material unit costs
raw process      = sum of outside-process unit costs
pre-G&A subtotal = burdened labor + raw material + raw process
```

Labor, material, and process are loaded independently:

```text
component G&A    = raw component × component G&A rate
component profit = (raw component + component G&A) × component profit rate
loaded component = raw component + component G&A + component profit
component subtotal = loaded labor + loaded material + loaded process
```

Final price:

```text
yield adjustment = pre-G&A subtotal × (1 - yield)
sales markup     = component subtotal × sales markup rate

sell price =
  component subtotal
  + amortized NRE
  + yield adjustment
  + facilities
  + sales markup

extended value = q × sell price

gross margin =
  (sell price - pre-G&A subtotal - amortized NRE - yield adjustment)
  / (sell price - amortized NRE)

material percent of price = raw material / sell price
```

Division by zero is represented as unavailable in the web UI rather than exposing Excel's
`#DIV/0!`, JavaScript `NaN`, or infinity.

Rubber difficulty and cavity count are retained as quote metadata because the workbook exposes
them, but neither field has downstream formula dependencies.

## Rate-source mapping

The quote workbook used a mapped-drive external link to `Estimating Rates.xlsx`. The deployed
dashboard retains the reviewed rate values but does not retain or expose the source workbook's
user-folder path.

The active annual values come from `Sheet1`, with 2023–2029 in columns P:V. Columns N:O contain
legacy 2020 and 2022 values and are not valid calculator choices. The workbook-defined external
range is `Rates2020 = Sheet1!$M$5:$Z$75`; the supplied local workbook and all 710 cached external
link cells were verified to agree.

The left-side compact tables are not used as the calculator authority. Annual operation rates,
burden, G&A, and profit come from the year-selected matrix.

## Regression expectations

Automated tests cover:

- exact annual rate lookup and first-match duplicate behavior;
- both material allocation branches;
- production setup/run allocation;
- outside-process allocation;
- Standard and Rubber NRE loading;
- conditional Rubber tooling markup;
- year switching;
- zero-value ratio safety;
- Rubber metadata non-effects; and
- a complete Standard fixture across all eight quantity tiers.

Any intentional business-rule change should update the implementation, this contract, and the
golden fixture in the same change.
