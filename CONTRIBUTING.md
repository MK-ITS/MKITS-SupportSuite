# Contributing

Thank you for your interest in MKITS-SupportSuite!

## Workflow

1. Look at [WORKPLAN.md](WORKPLAN.md) and open issues to find something to work on. For larger changes, open an issue first.
2. Create a feature branch from `main`: `feat/<short-name>`, `fix/<short-name>`, `docs/<short-name>`.
3. Follow [AGENTS.md](AGENTS.md) (golden rules, conventions, definition of done) and [docs/coding-guidelines.md](docs/coding-guidelines.md) – they apply to humans too.
4. Open a pull request against `main`. CI (build, tests, formatting, CodeQL, Docker build) must pass.
5. At least one review is required before merging. Prefer squash merges with a Conventional Commit title.

## Architecture decisions

Significant decisions (technology, protocol, security model, data model) are recorded as ADRs in [docs/adr/](docs/adr/README.md). Copy the template and submit it with your PR.

## Licensing of contributions

By contributing you agree that your contribution is licensed under the [AGPL-3.0-only](LICENSE) license of this project.
Only add dependencies with an AGPL-compatible license and list them in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Security issues

Never open a public issue for a vulnerability – see [SECURITY.md](SECURITY.md).
