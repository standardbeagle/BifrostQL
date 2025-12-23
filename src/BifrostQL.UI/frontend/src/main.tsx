import React from 'react';
import ReactDOM from 'react-dom/client';
import Editor from '@standardbeagle/edit-db';
import './app.css';

function App() {
  // The GraphQL endpoint is served by the same origin
  const graphqlUri = `${window.location.origin}/graphql`;

  return (
    <div className="app-container">
      <Editor
        uri={graphqlUri}
        onLocate={(location) => {
          // Update browser history for navigation
          window.history.pushState(null, '', location);
        }}
      />
    </div>
  );
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
