namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Embedded HTML document for the credential prompt child window.
    ///
    /// <para>
    /// The whole document is shipped as a single verbatim string and loaded
    /// into the child <c>PhotinoWindow</c> via <c>LoadRawString</c>. There
    /// is no HTTP server, no local file on disk, and no external resource
    /// fetch — the Content Security Policy in the document enforces that
    /// even if the HTML somehow ended up pointing at a remote URL, the
    /// WebView would refuse to load it.
    /// </para>
    ///
    /// <para>
    /// <b>Security controls baked into the document:</b>
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     CSP: <c>default-src 'none'</c> (no default fetches),
    ///     <c>style-src 'unsafe-inline'</c> (inline CSS only),
    ///     <c>script-src 'unsafe-inline'</c> (inline JS only),
    ///     <c>form-action 'none'</c> (form cannot POST anywhere —
    ///     credentials only leave via <c>window.external.sendMessage</c>).
    ///   </description></item>
    ///   <item><description>
    ///     Password input uses <c>autocomplete="new-password"</c> so the
    ///     WebView's credential manager does not prompt to save.
    ///   </description></item>
    ///   <item><description>
    ///     Inline keydown handler swallows <c>F12</c> and <c>Ctrl+Shift+I</c>
    ///     as belt-and-braces. The real gate is <c>SetDevToolsEnabled(false)</c>
    ///     on the host <c>PhotinoWindow</c>; this just makes casual attempts
    ///     visibly inert to the user.
    ///   </description></item>
    ///   <item><description>
    ///     On Save / Cancel, the inline JS zeroes the input values before
    ///     returning so the plaintext cannot linger in the DOM heap any
    ///     longer than strictly required.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// The document is a C# verbatim string literal (<c>@"..."</c>). All
    /// inner double quotes are escaped by doubling. There is no
    /// interpolation (<c>$</c> is deliberately absent) so the string is a
    /// constant and every build ships byte-identical HTML.
    /// </para>
    /// </summary>
    public static class CredentialPromptHtml
    {
        /// <summary>
        /// The full HTML document. Exposed as <c>const string</c> so it can
        /// be referenced from attributes or baked into other constants if
        /// ever needed.
        /// </summary>
        public const string Html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"">
  <meta http-equiv=""Content-Security-Policy"" content=""default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; form-action 'none';"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>Enter credentials</title>
  <style>
    html, body {
      margin: 0;
      padding: 0;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      background: #f5f5f7;
      color: #1d1d1f;
      height: 100%;
    }
    body {
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 20px;
      box-sizing: border-box;
    }
    .card {
      width: 380px;
      background: #ffffff;
      border: 1px solid #d2d2d7;
      border-radius: 10px;
      padding: 22px 24px;
      box-shadow: 0 6px 24px rgba(0, 0, 0, 0.08);
    }
    h1 {
      margin: 0 0 4px 0;
      font-size: 17px;
      font-weight: 600;
    }
    #subtitle {
      margin: 0 0 16px 0;
      font-size: 12px;
      color: #6e6e73;
    }
    #vault-name {
      font-weight: 600;
      color: #1d1d1f;
    }
    label {
      display: block;
      font-size: 12px;
      font-weight: 500;
      margin-bottom: 4px;
      color: #1d1d1f;
    }
    input[type=""text""], input[type=""password""] {
      width: 100%;
      box-sizing: border-box;
      padding: 8px 10px;
      font-size: 13px;
      border: 1px solid #d2d2d7;
      border-radius: 6px;
      margin-bottom: 12px;
      background: #ffffff;
      color: #1d1d1f;
      outline: none;
    }
    input[type=""text""]:focus, input[type=""password""]:focus {
      border-color: #0071e3;
      box-shadow: 0 0 0 3px rgba(0, 113, 227, 0.15);
    }
    .buttons {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      margin-top: 8px;
    }
    button {
      padding: 7px 16px;
      font-size: 13px;
      border-radius: 6px;
      border: 1px solid #d2d2d7;
      background: #ffffff;
      color: #1d1d1f;
      cursor: pointer;
    }
    button#save {
      background: #0071e3;
      border-color: #0071e3;
      color: #ffffff;
    }
    button#save:hover { background: #0077ed; }
    button#cancel:hover { background: #f5f5f7; }
    #error {
      display: none;
      margin-top: 8px;
      font-size: 12px;
      color: #d70015;
    }
  </style>
