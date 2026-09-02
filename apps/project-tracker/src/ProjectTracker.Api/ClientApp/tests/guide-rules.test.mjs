import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import test from 'node:test'
import { fileURLToPath } from 'node:url'
import { BLOUB_ANIMATIONS } from '../src/demo/bloub-animations.ts'
import { allProjectTrackerPermissionKeys, permissionKeys } from '../src/permissions.ts'
import { normalizeGuideQuery, resolveGuideIntent } from '../src/demo/guide-rules.ts'
import {
  BENNY_INTENTS,
  availableBennyIntents,
  normalizeBennyQuery,
  resolveBennyQuery,
} from '../src/demo/benny-rules.ts'
import {
  TRAINING_NOTIFICATIONS,
  TRAINING_PROJECT_DETAILS,
  VIEW_ONLY_TRAINING_USER,
} from '../src/demo/training-fixtures.ts'
import {
  VIEW_ONLY_PERMISSIONS,
  VIEW_ONLY_TRAINING_STEPS,
  eligibleTrainingTourSteps,
  eligibleTrainingTours,
  eligibleTrainingSteps,
  expandAndClampRect,
  placeTrainingCard,
} from '../src/demo/training-model.ts'
import { TRAINING_PERMISSION_COVERAGE } from '../src/demo/training-permissions.ts'
import { clearTrainingProfile, readTrainingProfile, saveTrainingProfile } from '../src/demo/training-profile.ts'
import { pageTourPromptKey, pageTourUrl, parsePageTour } from '../src/demo/page-tours.ts'

test('normalizes punctuation and repeated whitespace', () => {
  assert.equal(normalizeGuideQuery('  Where is:  Work Center Load? '), 'where is work center load')
})

test('matches approved phrases deterministically', () => {
  assert.equal(resolveGuideIntent('How do I add an operation?')?.id, 'add-operation')
  assert.equal(resolveGuideIntent('How do I add an operation?')?.animation, 'play')
  assert.equal(resolveGuideIntent('My change did not save')?.id, 'save-failed')
  assert.equal(resolveGuideIntent('My change did not save')?.animation, 'exclaim')
  assert.equal(resolveGuideIntent('Show my finish confirmation')?.id, 'notifications')
  assert.equal(resolveGuideIntent('Show my finish confirmation')?.animation, 'notify')
})

test('does not invent an answer for an unknown topic', () => {
  assert.equal(resolveGuideIntent('Tell me tomorrow\'s winning lottery numbers'), null)
})

test('normalizes Benny queries locally and deterministically', () => {
  assert.equal(normalizeBennyQuery('  Open: DEMO-1001?!  '), 'open demo 1001')
})

test('filters Benny intents before matching or suggesting them', () => {
  const viewerIntents = availableBennyIntents([permissionKeys.moduleView])
  assert.equal(viewerIntents.some((intent) => intent.id === 'add-operation'), false)
  assert.equal(viewerIntents.some((intent) => intent.id === 'project-activity'), false)

  const result = resolveBennyQuery('How do I add an operation?', {
    permissions: [permissionKeys.moduleView],
  })
  assert.equal(result.status, 'no-match')
  assert.ok(result.suggestions.every((suggestion) => suggestion.intentId !== 'add-operation'))
})

test('matches permission-backed Benny actions when access is granted', () => {
  const result = resolveBennyQuery('How do I add an operation?', {
    permissions: [permissionKeys.moduleView, permissionKeys.taskCreate],
  })
  assert.equal(result.status, 'matched')
  assert.equal(result.match.intentId, 'add-operation')
  assert.deepEqual(result.match.command, { kind: 'focus-ui', targetId: 'add-operation', screen: 'project' })
})

test('shows all behind-schedule projects for the exact natural-language question', () => {
  const result = resolveBennyQuery('What projects are behind schedule?', {
    permissions: [permissionKeys.moduleView],
  })

  assert.equal(result.status, 'matched')
  assert.equal(result.match.intentId, 'behind-projects')
  assert.deepEqual(result.match.command, { kind: 'filter', screen: 'dashboard', filter: 'behind' })
})

