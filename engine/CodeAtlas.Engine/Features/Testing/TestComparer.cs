static class TestComparer
{
    public static TestComparison Compare(TestRunReport baseline, TestRunReport current, string baselinePath, string currentPath)
    {
        static Dictionary<string, TestResult> Index(TestRunReport r) =>
            r.Projects.SelectMany(p => p.Results).GroupBy(t => $"{t.Project}|{t.Name}").ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var a = Index(baseline);
        var b = Index(current);

        TestChange Change(TestResult before, TestResult after) =>
            new(after.Name, after.Project, before.Outcome.ToString(), after.Outcome.ToString(), Math.Round(after.DurationMs - before.DurationMs, 1));

        var regressions = b.Where(p => a.TryGetValue(p.Key, out var before) && before.Outcome == TestOutcome.Passed && p.Value.Outcome == TestOutcome.Failed)
            .Select(p => Change(a[p.Key], p.Value)).OrderBy(c => c.Test, StringComparer.Ordinal).ToArray();
        var fixedNow = b.Where(p => a.TryGetValue(p.Key, out var before) && before.Outcome == TestOutcome.Failed && p.Value.Outcome == TestOutcome.Passed)
            .Select(p => Change(a[p.Key], p.Value)).OrderBy(c => c.Test, StringComparer.Ordinal).ToArray();
        var added = b.Where(p => !a.ContainsKey(p.Key)).Select(p => new TestChange(p.Value.Name, p.Value.Project, "(absent)", p.Value.Outcome.ToString(), null)).OrderBy(c => c.Test, StringComparer.Ordinal).ToArray();
        var removed = a.Where(p => !b.ContainsKey(p.Key)).Select(p => new TestChange(p.Value.Name, p.Value.Project, p.Value.Outcome.ToString(), "(absent)", null)).OrderBy(c => c.Test, StringComparer.Ordinal).ToArray();
        // Millisecond-scale durations are noise on a single run: only large, relative changes are listed, and they are not a performance conclusion.
        var durations = b.Where(p => a.TryGetValue(p.Key, out var before) && p.Value.Outcome == TestOutcome.Passed && before.Outcome == TestOutcome.Passed
                && Math.Abs(p.Value.DurationMs - before.DurationMs) >= 200 && Math.Abs(p.Value.DurationMs - before.DurationMs) > 0.5 * Math.Max(before.DurationMs, 1))
            .Select(p => Change(a[p.Key], p.Value)).ToArray();
        return new TestComparison(1, baselinePath, currentPath, regressions, fixedNow, added, removed, durations,
            new[] { "Outcomes come from the two TRX-backed reports. Duration changes are single measurements: repeat the run (or use a benchmark) before calling them regressions.",
                    "Tests that were skipped or not executed in either run are neither regressions nor fixes." });
    }
}
