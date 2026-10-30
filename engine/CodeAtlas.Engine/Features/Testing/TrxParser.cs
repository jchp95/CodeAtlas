using System.Globalization;
using System.Xml.Linq;

/// <summary>Reads Visual Studio Test Results (.trx). The TRX is the only source of test outcomes: nothing is inferred from console text.</summary>
static class TrxParser
{
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static List<(string Name, string Class, string Method, TestOutcome Outcome, string Raw, double DurationMs, string? Message, string? StackTrace)> Parse(string trxPath)
    {
        var document = XDocument.Load(trxPath);
        var definitions = document.Descendants(Ns + "UnitTest").ToDictionary(
            u => (string?)u.Attribute("id") ?? "",
            u => (Class: (string?)u.Element(Ns + "TestMethod")?.Attribute("className") ?? "",
                  Method: (string?)u.Element(Ns + "TestMethod")?.Attribute("name") ?? ""),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<(string, string, string, TestOutcome, string, double, string?, string?)>();
        foreach (var result in document.Descendants(Ns + "UnitTestResult"))
        {
            var name = (string?)result.Attribute("testName") ?? "";
            var id = (string?)result.Attribute("testId") ?? "";
            var raw = (string?)result.Attribute("outcome") ?? "Unknown";
            definitions.TryGetValue(id, out var definition);
            var error = result.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
            var duration = TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var span) ? span.TotalMilliseconds : 0;
            results.Add((name, definition.Class ?? "", (definition.Method ?? name).Split('(')[0], Map(raw), raw, duration,
                (string?)error?.Element(Ns + "Message"), (string?)error?.Element(Ns + "StackTrace")));
        }
        return results;
    }

    private static TestOutcome Map(string raw) => raw switch
    {
        "Passed" => TestOutcome.Passed,
        "Failed" or "Error" or "Timeout" or "Aborted" => TestOutcome.Failed,
        "NotExecuted" => TestOutcome.NotExecuted,
        "Skipped" => TestOutcome.Skipped,
        _ => TestOutcome.Other
    };
}
