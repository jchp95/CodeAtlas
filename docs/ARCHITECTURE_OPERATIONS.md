# Architecture blocks + StackPilot runtime controls

## User flow

1. Open a repository and let CodeAtlas scan it.
2. Open **Servicios locales** and create a starter profile for that exact workspace if one does not exist.
3. Review every generated command, working directory, environment entry, health URL, and dependency in `profiles.json` or StackPilot's dashboard. Generated services remain disabled until explicitly enabled.
4. Return to **Arquitectura** and refresh the local operations status.
5. Select a source-project block to reveal its runtime card. The card shows the configured command, resolved working directory, managed PID, health result, logs, and lifecycle actions.
6. Right-click a block for quick start/stop actions, or jump to StackPilot to review/configure the service.

## Binding rules

`ArchitectureRuntimeLinker` associates source-code projects with a service only inside the profile whose `workspaceRoot` matches the repository root. Infrastructure blocks use a separate conservative rule: they may bind to one exact recognized Compose service alias across the selected profiles because a shared broker/object store may be configured in a different selected repository.

Scoring order:

1. A .NET service command contains the project's relative `.csproj` path.
2. A non-Compose service working directory equals the directory containing the project or package metadata file.
3. A .NET service command contains the project's `.csproj` filename.
4. A service name exactly matches the project name.

The highest-scoring unique candidate is selected. If two candidates tie at the highest score, no binding is created; the user must resolve the configuration instead. Docker Compose entries are not attached to source-code project blocks.

Infrastructure blocks use a narrow alias set, for example `RabbitMQ` → `rabbitmq` / `rabbit-mq`, `MinIO` → `minio`, `SQL Server` → `sqlserver` / `mssql`, and `PostgreSQL` → `postgresql` / `postgres`. The service ID, display name or final service-name component of the Compose command must match exactly after normalization. Generic names such as `db`, `storage`, `broker` or `backend` are not inferred. If multiple profiles contain a match, the infrastructure block remains unbound to avoid controlling the wrong service.

## Runtime semantics and safety

- `running` means StackPilot can verify its own process PID/start timestamp, or Docker Compose reports the configured service as running. It does not mean every process launched outside StackPilot is detected.
- `healthy` is shown only when a health URL is configured and its check succeeds. A missing health URL appears as `not-configured`.
- Disabled service templates cannot be started from the diagram. The user must open StackPilot and explicitly review/enable them first.
- Stop targets only a process owned by StackPilot according to saved PID/start-time metadata, or the specifically configured Compose service.
- The diagram relationships are static-analysis evidence and must not be read as proof of live HTTP calls or RabbitMQ message traffic.
- Service command preview is informational. The engine executes configured command and argument arrays rather than shell-interpolating that preview.

## Tests

Run on macOS with Swift installed:

```bash
./scripts/runtime-linker-test.sh
```

It verifies workspace path normalization, exact `.NET` project-command matching, Node package working-directory matching, preservation of runtime status, and fail-closed behavior for ambiguous service mappings. The app itself still requires Xcode/SwiftUI and .NET 10. Use `./scripts/smoke-test.sh` on the target Mac for engine and managed-process integration coverage.
