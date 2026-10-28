sealed record Reached(string NodeId, int Depth, string? ParentId, GraphEdge? Via);

sealed record ReachResult(IReadOnlyDictionary<string, Reached> Visited, bool DepthLimited, bool ResultLimited);

/// <summary>Bounded breadth-first reachability. Depth and cardinality limits are reported, never applied silently.</summary>
static class GraphTraversal
{
    public static ReachResult Reach(GraphIndex index, IEnumerable<string> starts, bool forward, int maxDepth, int maxResults, bool definiteOnly,
        Func<GraphEdge, bool>? edgeFilter = null)
    {
        var visited = new Dictionary<string, Reached>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var start in starts) if (visited.TryAdd(start, new Reached(start, 0, null, null))) queue.Enqueue(start);
        var depthLimited = false;
        var resultLimited = false;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var depth = visited[current].Depth;
            var edges = forward ? index.OutEdges(current) : index.InEdges(current);
            foreach (var edge in edges)
            {
                if (definiteOnly && !GraphIndex.IsDefinite(edge)) continue;
                if (edgeFilter is not null && !edgeFilter(edge)) continue;
                var next = forward ? edge.To : edge.From;
                if (next is null || visited.ContainsKey(next)) continue;
                if (depth >= maxDepth) { depthLimited = true; continue; }
                if (visited.Count > maxResults) { resultLimited = true; return new ReachResult(visited, depthLimited, resultLimited); }
                visited[next] = new Reached(next, depth + 1, current, edge);
                queue.Enqueue(next);
            }
        }
        return new ReachResult(visited, depthLimited, resultLimited);
    }

    /// <summary>Edges from a start node to <paramref name="target"/> following BFS parents (shortest path found).</summary>
    public static IReadOnlyList<Reached> PathTo(ReachResult result, string target)
    {
        var path = new List<Reached>();
        for (var current = target; current is not null && result.Visited.TryGetValue(current, out var reached); current = reached.ParentId)
            path.Add(reached);
        path.Reverse();
        return path;
    }
}

/// <summary>Strongly connected components (iterative Tarjan) over a filtered edge set.</summary>
static class StronglyConnected
{
    public static List<List<string>> Components(IEnumerable<string> vertices, Func<string, IEnumerable<string>> successors)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var counter = 0;

        foreach (var root in vertices.OrderBy(v => v, StringComparer.Ordinal))
        {
            if (index.ContainsKey(root)) continue;
            var work = new Stack<(string Vertex, IEnumerator<string> Successors)>();
            index[root] = low[root] = counter++;
            stack.Push(root); onStack.Add(root);
            work.Push((root, successors(root).Distinct().OrderBy(v => v, StringComparer.Ordinal).GetEnumerator()));

            while (work.Count > 0)
            {
                var (vertex, enumerator) = work.Peek();
                if (enumerator.MoveNext())
                {
                    var next = enumerator.Current;
                    if (!index.ContainsKey(next))
                    {
                        index[next] = low[next] = counter++;
                        stack.Push(next); onStack.Add(next);
                        work.Push((next, successors(next).Distinct().OrderBy(v => v, StringComparer.Ordinal).GetEnumerator()));
                    }
                    else if (onStack.Contains(next)) low[vertex] = Math.Min(low[vertex], index[next]);
                    continue;
                }

                work.Pop();
                if (work.Count > 0) { var parent = work.Peek().Vertex; low[parent] = Math.Min(low[parent], low[vertex]); }
                if (low[vertex] != index[vertex]) continue;
                var component = new List<string>();
                string member;
                do { member = stack.Pop(); onStack.Remove(member); component.Add(member); } while (member != vertex);
                result.Add(component.OrderBy(v => v, StringComparer.Ordinal).ToList());
            }
        }
        return result;
    }
}
