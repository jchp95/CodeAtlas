using System.Globalization;
using System.Xml.Linq;

/// <summary>Reads Cobertura line coverage written by coverlet. Only measured lines are reported; nothing is estimated.</summary>
sealed class Cobertura
{
    public double LineRate { get; private init; }
    public int LinesCovered { get; private init; }
    public int LinesValid { get; private init; }
    /// <summary>repository-relative file → line number → hits</summary>
    public Dictionary<string, Dictionary<int, int>> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Cobertura Load(string path, string repositoryRoot)
    {
        var document = XDocument.Load(path);
        var root = document.Root!;
        var coverage = new Cobertura
        {
            LineRate = double.TryParse((string?)root.Attribute("line-rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 0,
            LinesCovered = int.TryParse((string?)root.Attribute("lines-covered"), out var covered) ? covered : 0,
            LinesValid = int.TryParse((string?)root.Attribute("lines-valid"), out var valid) ? valid : 0
        };
        foreach (var @class in document.Descendants("class"))
        {
            var file = (string?)@class.Attribute("filename");
            if (string.IsNullOrEmpty(file)) continue;
            var relative = Path.IsPathRooted(file) ? Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/') : file.Replace('\\', '/');
            if (!coverage.Files.TryGetValue(relative, out var lines)) coverage.Files[relative] = lines = new Dictionary<int, int>();
            foreach (var line in @class.Descendants("line"))
                if (int.TryParse((string?)line.Attribute("number"), out var number) && int.TryParse((string?)line.Attribute("hits"), out var hits))
                    lines[number] = Math.Max(lines.GetValueOrDefault(number), hits);
        }
        return coverage;
    }

    /// <returns>covered, uncovered, notInstrumented (the file is measured but that line holds no executable code) or noData.</returns>
    public string StateOf(string file, int line)
    {
        if (!Files.TryGetValue(file, out var lines)) return "noData";
        // A finding anchors on a statement start; accept the statement's own lines only.
        return lines.TryGetValue(line, out var hits) ? (hits > 0 ? "covered" : "uncovered") : "notInstrumented";
    }
}
