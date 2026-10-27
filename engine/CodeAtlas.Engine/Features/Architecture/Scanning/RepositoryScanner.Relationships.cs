using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

sealed partial class RepositoryScanner
{
private ArchitectureRelation[] BuildRelations(ProjectNode[] projects)
    {
        var relations = new List<ArchitectureRelation>();
        var byPath = projects.ToDictionary(p => NormalizePath(p.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var source in projects)
        {
            foreach (var reference in source.ProjectReferences)
            {
                if (byPath.TryGetValue(NormalizePath(reference), out var target) && source.Id != target.Id)
                    relations.Add(CreateRelation(source, target, "project-reference", "Referencia de proyecto", $"ProjectReference declarado en {source.Path}", "confirmed"));
            }
        }

        foreach (var producer in projects)
        {
            foreach (var contract in producer.ContractsPublished)
            {
                var contractName = NormalizeContractName(contract);
                foreach (var consumer in projects.Where(p => p.Id != producer.Id && p.ContractsConsumed.Any(c => ContractNamesMatch(contractName, c, projects))))
                {
                    var consumerContract = consumer.ContractsConsumed.First(c => ContractNamesMatch(contractName, c, projects));
                    var producerEvidence = producer.PublishedEvidence.FirstOrDefault(e => ContractNamesMatch(contractName, e.Split('|')[0], projects)) ?? producer.Path;
                    var consumerEvidence = consumer.ConsumedEvidence.FirstOrDefault(e => ContractNamesMatch(consumerContract, e.Split('|')[0], projects)) ?? consumer.Path;
                    var displayContract = TypeNames.Short(contractName);
                    relations.Add(CreateRelation(producer, consumer, "message-flow", $"RabbitMQ / MassTransit · {displayContract}",
                        $"Contrato: {contractName}; productor: {producerEvidence}; consumidor: {consumerEvidence}", "inferred", contractName));
                }
            }
        }

        foreach (var source in projects)
        {
            foreach (var candidate in source.HttpTargets)
            {
                if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri)) continue;
                foreach (var target in projects.Where(p => p.Id != source.Id))
                {
                    var hostMatch = HostMatchesProject(uri.Host, target);
                    var loopbackPortMatch = uri.IsLoopback && target.ListenAddresses.Any(address =>
                        Uri.TryCreate(address, UriKind.Absolute, out var listenUri) && listenUri.Port == uri.Port);
                    if (!hostMatch && !loopbackPortMatch) continue;
                    var description = $"HTTP · {uri.Host}:{uri.Port}{uri.AbsolutePath}";
                    relations.Add(CreateRelation(source, target, "http-call", description,
                        $"URL detectada en {candidate.SourcePath}: {candidate.Url}", "inferred"));
                }
            }
        }

        // Link a frontend/workspace package only when its declared dependency name exactly
        // matches another discovered local package name. External libraries remain metadata only.
        foreach (var source in projects.Where(p => p.Kind is "Next.js" or "Node.js"))
        {
            foreach (var target in projects.Where(p => p.Id != source.Id && (p.Kind is "Next.js" or "Node.js")))
            {
                if (source.Dependencies.Contains(target.Name, StringComparer.OrdinalIgnoreCase))
                    relations.Add(CreateRelation(source, target, "workspace-dependency", $"Workspace dependency · {target.Name}",
                        $"{source.Path} declara una dependencia con el nombre del paquete local '{target.Name}'", "inferred"));
            }
        }

        return relations.GroupBy(r => r.Id, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }

