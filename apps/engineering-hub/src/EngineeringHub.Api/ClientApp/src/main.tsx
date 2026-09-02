import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import './engineering-theme.css'
import App from './App.tsx'
import { initializeTheme } from './theme.ts'
import { installBennyIdle } from '../../../../../../shared/frontend/benny-idle.ts'
import './arda-shell.css'

initializeTheme()
installBennyIdle()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
