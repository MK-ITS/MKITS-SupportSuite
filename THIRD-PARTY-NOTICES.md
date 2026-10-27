# Third-party notices

MKITS-SupportSuite is licensed under AGPL-3.0-only. It uses the following third-party components.
Every dependency must be listed here with its license. Only AGPL-compatible licenses are allowed.

## Runtime dependencies

| Component | Used by | License | Source |
|---|---|---|---|
| .NET 10 runtime / ASP.NET Core | all .NET components | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/aspnetcore |
| ASP.NET Core Identity (EF Core stores) | server | MIT | https://github.com/dotnet/aspnetcore |
| Entity Framework Core + SQLite provider | server | MIT | https://github.com/dotnet/efcore |
| Google.Protobuf | common (wire protocol), server, agents | BSD-3-Clause | https://github.com/protocolbuffers/protobuf |
| @bufbuild/protobuf (Protobuf-ES runtime, bundled into viewer.js) | browser viewer | Apache-2.0 | https://github.com/bufbuild/protobuf-es |
| xterm.js (@xterm/xterm, @xterm/addon-fit; bundled into viewer.js/viewer.css) | browser viewer terminal | MIT | https://github.com/xtermjs/xterm.js |
| System.ServiceProcess.ServiceController | Windows agent (temporary service) | MIT | https://github.com/dotnet/runtime |
| MailKit / MimeKit | server (SMTP notifications) | MIT | https://github.com/jstedfast/MailKit |
| QRCoder | server (TOTP QR codes) | MIT | https://github.com/codebude/QRCoder |
| Avalonia (incl. Avalonia.Desktop, Avalonia.Themes.Fluent) | Linux agent UI | MIT | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp, HarfBuzzSharp (via Avalonia, native libraries bundled) | Linux agent UI | MIT | https://github.com/mono/SkiaSharp |
| Tmds.DBus.Protocol | Linux agent (xdg-desktop-portal on Wayland; also used by Avalonia) | MIT | https://github.com/tmds/Tmds.DBus |
| libjpeg-turbo (TurboJPEG library, bundled into the Linux AppImage) | Linux agent (JPEG encoding) | IJG / BSD-3-Clause / zlib | https://libjpeg-turbo.org |
| SQLite (via SQLitePCLRaw bundle) | server | Public Domain (SQLite), Apache-2.0 (SQLitePCLRaw) | https://sqlite.org, https://github.com/ericsink/SQLitePCL.raw |

## Development and test dependencies (not distributed)

| Component | Used by | License | Source |
|---|---|---|---|
| xUnit.net | tests | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk | tests | MIT | https://github.com/microsoft/vstest |
| Microsoft.AspNetCore.Mvc.Testing | server tests | MIT | https://github.com/dotnet/aspnetcore |
| Microsoft.EntityFrameworkCore.Design | migration tooling (not shipped) | MIT | https://github.com/dotnet/efcore |
| Grpc.Tools (protoc code generator) | build of common (not shipped) | Apache-2.0 | https://github.com/grpc/grpc |
| @bufbuild/buf, @bufbuild/protoc-gen-es | browser viewer code generation | Apache-2.0 | https://github.com/bufbuild/buf, https://github.com/bufbuild/protobuf-es |
| TypeScript | browser viewer build | Apache-2.0 | https://github.com/microsoft/TypeScript |
| esbuild | browser viewer bundling | MIT | https://github.com/evanw/esbuild |
| Vitest | browser viewer tests | MIT | https://github.com/vitest-dev/vitest |
| happy-dom | browser viewer tests (DOM) | MIT | https://github.com/capricorn86/happy-dom |

## Planned (to be confirmed when added)

| Component | Purpose | License |
|---|---|---|
| Avalonia | native viewer UI | MIT |
| xterm.js | terminal in browser viewer | MIT |
| OpenH264 | software H.264 encoding | BSD-2-Clause |
| Google Protocol Buffers | wire protocol | BSD-3-Clause |
