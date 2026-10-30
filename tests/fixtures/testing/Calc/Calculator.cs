namespace Calc;

public class Calculator
{
    public int Add(int a, int b) => a + b;

    // Defect on purpose: throws for a zero divisor instead of returning a result the tests expect.
    public int Divide(int a, int b) => Compute(a, b);

    private static int Compute(int a, int b) => a / b;

    public int Parse(string text) => int.Parse(text);
}
