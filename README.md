# WinBox

把 Windows 软件装进"盒子"里运行：把安装包拖进 WinBox，软件被装进一个独立的盒子，运行时不往系统里乱写东西；删除盒子时，程序、数据和它在系统里留下的记录一起清除。

WinBox 是 [MacBox](https://github.com/PykeDLing/macbox) 的 Windows 版。

> **早期预览，开发中。** 目前只在一台 Windows 11 25H2 上验证过，第一个支持的软件是夸克网盘。"WinBox"是开发代号，和 MikroTik 的 WinBox 重名，正式发布前会改名。

## 工作原理

每个盒子由两层隔离组成：

| 层 | 作用 |
|---|---|
| **MSIX 应用包**（每个盒子一个） | Windows 把软件在 AppData 下新建的文件、对 HKCU 注册表的写入，重定向到这个包的私有存储。注销应用包时，系统一并删除这些数据。 |
| **MXC 沙盒**（[Microsoft.Mxc.Sdk](https://www.nuget.org/packages/Microsoft.Mxc.Sdk) 的 ProcessContainer） | 默认拒绝访问。软件只能读写盒子自己的目录和用户的个人文件夹（桌面、文档、下载等），修改不了系统里已有的配置。 |

此外：

- **导入时不运行安装程序**：WinBox 直接从安装包里取出程序（Inno Setup 安装包用 innoextract 解开）。在线安装器（"迷你包"）通过配方找到完整安装包，并校验签名。
- **BoxShim**：沙盒里启动的第一个进程。沙盒的 LocalAppData 每次运行后会被系统清空，BoxShim 在启动前把上次的数据恢复进去，在软件退出、被强制退出或 Windows 关机时再保存回盒子。保存是原子的，中途断电也不会留下半新半旧的数据。
- **本机守卫**：检查注册表、开机启动、快捷方式和计划任务里有没有指向盒子的条目。这类条目会让盒子里的程序绕过沙盒运行，发现后提示用户，并可以先备份再清除。删除盒子时也用它清掉 Windows 记下的使用痕迹。

## 系统要求

- Windows 11 25H2（build 26200.9278 或更高），x64。MXC 的 ProcessContainer 需要这个版本。
- 打开**开发者模式**（设置 > 系统 > 开发者选项）。盒子的应用包目前未签名，只能以开发模式注册。
- 以管理员身份运行一次 MXC 发布包里的 `wxc-host-prep prepare-system-drive`，为沙盒准备系统盘的访问权限。
- 构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/)，不需要 Visual Studio。
- 导入 Inno Setup 安装包需要 [innoextract](https://constexpr.org/innoextract/)：放在 PATH 里，或用环境变量 `WINBOX_INNOEXTRACT` 指定位置。

## 构建和运行

```powershell
dotnet build WinBox.slnx
dotnet test --project tests\WinBox.Core.Tests\WinBox.Core.Tests.csproj
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
.\scripts\winbox.ps1 stop <盒子>           # 强制退出，数据仍会保存
.\scripts\winbox.ps1 remove <盒子>         # 删除盒子及它在系统里的痕迹
.\scripts\winbox.ps1 guard [--fix]         # 检查（并清除）指向盒子的本机设置
```

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

- 盒子的应用包未签名，需要开发者模式；普通用户使用之前要先解决签名问题。
- 目前只支持 Inno Setup 安装包，在线安装器只有夸克网盘有配方。
- 软件自带的自动更新、右键菜单、托盘图标在盒子里不可用。
- 基于 Chromium 的软件要关掉自带的内层沙盒（`--no-sandbox`），由 MXC 负责隔离。
- 断电或蓝屏时，这一次运行期间的数据改动会丢失，上一次保存的数据不受影响。

## 许可证

[MIT](LICENSE)
