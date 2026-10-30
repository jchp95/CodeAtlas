using Calc;
using Xunit;

namespace Calc.XUnit;

public class CalcTests
{
    [Fact]
    public void Add_works() => Assert.Equal(5, new Calculator().Add(2, 3));

    // Fails on purpose: Calculator.Divide throws DivideByZeroException.
    [Fact]
    public void Divide_by_zero_fails() => Assert.Equal(0, new Calculator().Divide(1, 0));

    [Fact(Skip = "not ready")]
    public void Skipped_one() => Assert.True(false);

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(2, 2, 4)]
    public void Add_theory(int a, int b, int expected) => Assert.Equal(expected, new Calculator().Add(a, b));
}
