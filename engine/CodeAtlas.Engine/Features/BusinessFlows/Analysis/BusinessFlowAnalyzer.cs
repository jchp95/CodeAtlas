using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class BusinessFlowAnalyzer
{
    private const int MaxDepth = 7;
    private const int MaxSteps = 24;
    private const int MaxFlowsPerProject = 250;

    public static BusinessFlow[] Analyze(
        string repositoryRoot,
        string projectId,
        string projectName,
        SyntaxTree[] syntaxTrees,
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, string[]> repositoryTypeIndex)
    {
        var semanticModels = syntaxTrees.ToDictionary(tree => tree, tree => compilation.GetSemanticModel(tree, ignoreAccessibility: true));
        var registrations = BuildDependencyRegistrations(syntaxTrees, semanticModels);
        var entries = DiscoverEntryPoints(repositoryRoot, syntaxTrees, semanticModels, repositoryTypeIndex)
            .Take(MaxFlowsPerProject).ToArray();
        var flows = new List<BusinessFlow>(entries.Length);

        foreach (var entry in entries)
        {
            var trace = new FlowTrace(repositoryRoot, semanticModels, registrations, repositoryTypeIndex);
            if (entry.Kind == "message-consumer")
            {
                trace.AddStep(
                    "message-consumer",
                    entry.Contract ?? entry.Endpoint.Replace("CONSUME ", string.Empty, StringComparison.Ordinal),
                    entry.File,
                    entry.Line,
                    entry.Confidence,
                    $"Consumidor candidato {entry.OwnerType ?? "IConsumer<T>"}; se detectó el contrato en código fuente. La suscripción efectiva y el binding de MassTransit no están comprobados.");
            }
            else
            {
                trace.AddStep(
                    "endpoint",
                    entry.Endpoint,
                    entry.File,
                    entry.Line,
                    "declared",
                    entry.Kind == "controller"
                        ? "Endpoint HTTP declarado mediante atributos ASP.NET Core; la disponibilidad en ejecución no está comprobada."
                        : "Ruta HTTP declarada mediante un mapeo de Minimal API; la disponibilidad en ejecución no está comprobada.");
            }

            if (entry.HandlerNode is not null)
            {
                trace.TraceNode(entry.HandlerNode, depth: 0);
                if (entry.Kind == "message-consumer")
                    trace.TraceSourceLocalFallbacks(entry.HandlerNode, depth: 0);
            }

            var normalizedEndpoint = NormalizeWhitespace(entry.Endpoint);
            var material = $"{projectId}|{normalizedEndpoint}|{entry.File}|{entry.Kind}";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..20];
            var steps = trace.Steps.ToArray();
            var confidence = steps.Any(step => step.Confidence == "candidate") ? "inferred" : "source-traced";
            var followOnSteps = Math.Max(0, steps.Length - 1);
            var summary = entry.Kind == "message-consumer"
                ? followOnSteps == 0
                    ? "Se detectó un contrato IConsumer<T>, pero no se pudo seguir la implementación Consume con evidencia suficiente."
                    : $"Consumidor de mensajería y {followOnSteps} paso(s) de código relacionados; el binding y la entrega runtime no están comprobados."
                : followOnSteps == 0
                    ? "Se detectó la entrada HTTP, pero no se pudo seguir una llamada interna con suficiente evidencia."
                    : $"Entrada HTTP y {followOnSteps} paso(s) de código relacionados; validar las ramas condicionales y la configuración de ejecución.";

            flows.Add(new BusinessFlow(id, projectId, projectName, normalizedEndpoint, normalizedEndpoint,
                entry.File, entry.Line, confidence, summary, steps));
        }

        return flows.ToArray();
    }

    public static BusinessFlow[] FromNextRoutes(string repositoryRoot, string projectId, string projectName, string[] routeCandidates)
    {
        var flows = new List<BusinessFlow>();
        foreach (var candidate in routeCandidates.Take(MaxFlowsPerProject))
        {
            var parts = candidate.Split(" · ", StringSplitOptions.None);
            if (parts.Length < 3) continue;
            var routeLabel = parts[0].Trim();
            var file = parts[^1].Trim().Replace('\\', '/');
            var material = $"{projectId}|{routeLabel}|{file}|next-route";
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..20];
            var line = routeLabel.IndexOf(' ');
            var verb = line > 0 ? routeLabel[..line] : "ROUTE";
            var path = line > 0 ? routeLabel[(line + 1)..] : routeLabel;
            var step = new BusinessFlowStep(0, "route", routeLabel, file, 1, "candidate",
                "Ruta detectada por convención de Next.js; no se ha trazado todavía el cuerpo TypeScript ni comprobado la ejecución.");
            flows.Add(new BusinessFlow(id, projectId, projectName, $"{verb} {path}", routeLabel, file, 1,
                "candidate", "Ruta detectada por estructura de archivos. El análisis del cuerpo TypeScript está pendiente.", [step]));
        }
        return flows.ToArray();
    }
}