test('recognizes common plural variants for behind-schedule projects', () => {
  for (const query of [
    'Which projects are running behind?',
    'Show me the delayed projects',
    'Are there any overdue projects?',
  ]) {
    const result = resolveBennyQuery(query, {
      permissions: [permissionKeys.moduleView],
    })

    assert.equal(result.status, 'matched', query)
    assert.equal(result.match.intentId, 'behind-projects', query)
    assert.deepEqual(result.match.command, { kind: 'filter', screen: 'dashboard', filter: 'behind' }, query)
  }
})

test('supports any-of permission rules for operation schedule help', () => {
  const denied = resolveBennyQuery('change start date', {
    permissions: [permissionKeys.moduleView],
  })
  assert.equal(denied.status, 'no-match')

  const allowed = resolveBennyQuery('change start date', {
    permissions: [permissionKeys.moduleView, permissionKeys.taskEditStartDate],
  })
  assert.equal(allowed.status, 'matched')
  assert.equal(allowed.match.intentId, 'operation-schedule')
})

test('returns only safe typed commands from the Benny catalog', () => {
  const safeKinds = new Set(['screen', 'filter', 'open-project', 'focus-operation', 'open-gantt', 'focus-ui', 'answer'])
  assert.ok(BENNY_INTENTS.every((intent) => safeKinds.has(intent.command.kind)))
  assert.ok(BENNY_INTENTS.every((intent) => !JSON.stringify(intent.command).match(/delete|post|put|patch|url/i)))
})

test('opens an exact project entity using caller-supplied local context', () => {
  const result = resolveBennyQuery('Open DEMO-1001', {
    permissions: [permissionKeys.moduleView],
    currentScreen: 'dashboard',
    projects: [
      { id: 9001, programName: 'DEMO-1001', customerName: 'Acme', salesOrderNumber: 'TRAIN-1001' },
      { id: 9002, programName: 'DEMO-1002', customerName: 'Acme', salesOrderNumber: 'TRAIN-1002' },
    ],
  })
  assert.equal(result.status, 'matched')
  assert.deepEqual(result.match.command, { kind: 'open-project', projectId: 9001 })
})

test('opens the requested project Gantt instead of stopping at project detail', () => {
  const result = resolveBennyQuery('Show the Gantt for DEMO-1001', {
    permissions: [permissionKeys.moduleView],
    projects: [{ id: 9001, programName: 'DEMO-1001' }],
  })
  assert.equal(result.status, 'matched')
  assert.deepEqual(result.match.command, { kind: 'open-gantt', projectId: 9001 })
})

test('filters multiple projects when the matched entity is explicitly plural', () => {
  const result = resolveBennyQuery('Show Acme projects', {
    permissions: [permissionKeys.moduleView],
    projects: [
      { id: 9001, programName: 'DEMO-1001', customerName: 'Acme' },
      { id: 9002, programName: 'DEMO-1002', customerName: 'Acme' },
    ],
  })
  assert.equal(result.status, 'matched')
  assert.deepEqual(result.match.command, { kind: 'filter', screen: 'dashboard', filter: 'query', value: 'Acme' })
})

test('focuses an operation from selected-project context without inventing a write action', () => {
  const selectedProject = {
    id: 9001,
    programName: 'DEMO-1001',
    operations: [{ id: 9103, title: 'CNC Machining' }],
  }
  const result = resolveBennyQuery('Show CNC Machining', {
    permissions: [permissionKeys.moduleView],
    selectedProject,
    projects: [selectedProject],
    currentScreen: 'project',
  })
  assert.equal(result.status, 'matched')
  assert.deepEqual(result.match.command, { kind: 'focus-operation', projectId: 9001, operationId: 9103 })
})

test('returns deterministic ambiguity and permission-safe no-match suggestions', () => {
  const ambiguous = resolveBennyQuery('project schedule', {
    permissions: [permissionKeys.moduleView],
  })
  assert.equal(ambiguous.status, 'ambiguous')
  assert.deepEqual(ambiguous.matches.map((match) => match.intentId), ['calendar', 'gantt'])

  const unknown = resolveBennyQuery('Tell me tomorrow\'s winning lottery numbers', {
    permissions: [permissionKeys.moduleView],
  })
  assert.equal(unknown.status, 'no-match')
  assert.deepEqual(unknown.suggestions.map((suggestion) => suggestion.intentId), [
    'find-project',
    'dashboard',
    'calendar',
    'behind-projects',
  ])
})

