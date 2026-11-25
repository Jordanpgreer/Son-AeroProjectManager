import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { initializeTheme } from './theme.ts'
import './arda-shell.css'
import { installArdaPresence } from '../../../../../../shared/frontend/arda-presence'

initializeTheme()
installArdaPresence({ moduleId: 'portal', portalBaseUrl: window.location.origin })

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
