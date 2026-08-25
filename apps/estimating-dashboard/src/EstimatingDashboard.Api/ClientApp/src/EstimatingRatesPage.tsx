import { useMemo, useState } from 'react'
import {
  BookOpenCheck,
  Database,
  History,
  LockKeyhole,
  RotateCcw,
  Search,
} from 'lucide-react'
import {
  ANNUAL_LABOR_RATES,
  ANNUAL_RATE_ASSUMPTIONS,
  RATE_EDIT_HISTORY,
} from './estimatingRates'
import { ESTIMATE_YEARS } from './types'
import type { EstimateYear, RateCategory } from './types'
import './rates.css'

type CategoryFilter = 'all' | RateCategory

const CATEGORY_OPTIONS: ReadonlyArray<{
  value: CategoryFilter
  label: string
}> = [
  { value: 'all', label: 'All' },
  { value: 'manufacturing', label: 'Manufacturing' },
  { value: 'rubber-breakdown', label: 'Rubber' },
]

const CATEGORY_LABELS: Record<RateCategory, string> = {
  manufacturing: 'Manufacturing',
  'rubber-breakdown': 'Rubber',
}

function formatPercent(value: number) {
  return `${(value * 100).toFixed(1)}%`
}

function formatRate(value: number) {
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })
}

function formatHourlyRate(value: number) {
  return (value * 60).toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })
}

function formatHistoryDate(value: string) {
  const [year, month, day] = value.split('-').map(Number)
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
    timeZone: 'UTC',
  }).format(new Date(Date.UTC(year, month - 1, day)))
}

