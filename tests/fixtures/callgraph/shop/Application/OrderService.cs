using Shop.Domain;
using Shop.Infrastructure;

namespace Shop.Application;

public class OrderService(IOrderRepository repository)
{
    public Order? Get(int id) => repository.Find(id);

    public void Place(Order order)
    {
        order.AddLine(10m);
        repository.Save(order);
    }

    // Intentional layering violation: the application layer reaches into infrastructure.
    public Order? Legacy(int id) => new SqlOrderRepository().Find(id);

    public int Process(int value) => value + 1;
    public int Process(string value) => value.Length;
    public T Convert<T>(T value) => value;
    public int Twice(int value) => value.Doubled();

    public int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
    public bool IsEven(int n) => n == 0 || IsOdd(n - 1);
    public bool IsOdd(int n) => n != 0 && IsEven(n - 1);

    public int UseLambda(IEnumerable<int> values) => values.Select(v => Process(v)).Sum();
    public int UseDelegate(Func<int, int> callback) => callback(3);
    public int UseMethodGroup() => UseDelegate(Process);

    public double TotalArea(IEnumerable<IShape> shapes) => shapes.Sum(s => s.Area());
    public double OneArea(IShape shape) => shape.Area();
    public string Talk(Animal animal) => animal.Speak();
    public string TalkToPuppy(Puppy puppy) => puppy.Speak();
}

public static class IntExtensions
{
    public static int Doubled(this int value) => value * 2;
}

// A call cycle that crosses two types: Alpha -> Beta -> Alpha.
public class Alpha
{
    public int Run(int n) => n <= 0 ? 0 : new Beta().Run(n - 1);
}

public class Beta
{
    public int Run(int n) => new Alpha().Run(n - 1);
}

// Identical names in different namespaces must stay distinct symbols.
internal interface IAuditSink { void Write(string text); }
internal class FileAuditSink : IAuditSink { public void Write(string text) { } }
internal class MemoryAuditSink : IAuditSink { public void Write(string text) { } }

public class AuditedAction
{
    private readonly IAuditSink[] sinks = { new FileAuditSink(), new MemoryAuditSink() };
    public void Run() { foreach (var sink in sinks) sink.Write("done"); }
}