test('returns no-match instead of inventing an answer for an unknown Benny question', () => {
  const result = resolveBennyQuery('Which supplier should I call about material shortages?', {
    permissions: [permissionKeys.moduleView],
  })

  assert.equal(result.status, 'no-match')
  assert.ok(result.suggestions.length > 0)
  assert.ok(result.suggestions.every((suggestion) => suggestion.command.kind !== 'answer'))
})

test('returns no matches or suggestions when the assistant feature is disabled', () => {
  assert.deepEqual(resolveBennyQuery('dashboard', {
    assistantEnabled: false,
    assistantName: 'Benny',
    permissions: [permissionKeys.moduleView],
  }), { status: 'no-match', suggestions: [] })
})

test('defines all 14 unique animation segments with the complete cycle duration', () => {
  assert.equal(BLOUB_ANIMATIONS.length, 14)
  assert.equal(new Set(BLOUB_ANIMATIONS.map((animation) => animation.id)).size, 14)
  assert.equal(BLOUB_ANIMATIONS.reduce((total, animation) => total + animation.duration, 0), 31.2)
})

test('has a local GIF asset for every defined animation', () => {
  const assetRoot = new URL('../public/prototypes/bloub-states/', import.meta.url)
  for (const animation of BLOUB_ANIMATIONS) {
    assert.equal(existsSync(fileURLToPath(new URL(`${animation.id}.gif`, assetRoot))), true, animation.id)
  }
})

test('builds four independent page tours from effective view access', () => {
  const tours = eligibleTrainingTours(VIEW_ONLY_PERMISSIONS)
  assert.deepEqual(tours.map((tour) => tour.id), ['dashboard', 'project', 'calendar', 'pastProjects'])
  assert.deepEqual(eligibleTrainingSteps(VIEW_ONLY_PERMISSIONS), tours.flatMap((tour) => tour.steps))
  assert.deepEqual(VIEW_ONLY_TRAINING_STEPS, tours.flatMap((tour) => tour.steps))
  assert.equal(eligibleTrainingTours([]).length, 0)
  assert.equal(eligibleTrainingTours(['PROJECT.CREATE']).length, 0)
})

test('maps every Project Tracker access checkbox to a walkthrough capability', () => {
  assert.equal(allProjectTrackerPermissionKeys.length, 41)
  assert.deepEqual(
    Object.keys(TRAINING_PERMISSION_COVERAGE).sort(),
    [...allProjectTrackerPermissionKeys].sort(),
  )
  const currentCoverage = new Set([
    'page-tours',
    'page-tour-static',
    'project-edit-overview',
    'past-actions',
    'admin-page-not-in-scope',
  ])
  assert.ok(Object.values(TRAINING_PERMISSION_COVERAGE).every((lesson) => currentCoverage.has(lesson)))
})

test('parses only supported page tours and gives each prompt its own session key', () => {
  const screens = ['dashboard', 'project', 'calendar', 'pastProjects']
  assert.deepEqual(screens.map((screen) => parsePageTour(screen)), screens)
  assert.equal(parsePageTour('notifications'), null)
  assert.equal(parsePageTour(''), null)
  assert.equal(parsePageTour(null), null)
  assert.equal(new Set(screens.map((screen) => pageTourPromptKey(screen))).size, screens.length)
})

test('builds a page-scoped tour URL without discarding unrelated query or hash state', () => {
  const result = new URL(pageTourUrl(
    'http://localhost:5135/?guideDemo=1&existing=value#project-detail',
    'project',
  ))
  assert.equal(result.searchParams.get('training'), 'current')
  assert.equal(result.searchParams.get('tour'), 'project')
  assert.equal(result.searchParams.get('existing'), 'value')
  assert.equal(result.searchParams.has('guideDemo'), false)
  assert.equal(result.hash, '#project-detail')
})

