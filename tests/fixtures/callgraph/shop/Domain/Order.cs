namespace Shop.Domain;

public class Order
{
    public int Id { get; set; }
    public decimal Total { get; private set; }

    public void AddLine(decimal amount) => Total += amount;
}

public interface IOrderRepository
{
    Order? Find(int id);
    void Save(Order order);
}

public interface IShape
{
    double Area();
}

public class Circle : IShape
{
    public double Area() => 3.14 * 2 * 2;
}

public class Square : IShape
{
    public double Area() => 4 * 4;
}

public abstract class Animal
{
    public virtual string Speak() => "...";
}

public class Dog : Animal
{
    public override string Speak() => "woof";
}

public sealed class Puppy : Dog
{
    public override string Speak() => "yip";
}

public class Cat : Animal
{
    public override string Speak() => "meow";
}
