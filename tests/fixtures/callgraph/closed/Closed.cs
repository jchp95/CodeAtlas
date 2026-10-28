namespace Closed;

public class Calculator
{
    public int Add(int a, int b) => a + b;
    public int AddTwice(int a, int b) => Add(Add(a, b), b);
    public string Describe(int a) => string.Concat("value ", a.ToString());
}