test('keeps every tour page-isolated, short, and free of typed action gates', () => {
  for (const permissions of [VIEW_ONLY_PERMISSIONS, allProjectTrackerPermissionKeys]) {
    for (const tour of eligibleTrainingTours(permissions)) {
      assert.ok(tour.steps.length > 0, tour.id)
      assert.ok(tour.steps.length <= 6, `${tour.id} has ${tour.steps.length} steps`)
      assert.ok(tour.steps.every((step) => step.screen === tour.id), `${tour.id} crosses into another page`)
      assert.ok(tour.steps.every((step) => ['next', 'click'].includes(step.advance)), `${tour.id} has a typed action gate`)
      assert.ok(tour.steps.every((step) => !/(^|-)nav(?:igation)?(-|$)/i.test(step.id)), `${tour.id} contains a navigation lesson`)
      assert.ok(tour.steps.every((step) => !step.targetId?.startsWith('nav-')), `${tour.id} targets another page's navigation`)
    }
  }
})

test('keeps dashboard training focused on My Projects, live search highlighting, and export', () => {
  const steps = eligibleTrainingTourSteps('dashboard', VIEW_ONLY_PERMISSIONS)
  const ids = new Set(steps.map((step) => step.id))

  assert.ok(steps.length >= 4 && steps.length <= 6)
  for (const id of ['dashboard-my-projects', 'dashboard-search', 'dashboard-export-open', 'dashboard-export-options']) {
    assert.equal(ids.has(id), true, id)
  }
  const searchCopy = steps
    .filter((step) => step.id === 'dashboard-search' || step.id === 'dashboard-results')
    .flatMap((step) => [step.title, step.body])
    .join(' ')
  assert.match(searchCopy, /live|as you type/i)
  assert.match(searchCopy, /filter/i)
  assert.match(searchCopy, /highlight/i)
})

test('teaches the project editor only when effective edit access exists', () => {
  const viewerSteps = eligibleTrainingTourSteps('project', VIEW_ONLY_PERMISSIONS)
  const viewerIds = new Set(viewerSteps.map((step) => step.id))
  for (const id of ['project-overview', 'project-operations', 'project-export-options']) {
    assert.equal(viewerIds.has(id), true, id)
  }
  assert.ok([...viewerIds].every((id) => !id.startsWith('project-edit-')))
  assert.doesNotMatch(
    viewerSteps.flatMap((step) => [step.eyebrow, step.title, step.body]).join(' '),
    /view[- ]only|edit(?:ing)? access|permission|unavailable|hidden control/i,
  )

  for (const editPermission of [permissionKeys.projectEditCustomerName, permissionKeys.taskCreate, permissionKeys.taskEditNotes]) {
    const steps = eligibleTrainingTourSteps('project', [permissionKeys.moduleView, editPermission])
    const ids = steps.map((step) => step.id)
    assert.ok(ids.includes('project-edit-open'), editPermission)
    assert.ok(ids.includes('project-edit-overview'), editPermission)
    assert.ok(ids.indexOf('project-edit-open') < ids.indexOf('project-edit-overview'), editPermission)
    assert.ok(ids.indexOf('project-edit-overview') < ids.indexOf('project-export-options'), editPermission)
    assert.ok(steps.length >= 4 && steps.length <= 6, editPermission)
  }

  const adminOnlyIds = eligibleTrainingTourSteps('project', [
    permissionKeys.moduleView,
    permissionKeys.settingsWorkCalendarManage,
  ]).map((step) => step.id)
  assert.ok(adminOnlyIds.every((id) => !id.startsWith('project-edit-')))
})

