# Security Policy

MKITS-SupportSuite gives remote control over computers. We take security reports seriously.

## Supported versions

The project is in early development. Until version 1.0, only the latest commit on `main` receives security fixes.

| Version | Supported |
|---|---|
| `main` (0.x) | ✅ |

## Reporting a vulnerability

**Please do not report vulnerabilities through public GitHub issues, discussions or pull requests.**

Report privately via GitHub's **[private vulnerability reporting](https://github.com/MK-ITS/MKITS-SupportSuite/security/advisories/new)**.

Please include:

- affected component (server, web UI, relay, agent, viewer) and version / commit,
- a description of the issue and its impact,
- steps to reproduce or a proof of concept,
- any suggested fix.

We aim to acknowledge reports within **5 working days** and to provide a fix or mitigation plan within **30 days**, depending on severity. We will credit reporters in the release notes unless they prefer to remain anonymous.

## Scope

In scope: authentication and authorization bypass, session hijacking, unauthorized remote access, consent bypass, relay or end-to-end encryption weaknesses, privilege escalation through the agents, injection, brute-force protection bypass, update mechanism tampering.

Out of scope: vulnerabilities in third-party infrastructure you operate (nginx, Docker, OS) unless caused by our defaults or documentation; social engineering of end users.

## Misuse

Remote support tools are abused by scammers. If you see MKITS-SupportSuite used for fraud, contact the operator of the server in question; this project does not operate any public servers.
