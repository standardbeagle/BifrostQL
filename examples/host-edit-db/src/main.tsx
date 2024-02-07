import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App.tsx'
import './index.css'
import { PathProvider } from '@standardbeagle/virtual-router'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <PathProvider path="/">
      <App />
    </PathProvider>
  </React.StrictMode>,
)
