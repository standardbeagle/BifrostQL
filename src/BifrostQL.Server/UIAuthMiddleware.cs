using Microsoft.AspNetCore.Authentication;

namespace BifrostQL.Server
{
    public static class UIAuthMiddleware
    {
        public static IApplicationBuilder UseUiAuth(this IApplicationBuilder app, string? response)
        {
            app.Use(async (context, next) =>
            {
                if (response != null)
                {
                    await context.ChallengeAsync("oauth2", new AuthenticationProperties() { 
                        RedirectUri = response
                    });
                    return;
                }
                await next.Invoke(context);
            });
            return app;
        } 
    }
}
