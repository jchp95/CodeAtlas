namespace Shop.Application;

public partial class PartialThing
{
    partial void Hook(int value)
    {
        Console.WriteLine(value);
    }
}
