// Minimal SPA logic: query the same-origin BifrostQL endpoint and render the result.

const output = document.getElementById('output');
const loadButton = document.getElementById('load');

const QUERY = `{
  widgets {
    data { id name color }
  }
}`;

async function loadWidgets() {
  output.textContent = 'Loading...';
  try {
    const response = await fetch('/graphql', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ query: QUERY }),
    });

    if (!response.ok) {
      output.textContent = `Request failed: HTTP ${response.status}`;
      return;
    }

    const payload = await response.json();
    if (payload.errors) {
      output.textContent = `GraphQL errors:\n${JSON.stringify(payload.errors, null, 2)}`;
      return;
    }

    output.textContent = JSON.stringify(payload.data.widgets.data, null, 2);
  } catch (error) {
    output.textContent = `Request error: ${error.message}`;
  }
}

loadButton.addEventListener('click', loadWidgets);
