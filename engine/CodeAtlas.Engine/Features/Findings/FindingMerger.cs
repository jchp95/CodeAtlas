/// <summary>
/// Collapses findings that describe the same problem (same stable id), keeping every independent
/// piece of evidence and the strongest validation level reached by any source.
/// </summary>
static class FindingMerger
{
    public static Finding[] Merge(IEnumerable<Finding> findings)
    {
        var merged = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            if (!merged.TryGetValue(finding.Id, out var existing))
            {
                merged[finding.Id] = finding;
                continue;
            }

            var evidence = existing.Evidence.Concat(finding.Evidence)
                .GroupBy(e => $"{e.Kind}|{e.Description}|{e.Location?.File}:{e.Location?.Line}", StringComparer.Ordinal)
                .Select(g => g.First()).ToArray();
            var stronger = finding.Validation.Rank() > existing.Validation.Rank() ? finding : existing;
            merged[finding.Id] = stronger with
            {
                Evidence = evidence,
                Limitations = existing.Limitations.Concat(finding.Limitations).Distinct(StringComparer.Ordinal).ToArray(),
                Fix = existing.Fix ?? finding.Fix,
                RelatedFindingIds = (existing.RelatedFindingIds ?? Array.Empty<string>())
                    .Concat(finding.RelatedFindingIds ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        return merged.Values
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Location.File, StringComparer.Ordinal)
            .ThenBy(f => f.Location.Line)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToArray();
    }
}
