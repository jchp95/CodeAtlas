using Calc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Calc.MSTestTests;

[TestClass]
public class CalcTests
{
    [TestMethod]
    public void Add_works() => Assert.AreEqual(5, new Calculator().Add(2, 3));

    // Fails on purpose: Calculator.Divide throws DivideByZeroException.
    [TestMethod]
    public void Divide_by_zero_fails() => Assert.AreEqual(0, new Calculator().Divide(1, 0));

    [TestMethod, Ignore("not ready")]
    public void Skipped_one() => Assert.Fail();

    [DataTestMethod]
    [DataRow(1, 2, 3)]
    [DataRow(2, 2, 4)]
    public void Add_rows(int a, int b, int expected) => Assert.AreEqual(expected, new Calculator().Add(a, b));
}
