namespace Shop.Application;

public partial class PartialThing
{
    partial void Hook(int value);

    public void Trigger() => Hook(1);
}
