import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App, { hubUrl } from './App.tsx'
import './index.css'
import { initializeTheme } from './theme'
import { installBennyIdle } from '../../../../../../shared/frontend/benny-idle.ts'
import './arda-shell.css'
import { installArdaPresence } from '../../../../../../shared/frontend/arda-presence.ts'

initializeTheme()
installBennyIdle()
installArdaPresence({ moduleId: 'quality-assurance', portalBaseUrl: hubUrl })

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
