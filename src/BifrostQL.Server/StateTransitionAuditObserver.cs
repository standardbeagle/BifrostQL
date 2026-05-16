using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using System.Globalization;

namespace BifrostQL.Server;

public sealed class StateTransitionAuditObserver : IStateTransitionObserver
{
    private readonly IBifrostWorkflowExecutor _executor;

    public StateTransitionAuditObserver(IBifrostWorkflowExecutor executor)
    {
        _executor = executor;
    }

    public async ValueTask OnTransitionAsync(
        StateTransitionInfo transition,
        IDictionary<string, object?> userContext)
    {
        if (transition is null) throw new ArgumentNullException(nameof(transition));
        if (userContext is null) throw new ArgumentNullException(nameof(userContext));

        var audit = new Dictionary<string, object?>
        {
            ["action"] = $"{transition.Entity}.{transition.From}->{transition.To}",
            ["entity_type"] = transition.Entity,
            ["entity_id"] = transition.EntityId?.ToString(),
            ["actor_user_id"] = ToAuditNumericValue(transition.Actor),
            ["summary"] = transition.EventName ?? "StateTransitioned",
            ["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };

        if (userContext.TryGetValue(MetadataKeys.Auth.DefaultTenantContextKey, out var tenantId))
            audit["tenant_id"] = ToAuditNumericValue(tenantId);

        await _executor.InsertAsync("audit_log", audit, BuildAuditContext(userContext));
    }

    private static IDictionary<string, object?> BuildAuditContext(IDictionary<string, object?> userContext)
    {
        var auditContext = new Dictionary<string, object?>(userContext, StringComparer.OrdinalIgnoreCase);
        auditContext[MetadataKeys.Auth.DefaultRolesContextKey] = new[] { MetadataKeys.Policy.DefaultAdminRole };
        return auditContext;
    }

    private static object? ToAuditNumericValue(object? value)
    {
        if (value is null)
            return null;

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong)
            return value;

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue)
            ? numericValue
            : value;
    }
}
