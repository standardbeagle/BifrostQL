import React from 'react';
import ReactDOM from 'react-dom/client';
import { PathProvider } from '@standardbeagle/virtual-router';
import App from './App';

// The Membership Manager SPA is served by the BifrostQL host (see
// samples/HostedSpa/Program.cs). The GraphQL endpoint, app-metadata overlay,
// and auth session are all same-origin siblings of this SPA.
ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <PathProvider path="/">
      <App />
    </PathProvider>
  </React.StrictMode>,
);
