using Calc;
using NUnit.Framework;

namespace Calc.NUnitTests;

public class CalcTests
{
    [Test]
    public void Add_works() => Assert.That(new Calculator().Add(2, 3), Is.EqualTo(5));

    // Fails on purpose: Calculator.Divide throws DivideByZeroException.
    [Test]
    public void Divide_by_zero_fails() => Assert.That(new Calculator().Divide(1, 0), Is.EqualTo(0));

    [Test, Ignore("not ready")]
    public void Skipped_one() => Assert.Fail();

    [TestCase(1, 2, 3)]
    [TestCase(2, 2, 4)]
    public void Add_cases(int a, int b, int expected) => Assert.That(new Calculator().Add(a, b), Is.EqualTo(expected));
}
