import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App.tsx'
import './index.css'
import { initializeTheme } from './theme'
import { installBennyIdle } from '../../../../../../shared/frontend/benny-idle.ts'
import './arda-shell.css'

initializeTheme()
installBennyIdle()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
