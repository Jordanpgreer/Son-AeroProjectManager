import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App, { hubUrl } from './App.tsx'
import { initializeTheme } from './theme.ts'
import { installBennyIdle } from '../../../../../../shared/frontend/benny-idle.ts'
import './arda-shell.css'
import { installArdaPresence } from '../../../../../../shared/frontend/arda-presence.ts'

initializeTheme()
installBennyIdle()
installArdaPresence({ moduleId: 'estimating-dashboard', portalBaseUrl: hubUrl })

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
