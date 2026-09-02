import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { initializeTheme } from './theme.ts'
import { clearTrainingProfile, type TrainingProfile } from './demo/training-profile.ts'
import { parsePageTour } from './demo/page-tours.ts'
import { installBennyIdle } from '../../../../../../shared/frontend/benny-idle.ts'
import './arda-shell.css'

initializeTheme()
installBennyIdle()

const searchParams = new URLSearchParams(window.location.search)
const trainingRequest = searchParams.get('training')
const requestedTour = parsePageTour(searchParams.get('tour'))
const developmentViewOnly = import.meta.env.DEV
  && import.meta.env.VITE_ENABLE_LEGACY_TRAINING_DEMO === 'true'
  && (trainingRequest === 'view-only' || searchParams.get('guideDemo') === '1')
const root = createRoot(document.getElementById('root')!)

type WalkthroughBootstrap = TrainingProfile & { enabled: boolean }

function renderApp() {
  if (trainingRequest || searchParams.has('guideDemo')) {
    clearTrainingProfile()
    searchParams.delete('training')
    searchParams.delete('guideDemo')
    searchParams.delete('tour')
    const cleanUrl = `${window.location.pathname}${searchParams.size ? `?${searchParams}` : ''}${window.location.hash}`
    window.history.replaceState(null, '', cleanUrl)
  }
  root.render(<StrictMode><App /></StrictMode>)
}

async function renderTraining(profile: TrainingProfile) {
  const { default: GuideDemo } = await import('./demo/GuideDemo.tsx')
  root.render(<StrictMode><GuideDemo profile={profile} initialTour={requestedTour} /></StrictMode>)
}

async function start() {
  if (developmentViewOnly) {
    await renderTraining({ displayName: 'Project Tracker Trainee', groups: [], permissions: ['module.view'] })
    return
  }

  if (trainingRequest !== 'current') {
    renderApp()
    return
  }

  try {
    const response = await fetch('/api/walkthrough/bootstrap', { credentials: 'same-origin' })
    if (!response.ok) throw new Error('Walkthrough bootstrap failed')
    const bootstrap = await response.json() as WalkthroughBootstrap
    if (!bootstrap.enabled || !bootstrap.permissions.some((permission) => permission.toLocaleLowerCase('en-US') === 'module.view')) {
      renderApp()
      return
    }
    await renderTraining({
      displayName: bootstrap.displayName,
      groups: bootstrap.groups,
      permissions: bootstrap.permissions,
      exitUrl: bootstrap.exitUrl,
    })
  } catch {
    renderApp()
  }
}

void start()