</head>
<body>
  <div class=""card"" role=""dialog"" aria-labelledby=""title"">
    <h1 id=""title"">Enter credentials</h1>
    <div id=""subtitle"">Vault entry: <span id=""vault-name"">(loading)</span></div>

    <label for=""username"">Username</label>
    <input id=""username"" type=""text"" autocomplete=""off"" autocorrect=""off"" autocapitalize=""off"" spellcheck=""false"">

    <label for=""password"">Password</label>
    <input id=""password"" type=""password"" autocomplete=""new-password"">

    <div id=""error""></div>

    <div class=""buttons"">
      <button id=""cancel"" type=""button"">Cancel</button>
      <button id=""save"" type=""button"">Save</button>
    </div>
  </div>

  <script>
    (function () {
      'use strict';

      // Swallow DevTools shortcuts. Photino's SetDevToolsEnabled(false) is
      // the real security control; this is a second layer so F12 on a
      // focused input is visibly inert.
      window.addEventListener('keydown', function (e) {
        if (e.key === 'F12') { e.preventDefault(); return; }
        if ((e.ctrlKey || e.metaKey) && e.shiftKey && (e.key === 'I' || e.key === 'i')) {
          e.preventDefault();
          return;
        }
      }, true);

      var usernameEl = document.getElementById('username');
      var passwordEl = document.getElementById('password');
      var saveBtn    = document.getElementById('save');
      var cancelBtn  = document.getElementById('cancel');
      var vaultNameEl = document.getElementById('vault-name');
      var errorEl    = document.getElementById('error');

      // Generate a request id — crypto.randomUUID where available,
      // fallback is fine here because the id only disambiguates our own
      // single in-flight request within this window's lifetime.
      function newId() {
        try {
          if (window.crypto && typeof window.crypto.randomUUID === 'function') {
            return window.crypto.randomUUID();
          }
        } catch (e) { /* ignore */ }
        return 'req-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 10);
      }

      function zeroInputs() {
        // Best-effort: overwrite then clear. Nothing the browser exposes
        // lets us zero the underlying buffer, but this at least makes the
        // DOM value unreachable via ordinary JS.
        try { passwordEl.value = ''; } catch (e) { /* ignore */ }
        try { usernameEl.value = ''; } catch (e) { /* ignore */ }
      }

      function send(kind, payload) {
        if (!window.external || typeof window.external.sendMessage !== 'function') {
          errorEl.style.display = 'block';
          errorEl.textContent = 'Native bridge unavailable. Close this window and retry.';
          return;
        }
        var envelope = { id: newId(), kind: kind, payload: payload };
        try {
          window.external.sendMessage(JSON.stringify(envelope));
        } catch (e) {
          errorEl.style.display = 'block';
          errorEl.textContent = 'Failed to deliver credentials to host.';
        }
      }

      function submit() {
        var u = usernameEl.value;
        var p = passwordEl.value;
        if (!u || u.length === 0) {
          errorEl.style.display = 'block';
          errorEl.textContent = 'Username is required.';
          usernameEl.focus();
          return;
        }
        if (!p || p.length === 0) {
          errorEl.style.display = 'block';
          errorEl.textContent = 'Password is required.';
          passwordEl.focus();
          return;
        }
        errorEl.style.display = 'none';
        errorEl.textContent = '';
        send('credential-save', { username: u, password: p });
        zeroInputs();
      }

      function cancel() {
        send('credential-cancel', {});
        zeroInputs();
      }

      saveBtn.addEventListener('click', submit);
      cancelBtn.addEventListener('click', cancel);

      // Enter in the password field submits; Escape anywhere cancels.
      passwordEl.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); submit(); }
      });
      usernameEl.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); passwordEl.focus(); }
      });
      window.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') { e.preventDefault(); cancel(); }
      });

      // Receive the vault name from the host. Photino's bridge exposes
      // receiveMessage as the inbound hook; the C# side pushes an 'init'
      // envelope containing { vaultName }.
      if (window.external && typeof window.external.receiveMessage === 'function') {
        window.external.receiveMessage(function (raw) {
          try {
            var msg = JSON.parse(raw);
            if (msg && msg.kind === 'init' && msg.payload && msg.payload.vaultName) {
              vaultNameEl.textContent = msg.payload.vaultName;
            }
          } catch (e) { /* ignore malformed */ }
        });
      }

      // Focus the username input on first paint so the user can just start typing.
      setTimeout(function () { try { usernameEl.focus(); } catch (e) { /* ignore */ } }, 0);
    })();
  </script>
</body>
</html>";
    }
}
