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
private (ProjectNode[] Nodes, ArchitectureRelation[] Relations) DetectInfrastructure(
        ProjectNode[] projects, List<ProjectDefinition> definitions)
    {
        var markers = new (string Key, string Name, string[] Tokens)[]
        {
            ("rabbitmq", "RabbitMQ", new[] { "rabbitmq", "masstransit", "addrabbitmq", "rabbit-host" }),
            ("minio", "MinIO", new[] { "minio", "minioClient" }),
            ("redis", "Redis", new[] { "redis", "stackexchange.redis", "addstackexchangerediscache" }),
            ("sqlserver", "SQL Server", new[] { "usesqlserver", "microsoft.data.sqlclient", "initial catalog=", "server=localhost;database=" }),
            ("postgresql", "PostgreSQL", new[] { "npgsql", "usesnpgsql", "postgresql://", "postgresql" }),
            ("mongodb", "MongoDB", new[] { "mongoclient", "mongodb://", "mongodb.driver" }),
            ("meilisearch", "Meilisearch", new[] { "meilisearch", "meilisearchclient" }),
            ("kafka", "Kafka", new[] { "confluent.kafka", "addkafka", "kafka://" }),
            ("elasticsearch", "Elasticsearch", new[] { "elastic.clients.elasticsearch", "elasticsearch" })
        };
        var detectedByProject = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var globalEvidence = new Dictionary<string, string>(StringComparer.Ordinal);
        var globalCandidates = EnumerateWorkspaceFiles(root, new[] { ".yml", ".yaml", ".json", ".env", ".example", ".xml" }, 12_000)
            .Where(file => Path.GetFileName(file).StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Equals("compose.yml", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Equals("compose.yaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var file in globalCandidates)
        {
            var content = SafeReadBounded(file, 500_000);
            foreach (var marker in markers)
                if (ContainsAny(content, marker.Tokens) && !globalEvidence.ContainsKey(marker.Key))
                    globalEvidence[marker.Key] = file;
        }

        foreach (var definition in definitions)
        {
            var project = definition.Node;
            var directory = Path.GetDirectoryName(definition.ProjectFile);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            var filePaths = EnumerateWorkspaceFiles(directory, new[] { ".cs", ".csproj", ".json", ".yml", ".yaml", ".env", ".xml", ".ts", ".tsx", ".js", ".mjs", ".cjs" }, 5_000);
            var hits = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in filePaths)
            {
                // Exclude large generated/binary files and cap reads to keep scans responsive.
                var content = SafeReadBounded(file, 250_000);
                if (content.Length == 0) continue;
                foreach (var marker in markers)
                    if (ContainsAny(content, marker.Tokens) && !hits.ContainsKey(marker.Key))
                        hits[marker.Key] = PathTools.Relative(root, file);
            }
            if (hits.Count > 0) detectedByProject[project.Id] = hits;
        }

        var detectedKeys = new HashSet<string>(globalEvidence.Keys, StringComparer.Ordinal);
        foreach (var hits in detectedByProject.Values) detectedKeys.UnionWith(hits.Keys);
        var infraNodes = new List<ProjectNode>();
        var infraByKey = new Dictionary<string, ProjectNode>(StringComparer.Ordinal);
        foreach (var marker in markers.Where(marker => detectedKeys.Contains(marker.Key)))
        {
            var infraNode = new ProjectNode(
                $"infrastructure:{marker.Key}", marker.Name, $".infrastructure/{marker.Key}", "Infrastructure",
                globalEvidence.ContainsKey(marker.Key) ? "docker-compose / configuration" : "configuration / code reference",
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<HttpTargetCandidate>(), Array.Empty<string>(),
                globalEvidence.TryGetValue(marker.Key, out var globalFile)
                    ? new[] { new ArchitectureEvidence("infrastructure-config", marker.Name, PathTools.Relative(root, globalFile), 1, "inferred", "Servicio detectado en configuración de infraestructura.") }
                    : Array.Empty<ArchitectureEvidence>());
            infraNodes.Add(infraNode);
            infraByKey[marker.Key] = infraNode;
        }

        var infraRelations = new List<ArchitectureRelation>();
        foreach (var project in projects)
        {
            if (!detectedByProject.TryGetValue(project.Id, out var hits)) continue;
            foreach (var (key, evidencePath) in hits)
            {
                if (!infraByKey.TryGetValue(key, out var infra)) continue;
                var display = markers.First(marker => marker.Key == key).Name;
                infraRelations.Add(CreateRelation(project, infra, "infrastructure", $"Depende de {display}",
                    $"Referencia detectada en {evidencePath}; relación inferida estáticamente, no es evidencia de una conexión activa.", "inferred", key));
            }
        }
        return (infraNodes.ToArray(), infraRelations.ToArray());

        static bool ContainsAny(string content, IEnumerable<string> tokens) => tokens.Any(token =>
            content.Contains(token, StringComparison.OrdinalIgnoreCase));

        static string SafeReadBounded(string path, int maxCharacters)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 5L * 1024 * 1024) return string.Empty;
                using var reader = new StreamReader(path);
                var buffer = new char[maxCharacters];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                return new string(buffer, 0, count);
            }
            catch { return string.Empty; }
        }

        static IEnumerable<string> EnumerateWorkspaceFiles(string startDirectory, string[] extensions, int maxFiles)
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".next", "dist", "build", "coverage", ".turbo", ".vercel", "vendor", "Pods", "DerivedData", "TestResults" };
            var pending = new Stack<string>();
            pending.Push(Path.GetFullPath(startDirectory));
            var count = 0;
            while (pending.Count > 0 && count < maxFiles)
            {
                var directory = pending.Pop();
                string[] files;
                string[] children;
                try { files = Directory.GetFiles(directory); children = Directory.GetDirectories(directory); }
                catch { continue; }
                foreach (var file in files)
                {
                    if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
                        || Path.GetFileName(file).StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return file;
                        if (++count >= maxFiles) yield break;
                    }
                }
                foreach (var child in children)
                    if (!excluded.Contains(Path.GetFileName(child))) pending.Push(child);
            }
        }
    }
}
