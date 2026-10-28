using Shop.Domain;

namespace Shop.Infrastructure;

public class SqlOrderRepository : IOrderRepository
{
    private readonly HttpClient _http = new();

    public Order? Find(int id) => new Order { Id = id };

    public void Save(Order order)
    {
        // Persistence stand-in: a remote call per saved line.
        foreach (var line in new[] { 1, 2, 3 })
        {
            _http.GetStringAsync("http://db/" + line + "/" + order.Id).GetAwaiter().GetResult();
        }
    }
}
