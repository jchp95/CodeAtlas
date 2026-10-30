namespace WebApp;

// ASP.NET Core arrives through the shared framework, not through a package: it must still bind.
public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/ping", () => Results.Ok("pong"));
    }
}
