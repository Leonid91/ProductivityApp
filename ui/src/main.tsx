import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './styles.css'
import App from './App'
import { installActionLog, record } from './actionLog'

// Everything done in the window is written to the log from the first moment.
installActionLog()

// An error React could not recover from: written with its call stack and the components it happened in.
createRoot(document.getElementById('root')!, {
  onUncaughtError: (error, info) => record('react.uncaught', { error, componentStack: info.componentStack }, 'app', 'error'),
}).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
