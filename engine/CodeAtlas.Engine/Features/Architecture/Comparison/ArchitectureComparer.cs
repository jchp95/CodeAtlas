using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class ArchitectureComparer
{
    public static ArchitectureDelta Compare(ArchitectureSnapshot previous, ArchitectureSnapshot current)
    {
        var oldProjects = previous.Projects.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var newProjects = current.Projects.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var addedProjects = current.Projects.Where(p => !oldProjects.ContainsKey(p.Id)).OrderBy(p => p.Name).ToArray();
        var removedProjects = previous.Projects.Where(p => !newProjects.ContainsKey(p.Id)).OrderBy(p => p.Name).ToArray();
        var changedProjects = new List<ChangedProject>();

        foreach (var newer in current.Projects)
        {
            if (!oldProjects.TryGetValue(newer.Id, out var older)) continue;
            var fields = new List<string>();
            if (!string.Equals(older.Kind, newer.Kind, StringComparison.Ordinal)) fields.Add("Tipo de proyecto");
            if (!string.Equals(older.Framework, newer.Framework, StringComparison.Ordinal)) fields.Add("Framework");
            if (!SequenceEqual(older.Endpoints, newer.Endpoints)) fields.Add("Endpoints / rutas");
            if (!SequenceEqual(older.Dependencies, newer.Dependencies)) fields.Add("Dependencias externas");
            if (!SequenceEqual(older.ProjectReferences, newer.ProjectReferences)) fields.Add("Referencias de proyecto");
            if (!SequenceEqual(older.ContractsPublished, newer.ContractsPublished)) fields.Add("Contratos publicados");
            if (!SequenceEqual(older.ContractsConsumed, newer.ContractsConsumed)) fields.Add("Contratos consumidos");
            if (!older.HttpTargets.Select(x => $"{x.Url}|{x.SourcePath}").OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(newer.HttpTargets.Select(x => $"{x.Url}|{x.SourcePath}").OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                fields.Add("Destinos HTTP candidatos");
            if (!SequenceEqual(older.ListenAddresses, newer.ListenAddresses)) fields.Add("Puertos / URLs locales");
            if (!EvidenceSequenceEqual(older.CodeEvidence, newer.CodeEvidence)) fields.Add("Inyección de dependencias / contratos detectados");
            if (fields.Count > 0) changedProjects.Add(new ChangedProject(newer.Id, newer.Name, fields.ToArray()));
        }

        var oldRelations = previous.Relations.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var newRelations = current.Relations.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var addedRelations = current.Relations.Where(r => !oldRelations.ContainsKey(r.Id)).OrderBy(r => r.Type).ThenBy(r => r.Label).ToArray();
        var removedRelations = previous.Relations.Where(r => !newRelations.ContainsKey(r.Id)).OrderBy(r => r.Type).ThenBy(r => r.Label).ToArray();

        // Older snapshots may not have businessFlows. Compare semantic path identities and
        // ignore line-only movement so formatting does not look like an architectural change.
        var oldFlows = (previous.BusinessFlows ?? Array.Empty<BusinessFlow>()).ToDictionary(flow => flow.Id, StringComparer.Ordinal);
        var newFlows = (current.BusinessFlows ?? Array.Empty<BusinessFlow>()).ToDictionary(flow => flow.Id, StringComparer.Ordinal);
        var flowChanges = new List<FlowChange>();
        foreach (var flow in current.BusinessFlows ?? Array.Empty<BusinessFlow>())
        {
            if (!oldFlows.TryGetValue(flow.Id, out var oldFlow))
            {
                flowChanges.Add(new FlowChange(flow.Id, "added", flow.ProjectId, flow.ProjectName, flow.Name,
                    ["Nuevo flujo detectado"], Array.Empty<BusinessFlowStep>(), flow.Steps));
                continue;
            }
            if (!string.Equals(FlowSignature(oldFlow), FlowSignature(flow), StringComparison.Ordinal))
            {
                var flowFields = new List<string>();
                if (!string.Equals(oldFlow.EntryPoint, flow.EntryPoint, StringComparison.Ordinal)) flowFields.Add("Entrada HTTP modificada");
                if (!SequenceEqual(oldFlow.Steps.Select(step => $"{step.Kind}|{step.Symbol}").ToArray(),
                    flow.Steps.Select(step => $"{step.Kind}|{step.Symbol}").ToArray())) flowFields.Add("Secuencia de pasos modificada");
                if (flowFields.Count == 0) flowFields.Add("Evidencia o dependencias del flujo modificadas");
                flowChanges.Add(new FlowChange(flow.Id, "modified", flow.ProjectId, flow.ProjectName, flow.Name,
                    flowFields.ToArray(), oldFlow.Steps, flow.Steps));
            }
        }
        foreach (var flow in previous.BusinessFlows ?? Array.Empty<BusinessFlow>())
        {
            if (!newFlows.ContainsKey(flow.Id))
                flowChanges.Add(new FlowChange(flow.Id, "removed", flow.ProjectId, flow.ProjectName, flow.Name,
                    ["Flujo ya no detectado"], flow.Steps, Array.Empty<BusinessFlowStep>()));
        }

        var oldDistributedFlows = (previous.DistributedFlows ?? Array.Empty<DistributedFlow>())
            .ToDictionary(flow => flow.Id, StringComparer.Ordinal);
        var newDistributedFlows = (current.DistributedFlows ?? Array.Empty<DistributedFlow>())
            .ToDictionary(flow => flow.Id, StringComparer.Ordinal);
        var distributedFlowChanges = new List<DistributedFlowChange>();
        foreach (var flow in current.DistributedFlows ?? Array.Empty<DistributedFlow>())
        {
            if (!oldDistributedFlows.TryGetValue(flow.Id, out var oldFlow))
            {
                distributedFlowChanges.Add(ToDistributedChange(flow, "added", ["Recorrido entre servicios añadido"]));
                continue;
            }
            if (!string.Equals(DistributedFlowSignature(oldFlow), DistributedFlowSignature(flow), StringComparison.Ordinal))
            {
                var fields = new List<string>();
                if (!string.Equals(oldFlow.Contract, flow.Contract, StringComparison.Ordinal)) fields.Add("Contrato de mensajería modificado");
                if (!SequenceEqual(oldFlow.Steps.Select(step => $"{step.Phase}|{step.ProjectId}|{step.Kind}|{step.Symbol}").ToArray(),
                    flow.Steps.Select(step => $"{step.Phase}|{step.ProjectId}|{step.Kind}|{step.Symbol}").ToArray()))
                    fields.Add("Secuencia distribuida modificada");
                if (fields.Count == 0) fields.Add("Evidencia de código del recorrido distribuido modificada");
                distributedFlowChanges.Add(ToDistributedChange(flow, "modified", fields.ToArray()));
            }
        }
        foreach (var flow in previous.DistributedFlows ?? Array.Empty<DistributedFlow>())
        {
            if (!newDistributedFlows.ContainsKey(flow.Id))
                distributedFlowChanges.Add(ToDistributedChange(flow, "removed", ["Recorrido entre servicios ya no detectado"]));
        }

        var summary = new List<string>();
        if (addedProjects.Length > 0) summary.Add($"{addedProjects.Length} proyecto(s) añadido(s)");
        if (removedProjects.Length > 0) summary.Add($"{removedProjects.Length} proyecto(s) eliminado(s)");
        if (changedProjects.Count > 0) summary.Add($"{changedProjects.Count} proyecto(s) con cambios arquitectónicos");
        if (addedRelations.Length > 0) summary.Add($"{addedRelations.Length} relación(es) añadida(s)");
        if (removedRelations.Length > 0) summary.Add($"{removedRelations.Length} relación(es) eliminada(s)");
        var addedFlows = flowChanges.Count(change => change.Status == "added");
        var removedFlows = flowChanges.Count(change => change.Status == "removed");
        var modifiedFlows = flowChanges.Count(change => change.Status == "modified");
        if (addedFlows > 0) summary.Add($"{addedFlows} flujo(s) de negocio añadido(s)");
        if (removedFlows > 0) summary.Add($"{removedFlows} flujo(s) de negocio eliminado(s)");
        if (modifiedFlows > 0) summary.Add($"{modifiedFlows} flujo(s) de negocio modificado(s)");
        var addedDistributedFlows = distributedFlowChanges.Count(change => change.Status == "added");
        var removedDistributedFlows = distributedFlowChanges.Count(change => change.Status == "removed");
        var modifiedDistributedFlows = distributedFlowChanges.Count(change => change.Status == "modified");
        if (addedDistributedFlows > 0) summary.Add($"{addedDistributedFlows} recorrido(s) distribuido(s) añadido(s)");
        if (removedDistributedFlows > 0) summary.Add($"{removedDistributedFlows} recorrido(s) distribuido(s) eliminado(s)");
        if (modifiedDistributedFlows > 0) summary.Add($"{modifiedDistributedFlows} recorrido(s) distribuido(s) modificado(s)");
        if (summary.Count == 0) summary.Add("No se detectaron cambios en la estructura analizada.");

        return new ArchitectureDelta(previous.HeadCommit, current.HeadCommit, previous.Branch, current.Branch,
            addedProjects, removedProjects, changedProjects.ToArray(), addedRelations, removedRelations, summary.ToArray(),
            flowChanges.ToArray(), distributedFlowChanges.ToArray());
    }

    private static DistributedFlowChange ToDistributedChange(DistributedFlow flow, string status, string[] changes) =>
        new(flow.Id, status, flow.Contract, flow.ProducerProjectName, flow.ConsumerProjectName,
            flow.ProducerEntryPoint, flow.ConsumerEntryPoint, changes);

    private static string DistributedFlowSignature(DistributedFlow flow) => string.Join("\n", flow.Steps
        .OrderBy(step => step.Sequence)
        .Select(step => $"{step.Phase}|{step.ProjectId}|{step.Kind}|{step.Symbol}|{step.File}|{step.Confidence}"));

    private static string FlowSignature(BusinessFlow flow) => string.Join("\n", flow.Steps
        .OrderBy(step => step.Sequence)
        .Select(step => $"{step.Kind}|{step.Symbol}|{step.File}|{step.Confidence}|{step.Detail}"));

    private static bool SequenceEqual(string[] first, string[] second) =>
        first.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
             .SequenceEqual(second.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static bool EvidenceSequenceEqual(ArchitectureEvidence[]? first, ArchitectureEvidence[]? second) =>
        (first ?? Array.Empty<ArchitectureEvidence>()).OrderBy(EvidenceKey, StringComparer.Ordinal)
            .Select(EvidenceKey).SequenceEqual((second ?? Array.Empty<ArchitectureEvidence>())
                .OrderBy(EvidenceKey, StringComparer.Ordinal).Select(EvidenceKey), StringComparer.Ordinal);

    private static string EvidenceKey(ArchitectureEvidence evidence) =>
        $"{evidence.Category}|{evidence.Symbol}|{evidence.File}|{evidence.Confidence}|{evidence.Detail}";
}
