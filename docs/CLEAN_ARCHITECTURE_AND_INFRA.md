# Clean Architecture blocks and infrastructure nodes

## Clean Architecture grouping

The main architecture diagram groups common .NET project-name conventions into a single block when at least two distinct layers share a base name in the same repository root. Recognized suffixes include `Api`, `Presentation`, `Web`, `Application`, `Domain`, `Infrastructure`, `Persistence`, `Data`, `Contracts`, `Abstractions`, `Worker`, and `Host`, with dot, hyphen or underscore separators.

Examples:

- `ZasExpress.Api`
- `ZasExpress.Application`
- `ZasExpress.Domain`
- `ZasExpress.Infrastructure`

These should appear as one `ZasExpress` block, represented by the API/presentation project when present. Selecting that block exposes a layer selector; selecting a layer opens the existing project inspector for that project's endpoints, dependencies and code evidence. Project references internal to the grouped service are hidden from the top-level diagram to reduce depth. Cross-service edges remain visible.

This is a presentation heuristic based on project naming and repository identity, not proof that the solution follows Clean Architecture. Projects with unrelated names, missing suffixes or incomplete workspace metadata remain separate blocks rather than being guessed into a group.

Run the pure grouping test:

```bash
./scripts/clean-architecture-grouper-test.sh
```

## Infrastructure nodes

The scanner looks for common infrastructure markers in source/configuration and Docker Compose files. Current candidates include RabbitMQ/MassTransit, MinIO, Redis, SQL Server, PostgreSQL, MongoDB, Meilisearch, Kafka and Elasticsearch. Infrastructure nodes are shared/deduplicated between roots in a multi-repository workspace.

A project-to-infrastructure relationship is labeled inferred and includes the file evidence that caused the match. Detecting a service name in configuration or code does not mean that the container exists, starts successfully, or can currently be reached. Compose topology, health and actual traffic must be checked separately.

## Multiple roots

Use **Seleccionar carpetas** to choose multiple roots in the file dialog, or **Añadir carpetas** to append more. Each project retains its owner root; project and relation IDs are namespaced to avoid collisions. The Flujos explorer includes a repository filter. HTTP endpoints, requests and outbound call traces are grouped by repository, and source opening/request execution use the owning root rather than always using the first selected folder.

StackPilot keeps profiles per workspace root. When the selected root list changes while the operations page is open, missing profile templates can be initialized; newly generated service commands remain disabled pending human review. The operations dashboard reads all profiles from the shared local StackPilot configuration.

Run the pure Swift workspace merger test:

```bash
./scripts/workspace-merger-test.sh
```

## Linking an infrastructure block to StackPilot

Infrastructure blocks can show local controls only when a StackPilot profile explicitly defines a Docker Compose service with an exact recognized name. Current aliases are deliberately narrow: RabbitMQ (`rabbitmq`, `rabbit-mq`), MinIO (`minio`), Redis (`redis`), SQL Server (`sqlserver`, `mssql`), PostgreSQL (`postgresql`, `postgres`), MongoDB (`mongodb`, `mongo`), Meilisearch, Kafka and Elasticsearch. Generic names such as `db`, `broker`, `storage` or `backend` are not guessed. If multiple selected repository profiles contain a matching service, CodeAtlas leaves the block unbound and asks the user to manage it through StackPilot rather than risking a wrong action.

For multi-root workspaces, a shared infrastructure node may originate from source evidence in one repository while its Compose service is configured in another. The linker searches selected profiles globally and binds only one unique exact match. Source-code project blocks remain excluded from Compose matching.

