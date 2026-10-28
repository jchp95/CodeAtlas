using Shop.Application;
using Shop.Domain;
using Shop.Infrastructure;

namespace Shop.Web;

[AttributeUsage(AttributeTargets.Method)]
public sealed class HttpGetAttribute(string template) : Attribute { public string Template { get; } = template; }

public class Router
{
    public void Add(string route, Func<int, string> handler) { }
}

public static class RouterExtensions
{
    public static void MapGet(this Router router, string route, Func<int, string> handler) => router.Add(route, handler);
}

[Route("api/orders")]
public class OrdersController(OrderService service)
{
    [HttpGet("{id}")]
    public string Get(int id) => service.Get(id)?.Id.ToString() ?? "none";

    [HttpGet("legacy/{id}")]
    public string Legacy(int id) => service.Legacy(id)?.Id.ToString() ?? "none";
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class RouteAttribute(string template) : Attribute { public string Template { get; } = template; }

public static class Startup
{
    public static void Configure(Router router)
    {
        router.MapGet("/ping", id => new OrderService(new SqlOrderRepository()).Process(id).ToString());
    }
}
