using Microsoft.AspNetCore.Authentication;

namespace BifrostQL.Server
{
    public static class UIAuthMiddleware
    {
        public static IApplicationBuilder UseUiAuth(this IApplicationBuilder app)
        {
            app.Use(async (context, next) =>
            {
                if ((context.User?.Identity?.IsAuthenticated ?? false) == false)
                {
                    await context.ChallengeAsync("oauth2", new AuthenticationProperties() { 
                        RedirectUri = "/"
                    });
                } else
                {
                    await next.Invoke(context);
                }
            });
            return app;
        } 
    }
}