    private static DistributedFlow[] BuildDistributedFlows(
        ProjectNode[] projects,
        BusinessFlow[] businessFlows,
        ArchitectureRelation[] relations)
    {
        var projectById = projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var output = new List<DistributedFlow>();
        const int maxDistributedFlows = 500;

        foreach (var relation in relations.Where(relation => relation.Type == "message-flow" && !string.IsNullOrWhiteSpace(relation.Contract)))
        {
            if (!projectById.TryGetValue(relation.SourceId, out var producer)
                || !projectById.TryGetValue(relation.TargetId, out var consumer)) continue;

            var contract = relation.Contract!;
            var producerFlows = businessFlows.Where(flow => flow.ProjectId == producer.Id
                    && flow.Steps.Any(step => step.Kind == "message-publish" && ContractNamesMatch(contract, step.Symbol, projects)))
                .Cast<BusinessFlow?>().ToList();
            if (producerFlows.Count == 0) producerFlows.Add(null);

            var consumerFlows = businessFlows.Where(flow => flow.ProjectId == consumer.Id
                    && flow.Steps.Any(step => step.Kind == "message-consumer" && ContractNamesMatch(contract, step.Symbol, projects)))
                .Cast<BusinessFlow?>().ToList();
            if (consumerFlows.Count == 0) consumerFlows.Add(null);

            var publishEvidence = producer.CodeEvidence?.FirstOrDefault(evidence => evidence.Category == "message-published"
                && ContractNamesMatch(contract, evidence.Symbol, projects));
            var consumeEvidence = consumer.CodeEvidence?.FirstOrDefault(evidence => evidence.Category == "message-consumed"
                && ContractNamesMatch(contract, evidence.Symbol, projects));

            foreach (var producerFlow in producerFlows)
            foreach (var consumerFlow in consumerFlows)
            {
                if (output.Count >= maxDistributedFlows) return output.ToArray();
                var producerEntry = producerFlow?.Name ?? $"PUBLISH {TypeNames.Short(contract)}";
                var consumerEntry = consumerFlow?.Name ?? $"CONSUME {TypeNames.Short(contract)}";
                var producerIdentity = producerFlow?.Id ?? $"fallback:{publishEvidence?.File ?? producer.Path}";
                var consumerIdentity = consumerFlow?.Id ?? $"fallback:{consumeEvidence?.File ?? consumer.Path}";
                var identity = $"{relation.SourceId}|{contract}|{producerIdentity}|{relation.TargetId}|{consumerIdentity}";
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..20];
                var steps = new List<DistributedFlowStep>();

                if (producerFlow is not null)
                {
                    foreach (var step in producerFlow.Steps)
                        steps.Add(new DistributedFlowStep(steps.Count, "producer", producer.Id, producer.Name,
                            step.Kind, step.Symbol, step.File, step.Line, step.Confidence, step.Detail));
                }
                else
                {
                    steps.Add(new DistributedFlowStep(steps.Count, "producer", producer.Id, producer.Name, "message-publish",
                        contract, publishEvidence?.File ?? producer.Path, publishEvidence?.Line ?? 1,
                        publishEvidence?.Confidence ?? "candidate",
                        "Se detectó el contrato publicado en el servicio, pero no se asoció a una ruta HTTP local concreta."));
                }

                steps.Add(new DistributedFlowStep(steps.Count, "transport", producer.Id, producer.Name,
                    "message-transport", $"RabbitMQ / MassTransit · {contract}", publishEvidence?.File ?? producer.Path,
                    publishEvidence?.Line ?? 1, "inferred",
                    $"Relación de contrato inferida. {relation.Evidence}. El exchange, routing key, binding, retries y entrega efectiva requieren configuración o telemetría runtime."));

                if (consumerFlow is not null)
                {
                    foreach (var step in consumerFlow.Steps)
                        steps.Add(new DistributedFlowStep(steps.Count, "consumer", consumer.Id, consumer.Name,
                            step.Kind, step.Symbol, step.File, step.Line, step.Confidence, step.Detail));
                }
                else
                {
                    steps.Add(new DistributedFlowStep(steps.Count, "consumer", consumer.Id, consumer.Name, "message-consumer",
                        contract, consumeEvidence?.File ?? consumer.Path, consumeEvidence?.Line ?? 1,
                        consumeEvidence?.Confidence ?? "candidate",
                        "El contrato aparece como consumido en este servicio, pero no se pudo trazar un método Consume local."));
                }

                output.Add(new DistributedFlow(id, contract, producer.Id, producer.Name, producerFlow?.Id,
                    producerEntry, consumer.Id, consumer.Name, consumerFlow?.Id, consumerEntry, "inferred",
                    $"El contrato {contract} se detecta en el productor y el consumidor. El recorrido combina evidencias de código; la entrega real de mensajes no está verificada.",
                    steps.ToArray()));
            }
        }

