# ADSyncDump 技术深度解析
**Language**: [English](../TECHNICAL_DETAILS.md) | **中文** | [Français](TECHNICAL_DETAILS.fr.md)

本文档完整、可验证地介绍ADSyncDump的底层原理、设计决策和实现权衡。所有结论均可通过源代码和公开的Azure AD Connect内部机制验证。

---

## 1. 背景：Azure AD Connect 架构
Azure AD Connect（AAD Connect）是微软官方的目录同步工具，部署在域加入服务器上，用于将本地Active Directory对象同步到Azure Active Directory。根据设计，它必须存储两套高权限凭证以执行双向同步：
1. **本地AD凭证**：随机生成的`MSOL_*`服务账号，被授予本地域的`DS-Replication-Get-Changes`和`DS-Replication-Get-Changes-All`权限（等效于DCSync权限，可导出域内所有哈希）
2. **Azure AD凭证**：目标`.onmicrosoft.com`租户中的服务账号，被授予等效全局管理员的目录同步角色，可完全接管Azure租户。

与凭证提取相关的AAD Connect核心组件：
| 组件 | 说明 |
|-----------|---------|
| 同步服务 | 以虚拟服务账号`NT SERVICE\ADSync`运行，进程为`miiserver.exe` |
| 配置存储 | SQL Server LocalDB（v1.x使用实例`ADSync`，v2.x/v3.x使用`ADSync2019`），所有连接器配置和加密凭证存储在`ADSync`数据库中 |
| 加密库 | 位于`C:\Program Files\Microsoft Azure AD Sync\Bin\`的`mcrypt.dll`，使用Windows数据保护API（DPAPI）实现凭证加解密 |
| 密钥存储 | 加密密钥存储在`mms_server_configuration`表中，通过DPAPI绑定到`NT SERVICE\ADSync`安全上下文保护 |

---

## 2. 核心加密原理
### 2.1 DPAPI隔离模型
Windows DPAPI（数据保护API）是AAD Connect凭证保护的信任根。DPAPI加密的blob在密码学上绑定到加密该数据的用户的安全上下文：
- 用户（或持有相同令牌的安全主体）可以解密自己的DPAPI blob
- 本地SYSTEM账号无法解密属于其他用户的DPAPI blob，包括虚拟服务账号
- 不存在硬编码主密钥；解密需要访问用户的密码派生主密钥，该密钥由系统LSA保护。

这是直接以SYSTEM权限运行解密代码会失败的根本原因：即使拥有完全管理员权限，进程也无法访问ADSync服务账号的DPAPI主密钥。

### 2.2 ADSync加密流程
AAD Connect凭证加密遵循`mcrypt.dll`中实现的固定、公开可验证的流程：
1. 首次启动时，KeyManager生成三部分密钥标识集：
   - `keySetId`：数字密钥ID（存储为32位无符号整数）
   - `instanceId`：AAD Connect安装的唯一GUID
   - `entropy`：作为额外加密熵的随机GUID
2. 这三个值以明文形式存储在`mms_server_configuration`表中，但用于解密凭证的实际对称密钥通过DPAPI加密到`NT SERVICE\ADSync`账号。
3. 解密凭证时：
   - 调用`KeyManager.LoadKeySet(entropy, instanceId, keySetId)`加载DPAPI加密的密钥材料
   - 调用`KeyManager.GetActiveCredentialKey()`初始化密钥存储（必需的未文档化初始化步骤）
   - 调用`KeyManager.GetKey(1, out key)`获取凭证解密密钥（密钥ID 1是所有AAD Connect版本通用的凭证密钥）
   - 调用`key.DecryptBase64ToString(encryptedBlob, out plaintext)`将base64编码的凭证blob解密为明文XML。

### 2.3 凭证存储
所有连接器凭证存储在`mms_management_agent`表中：
- `private_configuration_xml`：明文XML，包含连接器配置（域名、用户名、租户ID、连接设置）
- `encrypted_configuration`：base64编码的加密blob，包含敏感数据（密码、密钥）
- 每一行代表一个管理代理（连接器）：本地AD、Azure AD、LDAP、SQL、ADFS等。

---

## 3. 实现设计与权衡
ADSyncDump的每个设计决策都是针对真实AAD Connect部署迭代测试的结果，解决了公开工具和早期原型中发现的具体失败场景。

### 3.1 数据库连接层
**实现**：代码自动尝试连接`(localdb)\.\ADSync2019`和`(localdb)\.\ADSync`两个实例，使用集成Windows身份验证，连接超时5秒。
**设计原因**：
- 硬编码单个LocalDB实例会在不同AAD Connect版本上失败：v1.x使用`ADSync`实例，而v2.x（2019+）和v3.x将实例重命名为`ADSync2019`。自动回退确保所有版本兼容。
- 使用集成身份验证，因为本地管理员（和SYSTEM）默认拥有ADSync LocalDB的访问权限，不需要额外凭证。
- 5秒短超时避免在未安装AAD Connect的机器上长时间挂起，防止C2环境中长时间卡顿。
- 不执行任何数据库修改，所有查询均为只读，避免留下审计痕迹。

### 3.2 服务进程识别
**实现**：代码不枚举所有系统进程查找`miiserver.exe`，而是使用Windows服务控制管理器（SCM）API通过`QueryServiceStatusEx`直接查询`ADSync`服务的运行PID。
**设计原因**：
- 通过`CreateToolhelp32Snapshot`枚举进程名在不同环境中不可靠：
  - WOW64 32位进程无法正确枚举64位进程
  - 部分AAD Connect版本使用带版本号的进程名
  - 在部分锁定环境中进程枚举需要`SeDebugPrivilege`
- SCM查询100%可靠：服务名`ADSync`在所有AAD Connect版本中固定，无论进程名是什么都返回服务的确切运行PID。
- SCM查询除了标准本地管理员权限外不需要特殊权限，不会触发进程创建/访问遥测。

### 3.3 令牌模拟
**实现**：代码打开ADSync服务进程的句柄，通过`DuplicateToken`复制其主令牌，使用`WindowsIdentity.Impersonate()`在当前线程上模拟该令牌。解密完成后，恢复模拟上下文并关闭所有令牌句柄。
**设计原因**：
- 令牌模拟是无需窃取账号密码或创建新进程即可访问ADSync服务账号DPAPI密钥的唯一支持方法。
- 使用`DuplicateToken`（而非`DuplicateTokenEx`）创建模拟级令牌，足够用于DPAPI解密和DLL加载，不需要创建新进程主令牌所需的额外权限。
- 模拟范围仅限解密块：解密后在`finally`块中调用`Undo()`恢复原始进程令牌，遵循最小权限原则。
- 模拟期间不创建新进程，消除子进程遥测和会话隔离问题。

### 3.4 内存解密
**实现**：模拟ADSync服务令牌后，代码将当前工作目录和DLL搜索路径设置为AAD Connect Bin目录，然后通过反射将`mcrypt.dll`直接加载到当前进程中，调用KeyManager方法在内存中解密凭证。
**设计原因**：
- 该方案替代了常见的启动PowerShell子进程执行解密的模式，后者存在多个关键失败点：
  1. PowerShell子进程会触发AMSI扫描和脚本块日志，被所有现代EDR检测
  2. 父子进程间输出重定向在子进程输出缓冲区填满时容易死锁
  3. 虚拟服务账号的会话0隔离和缺失用户配置文件导致PowerShell无法正常启动
  4. 子进程创建明显的进程创建遥测（例如`notepad.exe → powershell.exe`是高优先级EDR检测规则）
- 显式设置`SetDllDirectory`和`Environment.CurrentDirectory`到AAD Connect Bin目录，解决了早期原型的关键失败：`mcrypt.dll`从自身目录加载依赖DLL和配置文件，如果进程工作目录是C2当前目录会抛出`FileNotFoundException`。
- `keyId`参数显式转换为`UInt32`：早期原型失败是因为`KeyManager.LoadKeySet`需要无符号32位整数，而SQL查询返回有符号`Int32`；这个类型不匹配在任何文档中都没有记载，会导致静默解密失败。
- 在获取解密密钥前调用未文档化的`GetActiveCredentialKey()`方法：这是解锁密钥存储所需的内部初始化步骤，省略该步骤会在获取密钥时抛出空引用异常。

### 3.5 AMSI绕过实现
**实现**：AMSI绕过默认关闭，仅在传入`--bypass-amsi`参数时启用。绕过通过反射修补`System.Management.Automation.AmsiUtils`中的`amsiInitFailed`标志，类型和字段名拆分为字符数组以避免静态字符串特征。
**设计原因**：
- 将AMSI绕过设为可选允许操作员根据目标环境选择：部分环境监控内存AMSI补丁，而其他环境会阻止未修补的程序集加载。
- 字符串拆分避免静态特征检测：二进制中不会出现连续的`AmsiUtils`和`amsiInitFailed`字符串，绕过静态特征扫描。
- 仅修补当前进程的AMSI；不执行系统范围或跨进程AMSI修改，减少持久化和检测面。
- `amsiInitFailed`补丁是跨PowerShell/.NET版本最稳定、兼容性最好的AMSI绕过，不需要修改可执行内存页（会触发内存完整性警报）。

### 3.6 C# 5兼容性
**实现**：所有代码仅使用C# 5.0语法，不使用C# 6+特性（字符串插值、null条件运算符、out变量、LINQ扩展方法），无外部NuGet依赖。
**设计原因**：
- .NET Framework 4.8（所有Windows 10/Server 2016+预装）自带的默认C#编译器（`csc.exe`）仅支持C# 5.0。限制为C# 5语法允许在任何原生Windows系统上编译，无需Visual Studio、Roslyn或额外SDK。
- 所有LINQ调用（例如`First()`）替换为显式`foreach`循环，避免需要引用`System.Core.dll`，消除编译和运行时依赖问题。
- 最终二进制仅依赖所有受支持Windows版本中存在的4个内置.NET Framework程序集：`mscorlib`、`System.Data`、`System.Xml`、`System.Security`。运行不需要额外DLL。

### 3.7 多连接器支持
**实现**：代码枚举`mms_management_agent`表中的所有行，而非仅过滤`ma_type='AD'`，通过类型和名称自动识别本地AD和Azure AD连接器，输出所有支持连接器的凭证。
**设计原因**：
- 大多数公开ADSync凭证提取工具仅获取本地AD的`MSOL_*`账号，忽略了价值更高的Azure AD同步凭证（可完全接管租户）。
- 自动类型检测无需用户配置：工具自动识别并标记凭证，包含权限说明供操作员参考。
- 枚举模型可在未来版本扩展支持其他连接器类型（LDAP、SQL、ADFS），无需修改核心逻辑。

---

## 4. 操作安全（OpSec）考虑
所有设计选择优先最小化检测面：
1. **无子进程派生**：所有操作完全在C2牺牲进程内完成，全程不创建任何新进程，消除进程创建遥测和父子进程检测规则。
2. **不使用xp_cmdshell**：工具不会在LocalDB实例上启用或使用`xp_cmdshell`，该功能会在SQL Server错误日志中留下清晰审计痕迹，被大多数SQL安全工具监控。所有数据库操作均为只读SELECT查询。
3. **无磁盘写入**：不向磁盘写入任何临时文件，所有解密在内存中完成。工具不修改注册表、创建服务或更改系统配置。
4. **最小句柄使用**：所有Win32句柄（进程、令牌、服务管理器）使用后立即显式关闭，避免句柄泄漏和EDR的句柄检查。
5. **无shellcode或反射DLL注入**：所有代码作为托管.NET运行，避免常见的内存注入检测规则。

---

## 5. 历史问题与解决方案
当前实现是解决开发过程中观察到的具体失败模式的结果：
| 失败模式 | 根本原因 | 解决方案 |
|--------------|------------|------------|
| 初始解密返回null/失败 | 以SYSTEM运行无法访问ADSync服务账号的DPAPI主密钥 | 实现ADSync服务进程的令牌模拟以访问正确的DPAPI上下文 |
| 进程枚举找不到`miiserver.exe` | 进程名枚举在不同版本和WOW64环境中不可靠 | 使用SCM API通过固定服务名直接查询ADSync服务PID |
| PowerShell子进程无限挂起 | 会话0隔离、AMSI阻止、输出缓冲区死锁导致子进程无法执行 | 完全消除子进程，通过反射在内存中执行解密 |
| KeyManager抛出`InvalidCastException` | `LoadKeySet`需要无符号`UInt32`密钥ID，而数据库返回有符号`Int32` | 调用KeyManager方法时将密钥ID显式转换为`uint` |
| `mcrypt.dll`加载后抛出`FileNotFoundException` | DLL从进程工作目录查找依赖配置文件，默认工作目录为C2工作目录 | 显式将工作目录和DLL搜索路径设置为AAD Connect Bin目录 |
| 默认系统csc编译失败 | 使用C# 6+语法和LINQ需要额外引用和更新的编译器 | 限制代码为C# 5语法，用显式循环替换LINQ，消除所有外部引用 |

---

## 6. 验证与确认
提取的凭证可独立验证有效性：
1. **本地AD凭证**：`MSOL_*`账号始终拥有目录复制权限。可使用`secretsdump.py -just-dc-user MSOL_<id> <DOMAIN>/<MSOL_user>:<password>@<domain_controller>`执行DCSync验证。
2. **Azure AD凭证**：同步账号在租户上拥有目录同步权限。可使用AADInternals的`Get-AADIntAccessTokenWithSyncCredentials`获取可执行租户管理的Azure AD访问令牌验证。
3. 工具不会产生假阳性：解密失败会显式报告为错误，失败时不会返回占位符或无效凭证。

---

## 7. 支持环境
- Azure AD Connect v1.x (2016)、v2.x (2019/2022)、v3.x（最新版）
- Windows Server 2016、2019、2022
- .NET Framework 4.5+（所有受支持Windows版本预装）
- C2框架：Sliver、Cobalt Strike、BruteRatel、Mythic以及所有支持`execute-assembly`的框架

---

## 8. 常见问题 (FAQ)

### 通过`execute-assembly`进行的内存反射加载会自动绕过AMSI吗？
**答：不会。** 通过`execute-assembly`进行的内存反射加载本身不能绕过AMSI，因为AMSI扫描运行在CLR（.NET运行时）层面，与程序集是从磁盘加载还是从内存加载无关：
1. **CLR集成的AMSI扫描**：从.NET Framework 4.8开始（Windows 10 1903+和Windows Server 2016+的默认运行时，也是所有现代Azure AD Connect版本的必需版本），公共语言运行时在加载**所有**.NET程序集时都会调用`AmsiScanBuffer`扫描程序集的IL字节码，与加载来源无关。该扫描发生在程序集入口点（`Main`方法）执行之前，对从磁盘加载的程序集和通过`Assembly.Load()`反射加载的程序集（所有`execute-assembly`实现使用的机制）同等生效。
2. **`execute-assembly`实际绕过的内容**：反射内存执行仅能规避基于磁盘的静态特征检测（即扫描写入磁盘的EXE文件），不会干预或禁用CLR层面的AMSI扫描——AMSI会直接检查进程内存中的程序集字节。牺牲进程派生（如`-p notepad.exe`）仅用于隔离执行，防止程序集崩溃或被检测时损坏beacon；不会禁用牺牲进程中的AMSI。
3. **`--bypass-amsi`参数的作用**：
   - ADSyncDump使用字符拆分字符串混淆，降低初始程序集加载时的静态特征检测概率，但EDR产品仍可能在执行期间标记敏感Win32 API调用（令牌模拟、SCM访问、加密解密例程）。
   - 内置的AMSI绕过在`Main`方法最开始通过反射patch `System.Management.Automation.AmsiUtils`中的`amsiInitFailed`标志，使当前进程中后续所有AMSI扫描请求直接返回干净结果。该补丁完全在内存中生效，不会修改磁盘上的系统文件。
   - 注意：该绕过不能规避程序集本身初始执行前的AMSI扫描，但可以中和工具执行期间的运行时AMSI遥测和扫描，在大多数红队场景下足够使用。
4. **环境边界情况**：.NET Framework 4.8之前的版本（如Windows Server 2012 R2及更早版本）未集成AMSI。在这些旧系统上，`--bypass-amsi`参数不需要，也不会产生任何效果。
