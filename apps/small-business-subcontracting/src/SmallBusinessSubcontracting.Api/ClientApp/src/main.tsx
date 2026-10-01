import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@fontsource-variable/inter'
import '@fontsource/ibm-plex-mono/400.css'
import App, { hubUrl } from './App.tsx'
import './index.css'
import { applyTheme, readThemePreference } from './theme'
import { installArdaPresence } from '../../../../../../shared/frontend/arda-presence.ts'

applyTheme(readThemePreference())
installArdaPresence({ moduleId: 'small-business-subcontracting', portalBaseUrl: hubUrl })

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
