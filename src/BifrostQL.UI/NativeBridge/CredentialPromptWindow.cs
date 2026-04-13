using System.Text.Json;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Factory for the locked-down credential prompt child window.
    ///
    /// <para>
    /// The prompt is a separate <see cref="PhotinoWindow"/> with its own
    /// WebView2 JS heap, its own <see cref="NativeBridgeHost"/>, and its
    /// HTML loaded from <see cref="CredentialPromptHtml.Html"/> via
    /// <c>LoadRawString</c>. No HTTP, no disk I/O, no shared bridge with
    /// the main SPA — the only way for a credential to leave the child
    /// window is through its own isolated message channel.
    /// </para>
    ///
    /// <para>
    /// <b>Isolation boundary.</b> The child window runs in a separate
    /// WebView instance which means its <c>window.external</c>,
    /// <c>localStorage</c>, cookies and script global object are all
    /// disjoint from the main SPA's. The main SPA's
    /// <see cref="NativeBridgeHost"/> never sees the child's traffic
    /// because the event is raised on the child's <c>PhotinoWindow</c>
    /// instance, which is the only window subscribed to by the child's
    /// <see cref="NativeBridgeHost"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Threading.</b> Photino's <c>WaitForClose</c> is a blocking call
    /// that pumps the native message loop. We run it on a dedicated
    /// background thread so the caller's await point on
    /// <see cref="PromptAsync"/> stays responsive and the cancellation
    /// token can fire without having to interrupt a blocked thread.
    /// </para>
    ///
    /// <para>
    /// <b>Init race.</b> <see cref="PhotinoWindow.SendWebMessage"/> fails
    /// silently if the webview has not finished loading the embedded HTML
    /// and the inline <c>receiveMessage</c> callback has not yet been
    /// installed. Photino 4.0.16 does not expose a reliable "webview
    /// ready" event on Linux, so we push the <c>init</c> envelope in a
    /// short retry loop (5 attempts, 100 ms apart) until either the user
    /// acts or the loop exits. This is safe because the inline JS is
    /// idempotent on repeated <c>init</c> messages — it just updates the
    /// visible vault name.
    /// </para>
    /// </summary>
    public static class CredentialPromptWindow
    {
        /// <summary>
        /// Opens a modal-ish child window and awaits the user's credentials.
        /// </summary>
        /// <param name="parent">
        /// The main application window. Passed for future use
        /// (parenting / positioning) and for a non-null assertion so
        /// callers cannot open the prompt from a disposed host.
        /// </param>
        /// <param name="vaultName">
        /// The vault entry name being unlocked — displayed in the child
        /// window title and subtitle.
        /// </param>
        /// <param name="logger">Optional logger for warnings / bridge errors.</param>
        /// <param name="ct">
        /// Cancellation token. Firing the token resolves the task with
        /// <see cref="CredentialResult.Cancelled"/>; the child window is
        /// closed as part of the unwind.
        /// </param>
        /// <returns>
        /// A <see cref="CredentialResult"/> in one of three shapes:
        /// <see cref="CredentialResult.Saved"/> (user clicked Save),
        /// <see cref="CredentialResult.Cancelled"/> (user cancelled or
        /// closed the window), or <see cref="CredentialResult.Failed"/>
        /// (a host-side error prevented the prompt from running).
        /// </returns>
        public static async Task<CredentialResult> PromptAsync(
            PhotinoWindow parent,
            string vaultName,
            ILogger? logger = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(parent);
            if (string.IsNullOrWhiteSpace(vaultName))
                throw new ArgumentException("vaultName required", nameof(vaultName));

            // Task that resolves exactly once: Save, Cancel, cancellation,
            // or window close — whichever fires first wins, the rest are
            // TrySetResult no-ops.
            var tcs = new TaskCompletionSource<CredentialResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            PhotinoWindow? child = null;
            NativeBridgeHost? childBridge = null;
            CancellationTokenRegistration ctRegistration = default;

            try
            {
                child = new PhotinoWindow()
                    .SetTitle($"Enter credentials for {vaultName}")
                    .SetSize(420, 320)
                    .SetResizable(false)
                    .SetChromeless(false)
                    .SetDevToolsEnabled(false)
                    .SetContextMenuEnabled(false)
                    .Center()
                    .LoadRawString(CredentialPromptHtml.Html);

                childBridge = new NativeBridgeHost(child);

                // Save handler — extract username/password, hand them to
                // the TCS. We return a lightweight ack so the JS side
                // knows the host received the message (not strictly
                // needed but matches the envelope protocol).
                // NOTE: the CT parameter is named (not `_`) because the
                // body below uses `_ = Task.Run(...)` as a discard and
                // a `_`-named parameter in scope would be assigned to
                // rather than discarded.
                childBridge.Register("credential-save", (payload, handlerCt) =>
                {
                    string? username = null;
                    string? password = null;
                    if (payload.ValueKind == JsonValueKind.Object)
                    {
                        if (payload.TryGetProperty("username", out var u) &&
                            u.ValueKind == JsonValueKind.String)
                        {
                            username = u.GetString();
                        }
                        if (payload.TryGetProperty("password", out var p) &&
                            p.ValueKind == JsonValueKind.String)
                        {
                            password = p.GetString();
                        }
                    }

                    tcs.TrySetResult(
                        CredentialResult.Saved(
                            username ?? string.Empty,
                            password ?? string.Empty));

                    // Close the child window from the host side now that
                    // we have the credential. Done in a fire-and-forget
                    // task so we don't block the bridge dispatch loop.
                    var windowToClose = child;
                    _ = Task.Run(() =>
                    {
                        try { windowToClose?.Close(); }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(
                                ex, "CredentialPrompt: child Close after save threw");
                        }
                    });

                    return Task.FromResult<object?>(new { ack = true });
                });

                // Cancel handler — user clicked Cancel / Escape. Same
                // teardown as Save but with a Cancelled result.
                childBridge.Register("credential-cancel", (payload, handlerCt) =>
                {
                    tcs.TrySetResult(CredentialResult.Cancelled());

                    var windowToClose = child;
                    _ = Task.Run(() =>
                    {
                        try { windowToClose?.Close(); }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(
                                ex, "CredentialPrompt: child Close after cancel threw");
                        }
                    });

                    return Task.FromResult<object?>(new { ack = true });
                });

                // Wire the external cancellation token: if the caller
                // cancels, resolve to Cancelled and close the child.
                if (ct.CanBeCanceled)
                {
                    ctRegistration = ct.Register(() =>
                    {
                        tcs.TrySetResult(CredentialResult.Cancelled());
                        var windowToClose = child;
                        _ = Task.Run(() =>
                        {
                            try { windowToClose?.Close(); }
                            catch (Exception ex)
                            {
                                logger?.LogDebug(
                                    ex, "CredentialPrompt: child Close after ct cancel threw");
                            }
                        });
                    });
                }

                // Push the vault name to the child. The webview may not
                // have installed its receiveMessage callback yet, so we
                // retry 5 times at 100 ms intervals. The JS side is
                // idempotent on repeated init messages.
                var bridgeForInit = childBridge;
                _ = Task.Run(async () =>
                {
                    for (var i = 0; i < 5 && !tcs.Task.IsCompleted; i++)
                    {
                        try
                        {
                            await bridgeForInit.SendAsync(
                                "init", new { vaultName }).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            logger?.LogDebug(
                                ex, "CredentialPrompt: init send attempt {Attempt} failed", i);
                        }
                        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    }
                });

                // Run the Photino message loop on a dedicated thread so
                // the caller's await remains responsive. WaitForClose is
                // a native blocking call; it returns when the user (or
                // our Close() above) closes the window. Any unexpected
                // close also resolves the TCS to Cancelled as a safety
                // net for the case where the user clicks the window's
                // close button before hitting Save/Cancel.
                var windowThread = new Thread(() =>
                {
                    try
                    {
                        child.WaitForClose();
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(
                            ex, "CredentialPrompt: WaitForClose threw");
                    }
                    finally
                    {
                        tcs.TrySetResult(CredentialResult.Cancelled());
                    }
                })
                {
                    IsBackground = true,
                    Name = "BifrostQL.CredentialPromptWindow"
                };
                windowThread.Start();

                return await tcs.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "CredentialPrompt: PromptAsync failed");
                return CredentialResult.Failed(ex.Message);
            }
            finally
            {
                // Unregister the CT before disposing the bridge so a
                // late-firing cancel can't race our teardown.
                ctRegistration.Dispose();
                try { child?.Close(); }
                catch (Exception closeEx)
                {
                    logger?.LogDebug(
                        closeEx, "CredentialPrompt: final Close threw");
                }
                childBridge?.Dispose();
            }
        }
    }
}