        return output.ToArray();
    }

    private ArchitectureRelation[] DetectTypeScriptModuleRelations(ProjectNode[] projects, List<string> warnings)
    {
        var packageProjects = projects.Where(p => p.Kind is "Next.js" or "Node.js")
            .ToDictionary(p => NormalizePath(p.Path), StringComparer.OrdinalIgnoreCase);
        if (packageProjects.Count < 2) return Array.Empty<ArchitectureRelation>();

        var scriptCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "typescript-analyzer.cjs"),
            Path.Combine(Environment.CurrentDirectory, "engine", "CodeAtlas.Engine", "tools", "typescript-analyzer.cjs"),
            Path.Combine(Environment.CurrentDirectory, "tools", "typescript-analyzer.cjs")
        };
        var script = scriptCandidates.FirstOrDefault(File.Exists);
        if (script is null) return Array.Empty<ArchitectureRelation>();

        try
        {
            var startInfo = new ProcessStartInfo("node")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add(root);
            using var process = Process.Start(startInfo);
            if (process is null) return Array.Empty<ArchitectureRelation>();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                warnings.Add("TypeScript Compiler API analysis exceeded 90 seconds and was skipped.");
                return Array.Empty<ArchitectureRelation>();
            }
            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                warnings.Add($"TypeScript Compiler API analyzer failed: {error.Trim()}");
                return Array.Empty<ArchitectureRelation>();
            }

            using var document = JsonDocument.Parse(output);
            var result = document.RootElement;
            if (result.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Array)
            {
                foreach (var diagnostic in diagnostics.EnumerateArray().Take(5))
                    warnings.Add($"TypeScript analyzer: {diagnostic.GetString()}");
            }
            if (!result.TryGetProperty("relations", out var edges) || edges.ValueKind != JsonValueKind.Array)
                return Array.Empty<ArchitectureRelation>();

            var relations = new List<ArchitectureRelation>();
            foreach (var edge in edges.EnumerateArray())
            {
                var sourcePath = edge.TryGetProperty("sourceProject", out var sourceElement) ? sourceElement.GetString() : null;
                var targetPath = edge.TryGetProperty("targetProject", out var targetElement) ? targetElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath)) continue;
                if (!packageProjects.TryGetValue(NormalizePath(sourcePath), out var source) ||
                    !packageProjects.TryGetValue(NormalizePath(targetPath), out var target) || source.Id == target.Id) continue;
                var label = edge.TryGetProperty("label", out var labelElement) ? labelElement.GetString() : null;
                var evidence = edge.TryGetProperty("evidence", out var evidenceElement) ? evidenceElement.GetString() : null;
                relations.Add(CreateRelation(source, target, "module-import",
                    string.IsNullOrWhiteSpace(label) ? "TypeScript module import" : label,
                    string.IsNullOrWhiteSpace(evidence) ? $"Resolved through TypeScript Compiler API from {source.Path} to {target.Path}" : evidence,
                    "confirmed"));
            }
            return relations.ToArray();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Node is optional for repositories that only contain .NET projects.
            if (projects.Any(p => p.Kind is "Next.js" or "Node.js"))
                warnings.Add("Node.js is not available; TypeScript Compiler API relationships were skipped.");
            return Array.Empty<ArchitectureRelation>();
        }
        catch (Exception ex)
        {
            warnings.Add($"TypeScript module analysis was skipped: {ex.Message}");
            return Array.Empty<ArchitectureRelation>();
        }
    }

    private static ArchitectureRelation CreateRelation(ProjectNode source, ProjectNode target, string type, string label, string evidence, string confidence,
        string? stableKey = null)
    {
        var material = $"{source.Id}|{target.Id}|{type}|{stableKey ?? label}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..20];
        return new ArchitectureRelation(id, source.Id, target.Id, type, label, evidence, confidence,
            type == "message-flow" ? stableKey : null);
    }
}
