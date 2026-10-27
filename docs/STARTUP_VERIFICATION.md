# Startup verification gate

CodeAtlas uses a fail-closed startup process. `scripts/launch-macos.sh` calls `scripts/verify-before-launch.sh` and launches the desktop target only if that script exits successfully.

The gate checks required toolchains, builds `CodeAtlas.Engine`, runs the complete scanner/integration smoke suite, runs distributed-flow and UI-structure regression tests, and builds the complete SwiftUI target. Any non-zero command exits the gate immediately. The launcher never starts the app after a failed check.

Run the gate without launching:

```bash
./scripts/verify-before-launch.sh
```

Launch with verification:

```bash
./scripts/launch-macos.sh
```

Do not use `cd macOSApp && swift run` when the fail-closed guarantee is required; that command bypasses the gate. The TypeScript pass can report `SKIPPED` if TypeScript is not available in the fixture/repository. That is an explicit optional-capability skip; malformed output or a failed assertion remains a hard failure.
