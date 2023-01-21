using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace BifrostQL.Core
{
    internal class BifrostContext : Dictionary<string, object?>
    {
        public ClaimsPrincipal? User { get; init;  }

        public BifrostContext(HttpContext context)
        {
            User = context.User;
            Add("id", User?.Identity?.Name);
        }
    }
}