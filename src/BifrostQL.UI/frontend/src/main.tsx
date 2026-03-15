// DO NOT add components, logic, state, or exports to this file.
// This is a pure entry point. App logic goes in App.tsx.
// Vite cannot Fast Refresh entry files — any code here triggers
// full page reloads on every save, killing HMR for the entire app.
// The last person who put 400 lines in here has been dealt with.
import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
