using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Turns the evented Speedscope file written by <c>dotnet-trace --profile dotnet-sampled-thread-time</c> into per-method CPU and blocked time.
/// The leaf marker frame (<c>CPU_TIME</c> / <c>BLOCKED_TIME</c>) says whether the thread was running or waiting, so CPU and wait time are never mixed.
/// </summary>
static partial class SpeedscopeCpu
{
    [GeneratedRegex(@"^(Process\d*\s|Threads$|Thread \(|\(Non-Activities\)|CPU_TIME$|BLOCKED_TIME$|UNMANAGED_CODE_TIME$)")]
    private static partial Regex PseudoFrame();

    public sealed class MethodAccumulator
    {
        public double Exclusive, Inclusive, Own, Blocked;
    }

    public sealed class TrieNode
    {
        public string Symbol = "";
        public double Inclusive;
        public Dictionary<string, TrieNode> Children = new(StringComparer.Ordinal);
    }

    public sealed record Aggregate(Dictionary<string, MethodAccumulator> Methods, TrieNode Root, double CpuMs, double BlockedMs, double SpanMs, int Threads, double StepMs);

    public static Aggregate Parse(string path, Func<string, bool> isSourceFrame)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var frames = document.RootElement.GetProperty("shared").GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("name").GetString() ?? "").ToArray();
        var pseudo = frames.Select(n => PseudoFrame().IsMatch(n)).ToArray();
        var methods = new Dictionary<string, MethodAccumulator>(StringComparer.Ordinal);
        var root = new TrieNode { Symbol = "(root)" };
        double cpu = 0, blocked = 0, spanStart = double.MaxValue, spanEnd = 0;
        var threads = 0;
        var steps = new List<double>();

        MethodAccumulator Of(string name) => methods.TryGetValue(name, out var m) ? m : methods[name] = new MethodAccumulator();

        foreach (var profile in document.RootElement.GetProperty("profiles").EnumerateArray())
        {
            if (profile.GetProperty("type").GetString() != "evented") continue;
            threads++;
            var stack = new List<int>();
            double? last = null;
            foreach (var e in profile.GetProperty("events").EnumerateArray())
            {
                var at = e.GetProperty("at").GetDouble();
                spanStart = Math.Min(spanStart, at); spanEnd = Math.Max(spanEnd, at);
                if (last is { } previous && at > previous && stack.Count > 0)
                {
                    var dt = at - previous;
                    if (steps.Count < 5000) steps.Add(dt);
                    var marker = frames[stack[^1]];
                    var chain = stack.Where(i => !pseudo[i]).Select(i => frames[i]).ToList();
                    if (chain.Count > 0)
                    {
                        if (marker == "CPU_TIME")
                        {
                            cpu += dt;
                            Of(chain[^1]).Exclusive += dt;
                            foreach (var name in chain.Distinct()) Of(name).Inclusive += dt;
                            var owner = chain.LastOrDefault(isSourceFrame);
                            if (owner is not null) Of(owner).Own += dt;
                            var node = root; node.Inclusive += dt;
                            foreach (var name in chain)
                            {
                                if (!node.Children.TryGetValue(name, out var child)) node.Children[name] = child = new TrieNode { Symbol = name };
                                child.Inclusive += dt;
                                node = child;
                            }
                        }
                        else if (marker == "BLOCKED_TIME")
                        {
                            blocked += dt;
                            foreach (var name in chain.Distinct()) Of(name).Blocked += dt;
                        }
                    }
                }
                switch (e.GetProperty("type").GetString())
                {
                    case "O": stack.Add(e.GetProperty("frame").GetInt32()); break;
                    case "C":
                        var frame = e.GetProperty("frame").GetInt32();
                        var at2 = stack.LastIndexOf(frame);
                        if (at2 >= 0) stack.RemoveRange(at2, stack.Count - at2);
                        break;
                }
                last = at;
            }
        }
        steps.Sort();
        return new Aggregate(methods, root, cpu, blocked, spanEnd > spanStart ? spanEnd - spanStart : 0, threads, steps.Count == 0 ? 0 : steps[steps.Count / 2]);
    }

    /// <summary>"Assembly!Ns.Type.Method(args)" → ("Assembly", "Ns.Type.Method(args)").</summary>
    public static (string Module, string Symbol) Split(string frame)
    {
        var bang = frame.IndexOf('!');
        return bang < 0 ? ("", frame) : (frame[..bang], frame[(bang + 1)..]);
    }
}
