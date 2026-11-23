using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Features.Authentication;

namespace BifrostQL.Server
{
    /// <summary>
    /// The upgrade request's state that a binary connection's messages read, copied once when the
    /// connection starts. <see cref="HttpContext"/> is not thread-safe, and a connection runs up to
    /// <see cref="BifrostBinaryMiddleware.MaxConcurrentRequestsPerConnection"/> messages at once:
    /// reading the shared context from them races its lazily installed features (the first read of
    /// <c>User</c> or <c>Request.Query</c> writes the feature collection). Each message instead gets
    /// its own context from <see cref="CreateMessageContext"/>, built from this snapshot, so no
    /// message task touches the upgrade request's context.
    /// </summary>
    internal sealed class BinaryUpgradeSnapshot
    {
        private readonly string _method;
        private readonly string _scheme;
        private readonly string _protocol;
        private readonly PathString _pathBase;
        private readonly PathString _path;
        private readonly QueryString _queryString;
        private readonly KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>[] _headers;
        private readonly KeyValuePair<object, object?>[] _items;
        private readonly string _connectionId;
        private readonly IPAddress? _remoteIpAddress;
        private readonly int _remotePort;
        private readonly IPAddress? _localIpAddress;
        private readonly int _localPort;
        private readonly X509Certificate2? _clientCertificate;
        private readonly string _traceIdentifier;

        private BinaryUpgradeSnapshot(HttpContext context)
        {
            var request = context.Request;
            _method = request.Method;
            _scheme = request.Scheme;
            _protocol = request.Protocol;
            _pathBase = request.PathBase;
            _path = request.Path;
            _queryString = request.QueryString;
            _headers = request.Headers.ToArray();
            _items = context.Items.ToArray();
            var connection = context.Connection;
            _connectionId = connection.Id;
            _remoteIpAddress = connection.RemoteIpAddress;
            _remotePort = connection.RemotePort;
            _localIpAddress = connection.LocalIpAddress;
            _localPort = connection.LocalPort;
            _clientCertificate = connection.ClientCertificate;
            _traceIdentifier = context.TraceIdentifier;
            User = context.User;
            RequestServices = context.RequestServices;
        }

        /// <summary>Copies what the messages read. Call before any message task starts.</summary>
        public static BinaryUpgradeSnapshot Capture(HttpContext context) => new(context);

        /// <summary>The upgrade request's principal.</summary>
        public ClaimsPrincipal User { get; }

        /// <summary>
        /// The upgrade request's services. Resolving from a provider is thread-safe; each message
        /// creates its own scope from it.
        /// </summary>
        public IServiceProvider RequestServices { get; }

        /// <summary>
        /// A fresh context for one message: the upgrade request's method, path, headers, connection
        /// and client certificate, a copy of its items, and the message's own principal, services
        /// and abort token. It has no response: nothing a message writes reaches the socket that way.
        /// </summary>
        public HttpContext CreateMessageContext(ClaimsPrincipal user, IServiceProvider services, CancellationToken aborted)
        {
            var headers = new HeaderDictionary(_headers.Length);
            foreach (var (name, value) in _headers)
                headers[name] = value;

            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(new HttpRequestFeature
            {
                Method = _method,
                Scheme = _scheme,
                Protocol = _protocol,
                PathBase = _pathBase.Value ?? "",
                Path = _path.Value ?? "",
                QueryString = _queryString.Value ?? "",
                Headers = headers,
            });
            features.Set<IHttpResponseFeature>(new HttpResponseFeature());
            features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
            features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
            {
                ConnectionId = _connectionId,
                RemoteIpAddress = _remoteIpAddress,
                RemotePort = _remotePort,
                LocalIpAddress = _localIpAddress,
                LocalPort = _localPort,
            });
            features.Set<ITlsConnectionFeature>(new TlsConnectionFeature { ClientCertificate = _clientCertificate });
            features.Set<IHttpRequestIdentifierFeature>(new HttpRequestIdentifierFeature { TraceIdentifier = _traceIdentifier });
            features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = aborted });
            features.Set<IHttpAuthenticationFeature>(new HttpAuthenticationFeature { User = user });
            features.Set<IServiceProvidersFeature>(new ServiceProvidersFeature { RequestServices = services });
            var items = new ItemsFeature();
            foreach (var (key, value) in _items)
                items.Items[key] = value;
            features.Set<IItemsFeature>(items);
            return new DefaultHttpContext(features);
        }
    }
}