test('does not broaden the project edit lesson beyond the granted capability group', () => {
  const projectOnly = eligibleTrainingTourSteps('project', [
    permissionKeys.moduleView,
    permissionKeys.projectEditCustomerName,
  ]).find((step) => step.id === 'project-edit-overview')
  assert.match(projectOnly.body, /project details/i)
  assert.doesNotMatch(projectOnly.body, /operation fields|operation list/i)

  const operationFieldOnly = eligibleTrainingTourSteps('project', [
    permissionKeys.moduleView,
    permissionKeys.taskEditNotes,
  ]).find((step) => step.id === 'project-edit-overview')
  assert.match(operationFieldOnly.body, /operation fields/i)
  assert.doesNotMatch(operationFieldOnly.body, /project details|operation list/i)

  const operationListOnly = eligibleTrainingTourSteps('project', [
    permissionKeys.moduleView,
    permissionKeys.taskCreate,
  ]).find((step) => step.id === 'project-edit-overview')
  assert.match(operationListOnly.body, /operation list/i)
  assert.doesNotMatch(operationListOnly.body, /project details|operation fields/i)

  const editorPermissions = new Set([
    permissionKeys.taskCreate,
    permissionKeys.taskDelete,
    permissionKeys.projectEditProgramName,
    permissionKeys.projectEditProgramManager,
    permissionKeys.projectEditEngineer,
    permissionKeys.projectEditCustomerName,
    permissionKeys.projectEditSalesOrderNumber,
    permissionKeys.projectEditJobNumber,
    permissionKeys.projectEditQuantities,
    permissionKeys.projectEditExternalLinks,
    permissionKeys.taskEditTitle,
    permissionKeys.taskEditWorkStation,
    permissionKeys.taskEditDependency,
    permissionKeys.taskEditStartDateLocked,
    permissionKeys.taskEditStartDate,
    permissionKeys.taskEditEndDate,
    permissionKeys.taskEditOriginalStartDate,
    permissionKeys.taskEditOriginalEndDate,
    permissionKeys.taskEditEstimatedDuration,
    permissionKeys.taskEditActualDuration,
    permissionKeys.taskEditPercentComplete,
    permissionKeys.taskEditNotes,
    permissionKeys.taskEditOvertimeDays,
    permissionKeys.taskReorder,
  ])
  for (const permission of allProjectTrackerPermissionKeys) {
    if (permission === permissionKeys.moduleView) continue
    const hasEditLesson = eligibleTrainingTourSteps('project', [permissionKeys.moduleView, permission])
      .some((step) => step.id === 'project-edit-overview')
    assert.equal(hasEditLesson, editorPermissions.has(permission), permission)
  }
})

test('teaches Past Projects actions only when a history action is granted', () => {
  const historyActions = new Set([
    permissionKeys.projectReopen,
    permissionKeys.archivedRestore,
    permissionKeys.archivedDelete,
  ])
  for (const permission of allProjectTrackerPermissionKeys) {
    if (permission === permissionKeys.moduleView) continue
    const hasHistoryLesson = eligibleTrainingTourSteps('pastProjects', [permissionKeys.moduleView, permission])
      .some((step) => step.id === 'past-actions')
    assert.equal(hasHistoryLesson, historyActions.has(permission), permission)
  }
})

test('keeps Calendar and Past Projects intentionally concise', () => {
  const calendar = eligibleTrainingTourSteps('calendar', allProjectTrackerPermissionKeys)
  const pastProjects = eligibleTrainingTourSteps('pastProjects', allProjectTrackerPermissionKeys)
  assert.ok(calendar.length > 0 && calendar.length <= 3)
  assert.ok(pastProjects.length > 0 && pastProjects.length <= 4)
})

test('keeps the permission snapshot through refresh until training explicitly clears it', () => {
  const values = new Map()
  globalThis.window = {
    sessionStorage: {
      getItem: (key) => values.get(key) ?? null,
      setItem: (key, value) => values.set(key, value),
      removeItem: (key) => values.delete(key),
    },
  }
  saveTrainingProfile(VIEW_ONLY_TRAINING_USER)
  assert.deepEqual(readTrainingProfile()?.permissions, [permissionKeys.moduleView])
  assert.deepEqual(readTrainingProfile()?.permissions, [permissionKeys.moduleView])
  clearTrainingProfile()
  assert.equal(readTrainingProfile(), null)
  delete globalThis.window
})

test('keeps page-tour copy concise', () => {
  assert.ok(VIEW_ONLY_TRAINING_STEPS.every((step) => step.body.length <= 85))
  assert.ok(VIEW_ONLY_TRAINING_STEPS.every((step) => (step.body.match(/[.!?](?:\s|$)/g) ?? []).length === 1))
  const allSteps = eligibleTrainingSteps(allProjectTrackerPermissionKeys)
  assert.ok(allSteps.every((step) => step.body.length <= 85))
  assert.ok(allSteps.every((step) => (step.body.match(/[.!?](?:\s|$)/g) ?? []).length === 1))
})

