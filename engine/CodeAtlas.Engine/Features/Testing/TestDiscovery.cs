/// <summary>
/// Static discovery: test methods recognized in source from their resolved attributes (xUnit, NUnit, MSTest). It needs no build,
/// and is exact for methods but not for data-driven rows supplied by a method or a file (<c>Cases</c> is then null).
/// Runtime discovery is whatever a run actually reports.
/// </summary>
static class TestDiscovery
{
    public static DiscoveredTest[] Discover(GraphIndex index) =>
        index.Graph.Nodes.Where(n => !n.IsExternal && n.Tags.Contains("test")).Select(n =>
        {
            var framework = n.Tags.FirstOrDefault(t => t.StartsWith("test:", StringComparison.Ordinal))?[5..] ?? "unknown";
            var skip = n.Tags.FirstOrDefault(t => t.StartsWith("test-skip:", StringComparison.Ordinal))?[10..];
            var cases = n.Tags.FirstOrDefault(t => t.StartsWith("test-cases:", StringComparison.Ordinal)) is { } c && int.TryParse(c[11..], out var count) ? count : (int?)null;
            return new DiscoveredTest(n.Id, n.Project, n.ContainingType ?? "", n.Name, framework, skip is not null, skip, cases, n.Location);
        }).OrderBy(t => t.Project, StringComparer.Ordinal).ThenBy(t => t.Class, StringComparer.Ordinal).ThenBy(t => t.Method, StringComparer.Ordinal).ToArray();

    public static TestDiscoveryReport Report(GraphIndex index)
    {
        var tests = Discover(index);
        return new TestDiscoveryReport(1, "static", tests, tests.Select(t => t.Project).Distinct(StringComparer.Ordinal).ToArray(), index.Graph.Completeness,
            new[] { "Static discovery lists test methods found in source; it does not prove they compile, are discovered by the runner, or pass.", "Data rows from MemberData/TestCaseSource/DynamicData are not counted." });
    }

    /// <summary>VSTest filter selecting exactly these methods (<c>FullyQualifiedName~Class.Method</c> matches theory rows too).</summary>
    public static string Filter(IEnumerable<(string Class, string Method)> tests) =>
        string.Join("|", tests.Distinct().OrderBy(t => t.Class, StringComparer.Ordinal).ThenBy(t => t.Method, StringComparer.Ordinal).Select(t => $"FullyQualifiedName~{t.Class}.{t.Method}"));
}
