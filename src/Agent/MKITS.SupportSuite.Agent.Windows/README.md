# MKITS.SupportSuite.Agent.Windows

Windows client (Windows 10 x64+ / Server 2016+). Current state: **portable ad-hoc client** (Phase 8A).

| Folder | Content |
|---|---|
| `Program.cs` | entry point: server + code from the file name (`MKITS-Support_<host>[+port]_<code>.exe`, ADR-0019), otherwise a dialog; `--server <url> --code <code>` for development |
| `UI/` | WinForms windows: `MainForm` (always on top: status, supporters, security code, chat, *End session*), `ConsentForm` (Deny is the default button), `ConnectForm`; texts in `Resources/Strings*.resx` (EN/DE) |
| `Platform/` | `GdiScreenSource` (GDI capture + JPEG), `SendInputInjector` (scan codes, Unicode text, special keys) |
| `Elevation/` | administrator mode (ADR-0021): `ElevationInstaller` (`--elevate`, after UAC), `AdHocService` (`--service`, temporary LocalSystem service), `PrivilegedHelper` (`--helper`, SYSTEM in the console session, follows the input desktop), `HelperClient` (pipe client used by the session window) |
| `Native/` | P/Invoke declarations (`LibraryImport`, blittable structs only) |

Limitations of this stage (see WORKPLAN Phase 8): no DXGI/H.264 yet. Without administrator mode the secure desktop
(UAC prompts, lock screen) and elevated windows cannot be seen or controlled; *Restart with administrator rights*
enables them for the rest of the session (UAC prompt on the device).

## Build and try it

```powershell
dotnet publish src/Agent/MKITS.SupportSuite.Agent.Windows -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
# against a local development server (http allowed only with --server):
publish\MKITS-Support.exe --server http://localhost:5080 --code 12345678
```

CI builds the same EXE on every PR (artifact **MKITS-Support-windows-x64**). It is unsigned (ADR-0009), so
SmartScreen warns on first start.