test('never teaches unavailable access in viewer-facing walkthrough copy', () => {
  const copy = VIEW_ONLY_TRAINING_STEPS
    .flatMap((step) => [step.eyebrow, step.title, step.body])
    .join(' ')
  assert.doesNotMatch(copy, /view[- ]only|edit access|editing controls|remain hidden|unavailable|permission/i)
})

test('gives every page-tour lesson a stable highlight target', () => {
  assert.ok(VIEW_ONLY_TRAINING_STEPS.every((step) => step.targetId))
})

test('keeps the in-memory training portfolio fictional and view only', () => {
  assert.deepEqual(VIEW_ONLY_TRAINING_USER.permissions, ['module.view'])
  assert.equal(VIEW_ONLY_TRAINING_USER.canEdit, false)
  assert.equal(VIEW_ONLY_TRAINING_USER.isRegistered, false)
  assert.ok(TRAINING_PROJECT_DETAILS.every((project) => project.id >= 8_000))
  assert.ok(TRAINING_PROJECT_DETAILS.every((project) => project.programName.startsWith('DEMO-')))
  assert.ok(TRAINING_PROJECT_DETAILS.every((project) => project.salesOrderNumber?.startsWith('TRAIN-')))
  assert.ok(TRAINING_PROJECT_DETAILS.every((project) => project.salesOrderUrl === null && project.jobUrl === null))
  assert.equal(TRAINING_NOTIFICATIONS.length, 2)
  assert.ok(TRAINING_NOTIFICATIONS.every((notification) => notification.projectId >= 9_000))
  assert.equal(new Set(TRAINING_NOTIFICATIONS.map((notification) => notification.id)).size, TRAINING_NOTIFICATIONS.length)
})

test('expands the highlight exactly and clamps it to the viewport', () => {
  assert.deepEqual(
    expandAndClampRect({ left: 100, top: 80, width: 200, height: 40 }, { width: 1280, height: 720 }),
    { left: 93, top: 73, width: 214, height: 54 },
  )
  assert.deepEqual(
    expandAndClampRect({ left: 1, top: 2, width: 40, height: 20 }, { width: 100, height: 80 }),
    { left: 4, top: 4, width: 44, height: 25 },
  )
})

test('places the guide card beside the target without viewport overflow', () => {
  const viewport = { width: 1280, height: 720 }
  const card = placeTrainingCard({ left: 200, top: 180, width: 300, height: 100 }, viewport)
  assert.equal(card.placement, 'right')
  assert.equal(card.left, 512)
  assert.ok(card.top >= 16)
  const centered = placeTrainingCard(null, viewport)
  assert.equal(centered.placement, 'center')
})

test('isolated training source contains no Project Tracker API route', () => {
  const demoRoot = new URL('../src/demo/', import.meta.url)
  for (const file of ['GuideDemo.tsx', 'TrainingWorkspace.tsx', 'TrainingSpotlight.tsx', 'training-model.ts', 'training-fixtures.ts', 'training-profile.ts']) {
    const source = readFileSync(fileURLToPath(new URL(file, demoRoot)), 'utf8')
    assert.equal(source.includes('/api/'), false, file)
  }
})

test('validates production training against the server and gates the fixed viewer route', () => {
  const source = readFileSync(fileURLToPath(new URL('../src/main.tsx', import.meta.url)), 'utf8')
  assert.equal(source.includes("trainingRequest !== 'current'"), true)
  assert.equal(source.includes('readTrainingProfile'), false)
  assert.equal(source.includes("trainingRequest === 'view-only'"), true)
  assert.equal(source.includes('import.meta.env.DEV'), true)
  assert.equal(source.includes("VITE_ENABLE_LEGACY_TRAINING_DEMO === 'true'"), true)
  assert.equal(source.includes("fetch('/api/walkthrough/bootstrap'"), true)
})
