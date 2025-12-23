using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace BifrostQL.Server
{
    internal class BifrostContext : Dictionary<string, object?>
    {
        public ClaimsPrincipal? User { get; init; }

        public BifrostContext(HttpContext context)
        {
            User = context.User;
            if (User == null) return;

            var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            id ??= context.User.FindFirstValue("sub");
            Add("id", id ?? context.User?.Identity?.Name ?? string.Empty);
            Add("user", context.User);
            foreach (var g in context.User!.Claims.GroupBy(c => c.Type))
            {
                Add(g.Key, g.Select(c => c.Value).ToArray());
            }
        }
    }
}