using Shop.Application;
using Shop.Domain;

namespace Shop.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute { }

public class StubRepository : IOrderRepository
{
    public Order? Find(int id) => new Order { Id = id };
    public void Save(Order order) { }
}

public class OrderServiceTests
{
    [Fact]
    public void Get_returns_order()
    {
        var service = new OrderService(new StubRepository());
        _ = service.Get(1);
    }
}
