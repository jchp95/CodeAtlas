# StackPilot — local service operations

StackPilot is the local process-management module of CodeAtlas. It is intentionally explicit: the starter profile generator creates candidate services **disabled**, and the UI requires a user action to enable them. It never kills a process merely because it owns a particular port.

## Configuration location

Default file on macOS:

```text
~/Library/Application Support/CodeAtlas/StackPilot/profiles.json
```

The engine also accepts `--config /absolute/path/profiles.json` for isolated workspaces and tests. Runtime PID metadata and process logs are stored under the same `CodeAtlas/StackPilot` application-support directory, outside source repositories.

## Starter profiles

From the desktop UI, open **Servicios locales** and choose **Crear perfil desde un repositorio…**. CodeAtlas detects ASP.NET Web SDK / executable projects and `package.json` files with a `dev` or `start` script. It creates one profile with disabled templates. Detection is only a starting point: inspect the command, working directory, variables, ports and health endpoint before enabling a service. Run the action again for another repository to add a second profile without replacing previous profiles.

The configuration is ordinary JSON. Example process service:

```json
{
  "id": "zas-express-api",
  "name": "ZasExpress API",
  "enabled": false,
  "kind": "process",
  "command": "dotnet",
  "arguments": ["run", "--project", "src/ZasExpress.Api/ZasExpress.Api.csproj"],
  "workingDirectory": ".",
  "healthUrl": "http://localhost:5080/health",
  "dependsOn": [],
  "environment": {
    "ASPNETCORE_ENVIRONMENT": "Development"
  }
}
```

Example Docker Compose service:

```json
{
  "id": "rabbitmq",
  "name": "RabbitMQ",
  "enabled": false,
  "kind": "compose",
  "command": "",
  "arguments": [],
  "workingDirectory": ".",
  "composeFile": "docker-compose.dev.yml",
  "composeService": "rabbitmq",
  "healthUrl": "http://localhost:15672/",
  "dependsOn": [],
  "environment": {}
}
```

`workspaceRoot` belongs on each profile and sets the base for relative working directories and compose-file paths. `dependsOn` contains service IDs in the same profile. A profile start uses dependency order; a cycle or missing dependency is reported rather than guessed around.

## CLI operations

```bash
cd engine/CodeAtlas.Engine

dotnet run -- stackpilot status
dotnet run -- stackpilot init --workspace /path/to/monorepo
dotnet run -- stackpilot enable <profile-id> <service-id>
dotnet run -- stackpilot start <profile-id> <service-id>
dotnet run -- stackpilot stop <profile-id> <service-id>
dotnet run -- stackpilot start-profile <profile-id>
dotnet run -- stackpilot stop-profile <profile-id>
dotnet run -- stackpilot logs <profile-id> <service-id> --lines 200
dotnet run -- stackpilot doctor
```

`init` is additive and idempotent per canonical workspace path. It does not overwrite profiles already configured. Add `--config <path>` to isolate a configuration file.

## Process ownership and safety

- Starting a process uses executable + argument arrays; configured command/arguments are not interpolated into a shell command.
- Output/error from managed processes is redirected to a per-service log file.
- StackPilot persists process ID and process-start timestamp and checks both to reduce the risk of mistaking a reused PID for its own process.
- Stop acts only on a process that matches StackPilot's saved runtime record; it does not search for or kill a process by port.
- Process stop requests termination of the managed process tree. Review the service command and its child-process behavior before enabling it.
- Docker Compose actions target the configured compose file and service name. They do not stop the whole Compose project unless the configured service command explicitly represents that scope.
- Health URLs are optional; health failure is an observation and does not automatically restart or stop a service.
- Secrets should not be placed in `profiles.json`. Prefer environment values loaded securely in the user's environment or a separate secrets mechanism.

## Current limits

- Generated commands are disabled until the user enables them. The generator may miss custom launch profiles, pnpm/yarn workspace conventions, solution-specific startup arguments, and services requiring special secrets.
- Compose uses the installed Docker CLI and requires Docker to be available and running.
- Runtime status is local to the machine where StackPilot runs. It does not read production state or a remote broker's topology.
- Logs are tailed from local process files or Docker Compose; this is not yet a multi-service timeline with distributed trace correlation.
- The process supervisor has not yet been compiled and integration-tested in this generation environment because the .NET SDK is unavailable here. Run `./scripts/stackpilot-smoke-test.sh` on the target Mac before relying on it for development workflows.
