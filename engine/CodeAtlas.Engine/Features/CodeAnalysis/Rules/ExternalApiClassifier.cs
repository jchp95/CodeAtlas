using Microsoft.CodeAnalysis;

/// <summary>Recognizes well-known external APIs by their resolved symbols (never by name alone). Shared by the rules and the call graph.</summary>
static class ExternalApiClassifier
{
    private static readonly HashSet<string> EfTerminalOperators = new(StringComparer.Ordinal)
    {
        "ToListAsync", "ToArrayAsync", "FirstAsync", "FirstOrDefaultAsync", "SingleAsync", "SingleOrDefaultAsync", "CountAsync",
        "LongCountAsync", "AnyAsync", "AllAsync", "SumAsync", "MaxAsync", "MinAsync", "FindAsync", "Find", "SaveChanges",
        "SaveChangesAsync", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
        "ExecuteDeleteAsync", "ExecuteUpdateAsync", "ToList", "ToArray", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Count", "Any"
    };

    /// <returns>"HTTP", "Database" or "Message" for a network/database/broker operation; otherwise null.</returns>
    public static string? Remote(IMethodSymbol method)
    {
        var type = method.ContainingType;
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "System.Net.Http" && type.Name == "HttpClient") return "HTTP";
        if (ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) && EfTerminalOperators.Contains(method.Name)) return "Database";
        if ((ns.StartsWith("System.Data", StringComparison.Ordinal) || ns.StartsWith("Microsoft.Data", StringComparison.Ordinal))
            && method.Name.StartsWith("Execute", StringComparison.Ordinal)) return "Database";
        if (ns == "Dapper" && (method.Name.StartsWith("Query", StringComparison.Ordinal) || method.Name.StartsWith("Execute", StringComparison.Ordinal))) return "Database";
        if (ns.StartsWith("MassTransit", StringComparison.Ordinal) && method.Name.StartsWith("Publish", StringComparison.Ordinal)) return "Message";
        return null;
    }

    /// <summary>Synchronous waits on a Task: Task.Wait*, awaiter GetResult, Task&lt;T&gt;.Result getter.</summary>
    public static bool IsBlockingWait(IMethodSymbol method)
    {
        var container = method.ContainingType;
        var ns = container.ContainingNamespace?.ToDisplayString();
        if (ns == "System.Threading.Tasks" && container.Name is "Task" or "Task`1" && (method.Name is "Wait" or "WaitAll" or "WaitAny" or "get_Result")) return true;
        if (ns == "System.Threading.Tasks" && container.OriginalDefinition.Name == "Task" && method.Name == "get_Result") return true;
        if (ns == "System.Threading.Tasks" && container.OriginalDefinition.Name == "ValueTask" && method.Name == "get_Result") return true;
        return ns == "System.Runtime.CompilerServices" && method.Name == "GetResult" && container.Name.EndsWith("Awaiter", StringComparison.Ordinal);
    }
}
