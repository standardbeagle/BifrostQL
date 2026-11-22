using System.Net;
using System.Net.Sockets;

namespace BifrostQL.Tests.Shared
{
    /// <summary>
    /// Loopback ports for tests that run a REAL listener.
    ///
    /// <para>Prefer binding port 0 and reading the bound port back from the server: the OS picks
    /// the port at bind time, so nothing can take it first. This helper is for listeners whose port
    /// must be known BEFORE the host starts (the protocol adapters validate 1..65535, and gRPC
    /// listener isolation is keyed on the configured port). A probed port is released before the
    /// listener claims it, so a concurrently running test or process can take it in between and the
    /// bind fails with "address already in use". <see cref="StartWithRetryAsync{T}"/> absorbs that
    /// lost race by probing a fresh port, a bounded number of times, and fails loudly after.</para>
    /// </summary>
    internal static class LoopbackPort
    {
        public const int MaxStartAttempts = 5;

        /// <summary>A loopback port the OS is not using right now. Released on return.</summary>
        public static int Probe() => ProbeDistinct(1)[0];

        /// <summary>
        /// Two distinct free loopback ports. Both probes are held open together, so the pair can
        /// never be the same port (two listeners on one port is a configuration error, not a race).
        /// </summary>
        public static (int First, int Second) ProbePair()
        {
            var ports = ProbeDistinct(2);
            return (ports[0], ports[1]);
        }

        /// <summary>
        /// Runs <paramref name="probeAndStart"/>, which must probe its port(s) itself, retrying
        /// with fresh ports when the bind loses the race. Any other failure propagates at once.
        /// </summary>
        public static async Task<T> StartWithRetryAsync<T>(Func<Task<T>> probeAndStart)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await probeAndStart();
                }
                catch (Exception ex) when (IsAddressInUse(ex))
                {
                    if (attempt == MaxStartAttempts)
                        throw new InvalidOperationException(
                            $"Lost the probe-then-bind port race {MaxStartAttempts} times in a row; "
                            + "something on this machine is churning loopback ports.", ex);
                }
            }
        }

        /// <summary>True when something else is listening on <paramref name="port"/> right now.</summary>
        public static bool IsTaken(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return false;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// True when <paramref name="ex"/> or anything it wraps is an "address already in use"
        /// socket error. Kestrel wraps it in AddressInUseException inside an IOException.
        /// </summary>
        public static bool IsAddressInUse(Exception? ex)
        {
            while (ex is not null)
            {
                if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                    return true;
                if (ex is AggregateException aggregate)
                    return aggregate.InnerExceptions.Any(IsAddressInUse);
                ex = ex.InnerException;
            }
            return false;
        }

        private static int[] ProbeDistinct(int count)
        {
            var probes = new TcpListener[count];
            try
            {
                for (var i = 0; i < count; i++)
                {
                    probes[i] = new TcpListener(IPAddress.Loopback, 0);
                    probes[i].Start();
                }
                return probes.Select(p => ((IPEndPoint)p.LocalEndpoint).Port).ToArray();
            }
            finally
            {
                foreach (var probe in probes)
                    probe?.Stop();
            }
        }
    }
}
