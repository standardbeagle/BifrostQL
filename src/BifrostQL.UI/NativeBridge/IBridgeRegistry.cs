using System.Text.Json;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Anything a bridge handler can register itself against. Implemented by
    /// <see cref="NativeBridgeHost"/> (the Photino webview channel this exists for)
    /// and by <see cref="HttpBridgeHost"/> (an opt-in loopback HTTP transport used
    /// by the end-to-end suite, which runs the app headless and therefore has no
    /// webview).
    ///
    /// <para>The point of the seam is that the HANDLERS are shared. A test transport
    /// that re-implemented exec-sql or the visual-query build would be testing its
    /// own copy of the logic, which is worth nothing; both transports dispatch into
    /// the same handler instances so the e2e suite exercises the code the desktop
    /// app runs.</para>
    /// </summary>
    public interface IBridgeRegistry
    {
        /// <summary>
        /// Registers an async handler for <paramref name="kind"/>, replacing any
        /// previously registered handler for the same kind.
        /// </summary>
        void Register(string kind, Func<JsonElement, CancellationToken, Task<object?>> handler);
    }
}
