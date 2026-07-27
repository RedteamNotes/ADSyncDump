# ADSyncDump Technical Deep Dive
**Language**: **English** | [中文](docs/TECHNICAL_DETAILS.zh-CN.md) | [Français](docs/TECHNICAL_DETAILS.fr.md)

This document provides a complete, verifiable breakdown of ADSyncDump's underlying principles, design decisions, and implementation tradeoffs. All claims are directly verifiable against the source code and public Azure AD Connect internals.

---

## 1. Background: Azure AD Connect Architecture
Azure AD Connect (AAD Connect) is Microsoft's official directory synchronization tool, deployed on domain-joined servers to replicate on-premises Active Directory objects to Azure Active Directory. By design, it must store two sets of high-privilege credentials to perform bidirectional synchronization:
1. **On-premises AD credentials**: A randomly generated `MSOL_*` service account, granted `DS-Replication-Get-Changes` and `DS-Replication-Get-Changes-All` rights on the local domain (equivalent to DCSync privileges, capable of dumping all domain hashes)
2. **Azure AD credentials**: A service account in the target `.onmicrosoft.com` tenant, granted Global Administrator equivalent Directory Synchronization roles, capable of full Azure tenant takeover.

AAD Connect core components relevant to credential extraction:
| Component | Details |
|-----------|---------|
| Sync Service | Runs as virtual service account `NT SERVICE\ADSync`, hosted in `miiserver.exe` |
| Configuration Storage | SQL Server LocalDB (v1.x uses instance `ADSync`, v2.x/v3.x uses `ADSync2019`), stores all connector configurations and encrypted credentials in the `ADSync` database |
| Encryption Library | `mcrypt.dll` located in `C:\Program Files\Microsoft Azure AD Sync\Bin\`, implements credential encryption/decryption using Windows Data Protection API (DPAPI) |
| Key Storage | Encryption keys are stored in the `mms_server_configuration` table, protected by DPAPI bound to the `NT SERVICE\ADSync` security context |

---

## 2. Core Cryptographic Principles
### 2.1 DPAPI Isolation Model
Windows DPAPI (Data Protection API) is the root of trust for AAD Connect credential protection. DPAPI encrypted blobs are cryptographically bound to the security context of the user that encrypted them:
- A user (or security principal with an identical token) can decrypt their own DPAPI blobs
- The local SYSTEM account cannot decrypt DPAPI blobs belonging to other user accounts, including virtual service accounts
- No hardcoded master key exists; decryption requires access to the user's password-derived master key, protected by the system LSA.

This is the fundamental reason why running decryption code directly as SYSTEM will fail: even with full administrative rights, the process does not have access to the ADSync service account's DPAPI master keys.

### 2.2 ADSync Encryption Flow
AAD Connect credential encryption follows a fixed, publicly verifiable flow implemented in `mcrypt.dll`:
1. On first startup, the KeyManager generates a 3-part key identifier set:
   - `keySetId`: Numeric key ID (stored as 32-bit unsigned integer)
   - `instanceId`: GUID unique to the AAD Connect installation
   - `entropy`: Random GUID added as additional encryption entropy
2. These three values are stored in plaintext in the `mms_server_configuration` table, but the actual symmetric encryption key used to decrypt credentials is encrypted via DPAPI to the `NT SERVICE\ADSync` account.
3. When decrypting credentials:
   - Call `KeyManager.LoadKeySet(entropy, instanceId, keySetId)` to load the DPAPI-encrypted key material
   - Call `KeyManager.GetActiveCredentialKey()` to initialize the key store (required, undocumented initialization step)
   - Call `KeyManager.GetKey(1, out key)` to retrieve the credential decryption key (key ID 1 is the universal credential key across all AAD Connect versions)
   - Call `key.DecryptBase64ToString(encryptedBlob, out plaintext)` to decrypt the base64-encoded credential blob to plaintext XML.

### 2.3 Credential Storage
All connector credentials are stored in the `mms_management_agent` table:
- `private_configuration_xml`: Plaintext XML containing connector configuration (domain name, username, tenant ID, connection settings)
- `encrypted_configuration`: Base64-encoded encrypted blob containing sensitive data (passwords, secrets)
- Each row represents one management agent (connector): on-premises AD, Azure AD, LDAP, SQL, ADFS, etc.

---

## 3. Implementation Design & Rationale
Every design decision in ADSyncDump is the result of iterative testing against real AAD Connect deployments, solving concrete failure modes observed in public tools and early prototypes.

### 3.1 Database Connection Layer
**Implementation**: The code automatically attempts connection to both `(localdb)\.\ADSync2019` and `(localdb)\.\ADSync` instances, using integrated Windows authentication with a 5-second connection timeout.
**Rationale**:
- Hardcoding a single LocalDB instance fails across AAD Connect versions: v1.x uses the `ADSync` instance, while v2.x (2019+) and v3.x renamed the instance to `ADSync2019`. Automatic fallback ensures compatibility across all versions.
- Integrated authentication is used because local administrators (and SYSTEM) have default access to the ADSync LocalDB; no additional credentials are required.
- A short 5-second timeout prevents hanging on machines without AAD Connect installed, avoiding long stalls in C2 environments.
- No database modifications are performed; all queries are read-only to avoid leaving audit trails.

### 3.2 Service Process Identification
**Implementation**: Instead of enumerating all system processes to find `miiserver.exe`, the code uses the Windows Service Control Manager (SCM) API to directly query the running PID of the `ADSync` service via `QueryServiceStatusEx`.
**Rationale**:
- Process name enumeration via `CreateToolhelp32Snapshot` is unreliable across environments:
  - WOW64 32-bit processes fail to enumerate 64-bit processes correctly
  - Some AAD Connect builds use versioned process names
  - Process enumeration requires `SeDebugPrivilege` in some locked-down environments
- SCM queries are 100% reliable: the service name `ADSync` is fixed across all AAD Connect versions, and returns the exact running PID of the service regardless of process name.
- SCM queries require no special privileges beyond standard local administrator rights, and do not trigger process creation/access telemetry.

### 3.3 Token Impersonation
**Implementation**: The code opens a handle to the ADSync service process, duplicates its primary token via `DuplicateToken`, and impersonates the token on the current thread using `WindowsIdentity.Impersonate()`. After decryption completes, the impersonation context is reverted and all token handles are closed.
**Rationale**:
- Token impersonation is the only supported way to access the ADSync service account's DPAPI keys without stealing the account's password or creating a new process.
- `DuplicateToken` (not `DuplicateTokenEx`) is used to create an impersonation-level token, which is sufficient for DPAPI decryption and DLL loading, without requiring the additional permissions needed to create a primary token for new processes.
- Impersonation is scoped only to the decryption block: after decryption, `Undo()` is called in a `finally` block to revert to the original process token, following least-privilege principles.
- No new processes are created during impersonation, eliminating child process telemetry and session isolation issues.

### 3.4 In-Memory Decryption
**Implementation**: After impersonating the ADSync service token, the code sets the current working directory and DLL search path to the AAD Connect Bin directory, then uses reflection to load `mcrypt.dll` directly into the current process and invoke the KeyManager methods to decrypt credentials in-memory.
**Rationale**:
- This approach replaces the common pattern of spawning a PowerShell child process to perform decryption, which has multiple critical failure modes:
  1. PowerShell child processes trigger AMSI scanning and script block logging, which is detected by all modern EDRs
  2. Output redirection between parent and child processes is prone to deadlocks when the child process's output buffer fills
  3. Session 0 isolation and missing user profiles for the virtual service account cause PowerShell to fail to start correctly
  4. Child processes create obvious process creation telemetry (e.g., `notepad.exe → powershell.exe` is a high-priority EDR detection rule)
- `SetDllDirectory` and `Environment.CurrentDirectory` are explicitly set to the AAD Connect Bin directory to solve a critical early-prototype failure: `mcrypt.dll` loads dependent DLLs and configuration files from its own directory, and will throw `FileNotFoundException` if the process working directory is set to the C2 current directory.
- The `keyId` parameter is explicitly cast to `UInt32`: an early prototype failed because `KeyManager.LoadKeySet` expects an unsigned 32-bit integer, while the SQL query returns a signed `Int32`; this type mismatch is not documented anywhere and causes silent decryption failure.
- The undocumented `GetActiveCredentialKey()` method is called before retrieving the decryption key: this internal initialization step is required to unlock the key store, and omitting it causes null reference exceptions when retrieving keys.

### 3.5 AMSI Bypass Implementation
**Implementation**: AMSI bypass is disabled by default, enabled only when the `--bypass-amsi` flag is passed. The bypass patches the `amsiInitFailed` flag in `System.Management.Automation.AmsiUtils` via reflection, with type and field names split into character arrays to avoid static string signatures.
**Rationale**:
- Making AMSI bypass optional allows operators to choose based on target environment: some environments monitor for in-memory AMSI patches, while others block unpatched assembly loading.
- String splitting avoids static signature detection: the full strings `AmsiUtils` and `amsiInitFailed` never appear as contiguous literals in the binary, defeating static signature scans.
- Only the current process's AMSI is patched; no system-wide or cross-process AMSI modifications are performed, reducing persistence and detection surface.
- The `amsiInitFailed` patch is the most stable and compatible AMSI bypass across PowerShell/.NET versions, and does not require modifying executable memory pages (which triggers memory integrity alerts).

### 3.6 C# 5 Compatibility
**Implementation**: All code uses only C# 5.0 syntax, with no C# 6+ features (string interpolation, null-conditional operators, out variables, LINQ extension methods) and no external NuGet dependencies.
**Rationale**:
- The default C# compiler (`csc.exe`) included with .NET Framework 4.8 (preinstalled on all Windows 10/Server 2016+ systems) only supports C# 5.0. Restricting syntax to C# 5 allows compilation on any stock Windows system without Visual Studio, Roslyn, or additional SDKs.
- All LINQ calls (e.g., `First()`) are replaced with explicit `foreach` loops to avoid requiring a reference to `System.Core.dll`, eliminating compilation and runtime dependency issues.
- The final binary only depends on 4 built-in .NET Framework assemblies present on all supported Windows versions: `mscorlib`, `System.Data`, `System.Xml`, and `System.Security`. No external DLLs are required to run.

### 3.7 Multi-Connector Support
**Implementation**: The code enumerates all rows in `mms_management_agent` instead of filtering to only `ma_type='AD'`, automatically identifying on-premises AD and Azure AD connectors by type and name, and outputting credentials for all supported connectors.
**Rationale**:
- Most public ADSync credential extraction tools only retrieve the on-premises AD `MSOL_*` account, ignoring the higher-value Azure AD sync credential which allows full tenant takeover.
- Automatic type detection removes the need for user configuration: the tool correctly identifies and labels credentials, including permission notes for operator context.
- The enumeration model is extensible to additional connector types (LDAP, SQL, ADFS) in future versions without core logic changes.

---

## 4. Operational Security (OpSec) Considerations
All design choices prioritize minimal detection surface:
1. **No child process spawning**: All operations occur entirely within the sacrificial C2 process. No new processes are created at any point, eliminating process creation telemetry and parent-child detection rules.
2. **No xp_cmdshell usage**: The tool never enables or uses `xp_cmdshell` on the LocalDB instance, which leaves clear audit logs in SQL Server error logs and is monitored by most SQL security tools. All database operations are read-only SELECT queries.
3. **No disk writes**: No temporary files are written to disk; all decryption occurs in memory. The tool does not modify the registry, create services, or alter system configuration.
4. **Minimal handle usage**: All Win32 handles (process, token, service manager) are explicitly closed immediately after use to avoid handle leaks and handle inspection by EDRs.
5. **No shellcode or reflective DLL injection**: All code runs as managed .NET, avoiding common memory injection detection rules.

---

## 5. Historical Pitfalls & Resolutions
The current implementation is the result of resolving concrete failure modes observed during development:
| Failure Mode | Root Cause | Resolution |
|--------------|------------|------------|
| Initial decryption returns null/failure | Running as SYSTEM does not grant access to the ADSync service account's DPAPI master keys | Implement token impersonation of the ADSync service process to access the correct DPAPI context |
| Process enumeration cannot find `miiserver.exe` | Process name enumeration is unreliable across versions and WOW64 environments | Use SCM API to directly query the ADSync service PID by fixed service name |
| PowerShell child process hangs indefinitely | Session 0 isolation, AMSI blocking, and output buffer deadlocks prevent child process execution | Eliminate child processes entirely, perform decryption in-memory via reflection |
| KeyManager throws `InvalidCastException` | `LoadKeySet` expects an unsigned `UInt32` key ID, while the database returns a signed `Int32` | Explicitly cast key ID to `uint` when calling KeyManager methods |
| `mcrypt.dll` throws `FileNotFoundException` after load | The DLL looks for dependent configuration files in the process working directory, which defaults to the C2 working directory | Explicitly set the working directory and DLL search path to the AAD Connect Bin directory |
| Compilation fails on default system csc | Use of C# 6+ syntax and LINQ requires additional references and newer compilers | Restrict code to C# 5 syntax, replace LINQ with explicit loops, eliminate all external references |

---

## 6. Verification & Validation
Extracted credentials can be independently verified as valid:
1. **On-premises AD credentials**: The `MSOL_*` account will always have domain replication rights. Validate with `secretsdump.py -just-dc-user MSOL_<id> <DOMAIN>/<MSOL_user>:<password>@<domain_controller>` to perform DCSync.
2. **Azure AD credentials**: The sync account will have Directory Synchronization rights on the tenant. Validate with `Get-AADIntAccessTokenWithSyncCredentials` from AADInternals to retrieve an Azure AD access token capable of tenant administration.
3. The tool does not produce false positives: decryption failures are explicitly reported as errors, and no placeholder or invalid credentials are returned on failure.

---

## 7. Supported Environments
- Azure AD Connect v1.x (2016), v2.x (2019/2022), v3.x (current)
- Windows Server 2016, 2019, 2022
- .NET Framework 4.5+ (preinstalled on all supported Windows versions)
- C2 frameworks: Sliver, Cobalt Strike, BruteRatel, Mythic, and all other frameworks supporting `execute-assembly`

---

## 8. Frequently Asked Questions (FAQ)

### Q: Does reflective in-memory loading via `execute-assembly` automatically bypass AMSI?
**A: No.** In-memory reflective loading via `execute-assembly` does not inherently bypass AMSI, as AMSI scanning operates at the CLR (.NET Runtime) level independent of whether an assembly is loaded from disk or memory:
1. **CLR-integrated AMSI scanning**: Starting with .NET Framework 4.8 (the default runtime on Windows 10 1903+ and Windows Server 2016+, required for all modern Azure AD Connect versions), the Common Language Runtime invokes `AmsiScanBuffer` to scan the IL bytecode of *all* .NET assemblies during load, regardless of load source. This scan occurs before the assembly entry point (`Main` method) executes, and applies equally to disk-loaded assemblies and assemblies loaded reflectively via `Assembly.Load()` (the mechanism used by all `execute-assembly` implementations).
2. **What `execute-assembly` actually bypasses**: Reflective in-memory execution only evades disk-based static signature detection (i.e., scanning of EXE files written to disk). It does not interact with or disable CLR-level AMSI scanning, which inspects assembly bytes directly in process memory. Sacrificial process spawning (e.g., `-p notepad.exe`) only isolates execution to prevent beacon corruption if the assembly crashes or is detected; it does not disable AMSI in the sacrificial process.
3. **Purpose of the `--bypass-amsi` flag**:
   - ADSyncDump uses character-split string obfuscation to reduce static signature detection during initial assembly load, but EDR products may still flag sensitive Win32 API calls (token impersonation, SCM access, cryptographic decryption routines) during execution.
   - The included AMSI bypass patches the `amsiInitFailed` flag in `System.Management.Automation.AmsiUtils` via reflection at the very start of `Main`, causing all subsequent AMSI scan requests in the current process to return a clean result. This patch is entirely in-memory and does not modify system files on disk.
   - Note: This bypass does not evade initial pre-execution AMSI scanning of the assembly itself, but neutralizes runtime AMSI telemetry and scanning during tool execution, which is sufficient for most red team environments.
4. **Environment edge case**: AMSI is not integrated into .NET Framework versions prior to 4.8 (e.g., Windows Server 2012 R2 and older). On these legacy systems, the `--bypass-amsi` flag is unnecessary and has no effect.
