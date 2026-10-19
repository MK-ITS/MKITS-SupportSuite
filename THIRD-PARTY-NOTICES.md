# Third-party notices

MKITS-SupportSuite is licensed under AGPL-3.0-only. It uses the following third-party components.
Every dependency must be listed here with its license. Only AGPL-compatible licenses are allowed.

## Runtime dependencies

| Component | Used by | License | Source |
|---|---|---|---|
| .NET 10 runtime / ASP.NET Core | all .NET components | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/aspnetcore |
| ASP.NET Core Identity (EF Core stores) | server | MIT | https://github.com/dotnet/aspnetcore |
| Entity Framework Core + SQLite provider | server | MIT | https://github.com/dotnet/efcore |
| QRCoder | server (TOTP QR codes) | MIT | https://github.com/codebude/QRCoder |
| SQLite (via SQLitePCLRaw bundle) | server | Public Domain (SQLite), Apache-2.0 (SQLitePCLRaw) | https://sqlite.org, https://github.com/ericsink/SQLitePCL.raw |

## Development and test dependencies (not distributed)

| Component | Used by | License | Source |
|---|---|---|---|
| xUnit.net | tests | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | tests | MIT | https://github.com/microsoft/vstest |
| Microsoft.AspNetCore.Mvc.Testing | server tests | MIT | https://github.com/dotnet/aspnetcore |
| Microsoft.EntityFrameworkCore.Design | migration tooling (not shipped) | MIT | https://github.com/dotnet/efcore |

## Planned (to be confirmed when added)

| Component | Purpose | License |
|---|---|---|
| Avalonia | native viewer UI | MIT |
| xterm.js | terminal in browser viewer | MIT |
| OpenH264 | software H.264 encoding | BSD-2-Clause |
| Google Protocol Buffers | wire protocol | BSD-3-Clause |
