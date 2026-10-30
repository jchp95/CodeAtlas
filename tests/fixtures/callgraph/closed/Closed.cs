namespace Closed;

// Everything is internal and statically bound, so the whole graph can be enumerated.
internal class Calculator
{
    internal int Add(int a, int b) => a + b;
    internal int AddTwice(int a, int b) => Add(Add(a, b), b);
    internal string Describe(int a) => string.Concat("value ", a.ToString());
}
