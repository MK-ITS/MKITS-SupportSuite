# Development workflow

Practical notes for humans and AI agents working on this repository. Rules and conventions: [../AGENTS.md](../AGENTS.md).

## Build and test

```bash
dotnet restore MKITS.SupportSuite.slnx
dotnet build   MKITS.SupportSuite.slnx -c Release
dotnet test    MKITS.SupportSuite.slnx -c Release
dotnet format  MKITS.SupportSuite.slnx --verify-no-changes
```

Run the server locally: `dotnet run --project src/Server/MKITS.SupportSuite.Server` → http://localhost:5080. Data (SQLite database, keys) goes to `src/Server/MKITS.SupportSuite.Server/data/` in development – delete the folder to start fresh.

## Branches and pull requests

- `main` is protected: changes only via pull request, CI checks must pass, review threads must be resolved.
- Branch names: `feat/…`, `fix/…`, `docs/…`, `chore/…`. One topic per PR; large phases are split into several PRs.
- Stacked PRs are fine (base = the previous feature branch); GitHub retargets them to `main` when the base is merged.

## Database migrations (EF Core)

The schema lives in [`MkitsDbContext`](../src/Server/MKITS.SupportSuite.Server/Infrastructure/Data/MkitsDbContext.cs) and is changed **only** through migrations in `Infrastructure/Data/Migrations/`. The server applies pending migrations automatically at startup.

**With NuGet access (normal development machine):**

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add <PascalCaseName> \
  --project src/Server/MKITS.SupportSuite.Server \
  --output-dir Infrastructure/Data/Migrations
```

(Do not pass `--namespace`: EF would then place the model snapshot in a folder derived from the namespace.)

**Without NuGet access (e.g. sandboxed AI agents):** change the model, commit and push to your feature branch with the marker `[ef-migration:<PascalCaseName>]` in the commit message. The workflow [`ef-migration.yml`](../.github/workflows/ef-migration.yml) generates the migration and commits it back to the branch. Then `git pull`.

Rules:
- Never edit a migration that has been merged to `main` – add a new one.
- Generated migration files are marked as generated code in `.editorconfig` (style rules do not apply); the workflow adds the license header.
- SQLite cannot alter columns in place; EF Core rebuilds tables for such changes – check the generated migration for data loss.

## Diagnosing CI failures without log access

Some environments cannot download GitHub Actions logs or artifacts. The CI therefore reports problems as **check annotations**, which are available through the API:

```bash
gh api repos/MK-ITS/MKITS-SupportSuite/commits/<sha>/check-runs --jq '.check_runs[] | "\(.id) \(.name) \(.conclusion)"'
gh api repos/MK-ITS/MKITS-SupportSuite/check-runs/<id>/annotations --jq '.[] | "\(.path):\(.start_line) \(.message)"'
gh api repos/MK-ITS/MKITS-SupportSuite/actions/jobs/<id> --jq '.steps[] | "\(.name) \(.conclusion)"'
```

- Compiler errors and analyzer findings appear as annotations automatically.
- Failed tests are turned into annotations by the step *Report failed tests as annotations* (parses the TRX files).

## Test helpers

| Helper | Use |
|---|---|
| `MkitsWebApplicationFactory` | full server in memory with a temporary data directory; header `X-Test-Remote-Ip` sets the client IP |
| `TestDatabase` | real SQLite database in a temp file, migrated; implements `IDbContextFactory<MkitsDbContext>` |
| `ManualTimeProvider` | deterministic clock (`Advance(...)`) for time-dependent logic |