export default function EstimatingRatesPage() {
  const [search, setSearch] = useState('')
  const [selectedYear, setSelectedYear] = useState<EstimateYear>(2026)
  const [category, setCategory] = useState<CategoryFilter>('all')

  const normalizedSearch = search.trim().toLocaleLowerCase()
  const filteredRows = useMemo(
    () => ANNUAL_LABOR_RATES.filter((row) => {
      const matchesCategory = category === 'all' || row.category === category
      const matchesSearch = !normalizedSearch
        || row.operation.toLocaleLowerCase().includes(normalizedSearch)
      return matchesCategory && matchesSearch
    }),
    [category, normalizedSearch],
  )

  const assumptions = ANNUAL_RATE_ASSUMPTIONS[selectedYear]
  const filtersActive = search.length > 0 || category !== 'all' || selectedYear !== 2026

  function clearFilters() {
    setSearch('')
    setCategory('all')
    setSelectedYear(2026)
  }

  return (
    <article className="rates-page" aria-labelledby="rates-reference-title">
      <section className="rates-intro" aria-labelledby="rates-reference-title">
        <div className="rates-intro-copy">
          <span className="rates-readonly-badge">
            <LockKeyhole size={14} aria-hidden="true" />
            Read-only reference
          </span>
          <h2 id="rates-reference-title">Annual Estimating Rate Matrix</h2>
          <p>
            Review workbook-aligned labor rates and pricing assumptions. Values on this page
            cannot be edited.
          </p>
        </div>

        <div className="rates-legend" aria-label="Workbook field legend">
          <span className="rates-legend-item rates-legend-input">
            <i aria-hidden="true" />Input
          </span>
          <span className="rates-legend-item rates-legend-calculated">
            <i aria-hidden="true" />Calculated
          </span>
          <span className="rates-legend-item rates-legend-reference">
            <i aria-hidden="true" />Reference
          </span>
          <small>Legend mirrors calculator field types; this page is entirely reference-only.</small>
        </div>
      </section>

      <section className="rates-controls" aria-label="Rate table filters">
        <div className="rates-search">
          <label htmlFor="rate-search">Search operations</label>
          <div className="rates-search-field">
            <Search size={17} aria-hidden="true" />
            <input
              id="rate-search"
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Search by operation"
              autoComplete="off"
            />
          </div>
        </div>

        <div className="rates-year-control">
          <label htmlFor="rate-year">Selected year</label>
          <select
            id="rate-year"
            value={selectedYear}
            onChange={(event) => setSelectedYear(Number(event.target.value) as EstimateYear)}
          >
            {ESTIMATE_YEARS.map((year) => (
              <option value={year} key={year}>{year}</option>
            ))}
          </select>
        </div>

        <fieldset className="rates-category-control">
          <legend>Category</legend>
          <div className="rates-segmented">
            {CATEGORY_OPTIONS.map((option) => (
              <button
                type="button"
                key={option.value}
                aria-pressed={category === option.value}
                onClick={() => setCategory(option.value)}
              >
                {option.label}
              </button>
            ))}
          </div>
        </fieldset>

        <button
          type="button"
          className="rates-clear-button"
          onClick={clearFilters}
          disabled={!filtersActive}
        >
          <RotateCcw size={16} aria-hidden="true" />
          Clear filters
        </button>
      </section>

      <section className="rates-summary" aria-label={`${selectedYear} rate assumptions`}>
        <div className="rates-summary-card rates-summary-year">
          <span>Selected year</span>
          <strong>{selectedYear}</strong>
          <small>{filteredRows.length} of {ANNUAL_LABOR_RATES.length} operations shown</small>
        </div>
        <div className="rates-summary-card">
          <span>Labor burden</span>
          <strong>{formatPercent(assumptions.burden)}</strong>
          <small>Annual burden assumption</small>
        </div>
        <div className="rates-summary-card">
          <span>G&amp;A</span>
          <strong>{formatPercent(assumptions.laborGa)}</strong>
          <small>Labor · Material · Process</small>
        </div>
        <div className="rates-summary-card">
          <span>Profit</span>
          <strong>{formatPercent(assumptions.laborProfit)}</strong>
          <small>Labor · Material · Process</small>
        </div>
      </section>

      <section className="rates-table-card" aria-labelledby="annual-rates-heading">
        <div className="rates-section-heading">
          <div>
            <span className="rates-section-icon"><BookOpenCheck size={18} aria-hidden="true" /></span>
            <div>
              <h3 id="annual-rates-heading">Labor Rates By Operation</h3>
              <p>USD per minute · hover or focus a value for its hourly equivalent</p>
            </div>
          </div>
          <span className="rates-result-count" aria-live="polite">
            {filteredRows.length} {filteredRows.length === 1 ? 'operation' : 'operations'}
          </span>
        </div>

        <div className="rates-table-scroll" tabIndex={0} aria-label="Scrollable annual labor rates">
          <table className="rates-table">
            <caption>
              Annual labor rates from 2023 through 2029 in United States dollars per minute.
              The {selectedYear} column is the selected year.
            </caption>
            <thead>
              <tr>
                <th className="rates-operation-column" scope="col">Operation</th>
                {ESTIMATE_YEARS.map((year) => {
                  const selected = year === selectedYear
                  return (
                    <th
                      key={year}
                      scope="col"
                      className={selected ? 'is-selected-year' : undefined}
                      aria-current={selected ? 'true' : undefined}
                    >
                      {year}
                      {selected && <span className="sr-only">, selected year</span>}
                    </th>
                  )
                })}
              </tr>
            </thead>
            <tbody>
              {filteredRows.map((row) => (
                <tr key={row.sourceRow} className={`is-${row.category}`}>
                  <th className="rates-operation-column" scope="row">
                    <span className="rates-operation-name">{row.operation}</span>
                    <span className="rates-category-badge">
                      {CATEGORY_LABELS[row.category]}
                    </span>
                  </th>
                  {ESTIMATE_YEARS.map((year) => {
                    const rate = row.rates[year]
                    const selected = year === selectedYear
                    const hourly = `${formatHourlyRate(rate)} per hour`
                    return (
                      <td
                        key={year}
                        className={selected ? 'is-selected-year' : undefined}
                        title={hourly}
                      >
                        <span>{formatRate(rate)}</span>
                        {selected && <small aria-hidden="true">{formatHourlyRate(rate)}/hr</small>}
                        <span className="sr-only"> per minute; {hourly}</span>
                      </td>
                    )
                  })}
                </tr>
              ))}
              {filteredRows.length === 0 && (
                <tr>
                  <td className="rates-empty-state" colSpan={ESTIMATE_YEARS.length + 1}>
                    <Search size={22} aria-hidden="true" />
                    <strong>No matching operations</strong>
                    <span>Adjust the search or category filter to see rate rows.</span>
                    <button type="button" onClick={clearFilters}>Clear filters</button>
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      <aside className="rates-provenance" aria-labelledby="rates-source-heading">
        <Database size={20} aria-hidden="true" />
        <div>
          <h3 id="rates-source-heading">Source Provenance</h3>
          <p>
            The authoritative 2023–2029 matrix is{' '}
            <code>Estimating Rates.xlsx · Sheet1!P5:V75</code>. The dashboard uses the
            reviewed values directly, without requiring the workbook&apos;s mapped-drive link.
          </p>
        </div>
      </aside>

      <details className="rates-history">
        <summary>
          <span className="rates-section-icon"><History size={18} aria-hidden="true" /></span>
          <span>
            <strong>Edit history</strong>
            <small>{RATE_EDIT_HISTORY.length} source workbook entries</small>
          </span>
        </summary>
        <ol className="rates-timeline">
          {[...RATE_EDIT_HISTORY].reverse().map((entry, index) => (
            <li key={`${entry.date}-${index}`}>
              <div className="rates-timeline-marker" aria-hidden="true" />
              <article>
                <header>
                  <div>
                    <strong>{entry.editor.trim()}</strong>
                    <time dateTime={entry.date}>{formatHistoryDate(entry.date)}</time>
                  </div>
                  <span>Approved by {entry.approver.trim()}</span>
                </header>
                <p>{entry.description}</p>
              </article>
            </li>
          ))}
        </ol>
      </details>
    </article>
  )
}
