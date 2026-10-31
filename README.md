<p align="center">
  <img src="src/WinBox.App/Assets/Icon/AppIcon.svg" width="160" alt="WinBox 图标">
</p>

<h1 align="center">WinBox</h1>

把 Windows 软件装进"盒子"里运行：把安装包拖进 WinBox，软件被装进一个独立的盒子，运行时不往系统里乱写东西；删除盒子时，程序、数据和它在系统里留下的记录一起清除。

WinBox 是 [MacBox](https://github.com/PykeDLing/macbox) 的 Windows 版。

> **早期预览，开发中。** 目前只在一台 Windows 11 25H2 上验证过，第一个支持的软件是夸克网盘。

## 系统要求

- Windows 11 25H2（build 26200.9278 或更高），x64。MXC 的 ProcessContainer 需要这个版本。
- 打开**开发者模式**（设置 > 系统 > 开发者选项）。盒子的应用包目前未签名，只能以开发模式注册。
- 安装 [.NET 10 运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（x64，选".NET 运行时"即可）。WinBox 和它放进每个盒子的 BoxHost、BoxShim 都用系统里的 .NET 运行。
- 以管理员身份运行一次 `wxc-host-prep prepare-system-drive`，为沙盒准备系统盘的访问权限。这个工具在 [MXC 发布页](https://github.com/microsoft/mxc/releases)的 `mxc-release-binaries.zip` 里（`bin\x64\wxc-host-prep.exe`）。

## 下载和安装

1. 先满足上面的系统要求。
2. 从 [Releases](https://github.com/PykeDLing/winbox/releases) 下载 `WinBox-<版本>-win-x64.zip`，解压到一个**路径短、当前用户可写**的位置，例如 `D:\WinBox`。
   - 盒子和它们的数据都放在 WinBox 文件夹下的 `Data\` 里，所以不要放进 `C:\Program Files`。
   - 有些软件的文件在盒子里很深（夸克网盘有 140 多个字符），WinBox 所在的路径太长时，软件运行时会超出 Windows 260 个字符的路径长度限制。WinBox 文件夹的路径最好不超过 50 个字符。
3. 运行 `WinBox.App.exe`。程序没有签名，第一次运行时 Windows 可能提示"Windows 已保护你的电脑"，点"更多信息"，再点"仍要运行"。

发布包自带 [innoextract](https://constexpr.org/innoextract/)（`tools\`），不用另外安装。卸载 WinBox：先在 WinBox 里删除所有盒子，再删除 WinBox 文件夹。

## 使用

- **添加软件**：把安装包拖进窗口，或点"添加安装包…"。WinBox 从安装包里取出程序，不运行安装程序。
- **打开 / 退出**：点卡片上的"打开"，软件在盒子里运行。点"退出"时，WinBox 像 Windows 注销时那样请软件自己保存并退出，再把数据保存回盒子。
- **显示窗口**：软件在盒子里显示不了托盘图标。关窗口后缩到托盘的软件，用卡片上的"显示窗口"找回。
- **在盒外打开**：盒子里的软件自己"打开文件""打开所在文件夹"会被沙盒拒绝。卡片的 ⋯ 菜单可以打开授权的文件夹，或者用这台电脑上的程序打开一个文件。
- **删除**：程序、数据、应用包和 Windows 记下的使用痕迹一起清除。
- **关闭 WinBox**：
  - 没有盒子在运行时，直接退出。
  - 有盒子在运行时，第一次会问：最小化到托盘，还是退出 WinBox 并关闭所有盒子。可以记住选择，之后在托盘菜单里修改。
  - 在托盘里时，点图标打开窗口；托盘菜单里可以找回或退出每个盒子，也可以退出 WinBox。退出 WinBox 会先确认，再让每个盒子保存数据并退出。
  - 最后一个盒子结束后，WinBox 自己退出；如果有需要你处理的问题，就重新显示窗口。
  - WinBox 在托盘里时再次启动它，会切换到已经在运行的那个。

## 工作原理

每个盒子由两层隔离组成：

| 层 | 作用 |
|---|---|
| **MSIX 应用包**（每个盒子一个） | Windows 把软件在 AppData 下新建的文件、对 HKCU 注册表的写入，重定向到这个包的私有存储。注销应用包时，系统一并删除这些数据。 |
| **MXC 沙盒**（[Microsoft.Mxc.Sdk](https://www.nuget.org/packages/Microsoft.Mxc.Sdk) 的 ProcessContainer） | 默认拒绝访问。软件只能读写盒子自己的目录、用户的个人文件夹（桌面、文档、下载等）和 D:、E: 这类非系统盘；非系统盘上已经安装的程序（按卸载信息和 App Paths 找出的安装目录）一律拒绝访问。软件修改不了系统里已有的配置。 |

此外：

- **导入时不运行安装程序**：WinBox 直接从安装包里取出程序（Inno Setup 安装包用 innoextract 解开）。在线安装器（"迷你包"）通过配方找到完整安装包，并校验签名。
- **BoxShim**：沙盒里启动的第一个进程。沙盒的 LocalAppData 每次运行后会被系统清空，BoxShim 在启动前把上次的数据恢复进去，在软件退出或 Windows 关机时再保存回盒子。保存是原子的，中途断电也不会留下半新半旧的数据。它还负责：
  - **退出**：像 Windows 注销时那样请软件自己保存并退出（关窗口只会缩到托盘的软件也会真正退出）；10 秒内没退出的部分才被结束。
  - **显示窗口**：托盘图标在沙盒里出不来，软件关窗口后，WinBox 让 BoxShim 在沙盒里再启动一次软件，单实例的软件就会把窗口找回来。
- **BoxHost**：运行在每个盒子的应用包里，由系统启动，不是 WinBox 的子进程。它启动沙盒，退出时等 BoxShim 保存完数据；Windows 关机或注销时，它最先收到通知，先保存再放行。
- **本机守卫**：检查注册表、开机启动、快捷方式和计划任务里有没有指向盒子的条目。这类条目会让盒子里的程序绕过沙盒运行，发现后提示用户，并可以先备份再清除。删除盒子时也用它清掉 Windows 记下的使用痕迹：指向盒子路径的（含 UserAssist 里 ROT13 编码的程序记录），以及 Windows 按盒子应用包名记下、在应用包注销后仍留着的记录。备份只保存被删除的值。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/)，不需要 Visual Studio。导入 Inno Setup 安装包需要 innoextract：放在 PATH 里，或用环境变量 `WINBOX_INNOEXTRACT` 指定位置。

在仓库根目录：

```powershell
dotnet build
dotnet test
```

开发时数据放在仓库下的 `.data\`（相当于安装目录里的 `Data\`）。

图形界面：

```powershell
.\scripts\app.ps1
```

命令行（和界面使用同一套代码）：

```powershell
.\scripts\winbox.ps1 info                  # 沙盒、应用包、本机守卫的状态
.\scripts\winbox.ps1 import <安装包>        # 导入，并为盒子注册应用包
.\scripts\winbox.ps1 list
.\scripts\winbox.ps1 run <盒子 id 或名称>
.\scripts\winbox.ps1 show <盒子>           # 找回软件被关掉的窗口
.\scripts\winbox.ps1 stop <盒子>           # 退出：先请软件自己保存并退出，必要时结束进程，数据保存回盒子
.\scripts\winbox.ps1 remove <盒子>         # 删除盒子及它在系统里的痕迹
.\scripts\winbox.ps1 guard [--fix]         # 检查（并清除）指向盒子的本机设置，以及已删除盒子留下的 Windows 记录
```

发布包：

```powershell
.\scripts\release.ps1                      # 生成 artifacts\release\WinBox-<版本>-win-x64.zip 和它的 SHA-256
```

版本号在 `Directory.Build.props` 里。

WinBox 必须在没有应用包身份的进程里运行，否则 MXC 无法启动沙盒。请从资源管理器或普通终端启动，不要从其他 MSIX 应用（包括某些 IDE 或 AI 工具的内置终端）里启动。

## 项目结构

| 项目 | 内容 |
|---|---|
| `src/WinBox.Core` | 平台无关的逻辑：盒子模型、存储、导入管线、配方、沙盒策略、数据同步、本机守卫规则 |
| `src/WinBox.Platform` | Windows 实现：MXC 沙盒、应用包注册与激活、innoextract、签名检查、本机守卫扫描 |
| `src/WinBox.BoxHost` | 运行在每个盒子的应用包里，负责启动沙盒 |
| `src/WinBox.BoxShim` | 沙盒里的第一个进程，负责恢复和保存 LocalAppData |
| `src/WinBox.App` | 图形界面，WinUI 3 + [Reactor](https://github.com/microsoft/microsoft-ui-reactor)，非打包运行 |
| `src/WinBox.Cli` | 开发用命令行 |
| `tests/WinBox.Core.Tests` | 单元测试 |

## 已知限制

- 盒子的应用包未签名，需要开发者模式；普通用户使用之前要先解决签名问题。WinBox 本身的程序也没有签名。
- 目前只支持 Inno Setup 安装包，在线安装器只有夸克网盘有配方。
- 软件自带的自动更新、右键菜单、托盘图标在盒子里不可用。软件关窗口后缩到托盘的，用 WinBox 卡片上的"显示窗口"找回。
- Windows 的应用列表备份（`AppListBackup`）会记下盒子的应用包名。这个值和其他应用共用，删除盒子时不改动它。
- 非系统盘按"盘根 + 每个顶层文件夹"授权（MXC 对盘根的授权不覆盖子文件夹）。软件运行中在盘根新建的文件夹，要到下次打开盒子才能写入。
- 基于 Chromium 的软件要关掉自带的内层沙盒（`--no-sandbox`），由 MXC 负责隔离。
- 断电或蓝屏时，这一次运行期间的数据改动会丢失，上一次保存的数据不受影响。

## 许可证

[MIT](LICENSE)

发布包里的 `tools\innoextract.exe` 来自 [innoextract](https://constexpr.org/innoextract/)，按它自己的许可证（zlib 许可证）分发；它和它链接的库的许可证文本在 `tools\innoextract-license\` 里。
