export const ganttZoomLevels = [
  { label: '25%', dayWidth: 6.5 },
  { label: '50%', dayWidth: 13 },
  { label: '75%', dayWidth: 20 },
  { label: '100%', dayWidth: 26 },
  { label: '125%', dayWidth: 34 },
]

export const defaultGanttZoomIndex = 3

export function getGuidedGanttScrollLeft({
  scrollLeft,
  maxScrollLeft,
  viewportWidth,
  labelWidth,
  barStart,
  barEnd,
  alignStart = false,
  margin = 20,
}: {
  scrollLeft: number
  maxScrollLeft: number
  viewportWidth: number
  labelWidth: number
  barStart: number
  barEnd: number
  alignStart?: boolean
  margin?: number
}) {
  const timelineViewportWidth = Math.max(1, viewportWidth - labelWidth)
  const safeMargin = Math.min(margin, timelineViewportWidth / 4)
  const visibleStart = scrollLeft + safeMargin
  const visibleEnd = scrollLeft + timelineViewportWidth - safeMargin
  const start = Math.min(barStart, barEnd)
  const end = Math.max(barStart, barEnd)
  const visibleWidth = Math.max(1, visibleEnd - visibleStart)
  const barWidth = Math.max(1, end - start)
  const overlap = Math.max(0, Math.min(end, visibleEnd) - Math.max(start, visibleStart))
  const meaningfulOverlap = Math.min(64, visibleWidth * 0.35, barWidth)

  // At the top of a project, show where the first operation actually begins.
  // This also corrects a horizontal position carried over from another project.
  if (alignStart) {
    return Math.max(0, Math.min(maxScrollLeft, start - safeMargin))
  }

  // A long operation can span beyond both sides of the viewport. If a useful
  // portion is already visible, keep the user's date position stable.
  if (barWidth > visibleWidth && overlap >= meaningfulOverlap) return scrollLeft

  let nextScrollLeft = scrollLeft
  if (start < visibleStart) {
    nextScrollLeft = start - safeMargin
  } else if (end > visibleEnd) {
    nextScrollLeft = end - timelineViewportWidth + safeMargin
  }

  return Math.max(0, Math.min(maxScrollLeft, nextScrollLeft))
}
