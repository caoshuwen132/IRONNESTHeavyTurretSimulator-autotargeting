# IronNestFCS

> 安装修改版请先阅读：[图文安装说明](图文安装说明.md)。教程包含 MelonLoader、原作者版本、自动索敌 DLL 替换，以及 GitHub 依赖下载失败的处理方法。

玩家发布包提供离线单文件安装器 `IronNestFCS-单文件安装器.exe`。MelonLoader、UnityDependencies 6000.3.9/6000.3.21、Cpp2IL 和 Mod DLL 全部嵌入 EXE，不依赖 PowerShell、CMD 脚本或外部 `payload` 文件夹。安装时还会开启 MelonLoader 强制离线生成，避免 RemoteAPI SSL 连接失败红字。同一个程序内同时提供安装和完全卸载；完全卸载会清除 MelonLoader、全部 Mod、配置和安装备份，只保留游戏本体。

[Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/) 的 [MelonLoader](https://melonwiki.xyz/) Mod，为游戏中的重型炮塔加入一套自动化**火控系统（Fire Control System, FCS）**：在地图上点选目标，Mod 会自动解算弹道、采购/装填炮弹、调整炮塔方向与仰角，并完成确认与击发的全套流程。

> 使用 IL2CPP + MelonLoader；宿主同时兼容游戏 1577 更新前后带冒号和不带冒号的产品名，支持正式版与 Demo。

## 功能

- **游戏 1577 兼容**：同时声明更新前后的游戏产品名，避免 MelonLoader 因名称不匹配显示 `0 Mods loaded`。
- **一键打击**：点击地图上的炮兵目标（T1~T4），自动为其下达一次完整的打击任务。
- **双炮管任务调度**：任务进入队列后由调度器自动派给空闲炮管，一管炮打完一发自动拉取下一个任务，两管炮并行作业。
- **自动弹道解算**：读取目标的方向角与距离，自动设定装药、弹种并解算所需仰角。
- **多弹种支持**：AP / HCHE / HE / STAR / SMK，可在面板上选择当前弹种；弹仓缺弹时自动到采购台购买。
- **自动击发（可选）**：通过面板上的 `Auto Fire` 开关切换是手动还是自动完成最后的击发动作。
- **状态面板**：IMGUI 窗口实时显示两管炮的当前任务、目标参数与待派发任务数。
- **热重载开发**：火控逻辑独立成可卸载的程序集，开发时改完代码按 **F9** 即可在不重启游戏的情况下重新加载。
- **局内自由视角**：按 **F10** 开关自由视角，支持鼠标转向、WASD 飞行、空格/C 升降以及 Shift 加速。
- **碰撞箱显示**：按 **F11** 独立开关碰撞箱线框；普通碰撞为青绿色，触发区域为黄色，正常视角和自由视角均可使用。
- **自定义唱片机**（附带的独立 Mod）：把 `UserData/CustomRecords/` 下的音频文件（`.mp3` / `.wav` / `.flac`，封面取同名图片或内嵌封面）自动克隆成场景内的 RecordDisk，换上合成封面与音轨。

## 架构

工程拆分为四个程序集，核心是为**热重载**服务的宿主 / 逻辑分离设计：

| 项目 | 角色 | 说明 |
| --- | --- | --- |
| `IronNestFCS` | **宿主 Mod** | 稳定加载、永不重载。负责首次加载 Logic、监听 F9 触发热重载、转发生命周期回调。 |
| `IronNestFCS.Abstractions` | **契约** | 仅含 `IFcsModule` 接口。只加载一份，是唯一能安全跨 `AssemblyLoadContext` 边界传递的类型。 |
| `IronNestFCS.Logic` | **火控逻辑** | 所有高频改动的火控代码：弹道解算、任务调度、炮塔/炮管操控、UI。被装进可回收的 ALC，按 F9 卸载并重载。 |
| `IronNestFCS.CustomRecords` | **独立 Mod** | 与火控无关的场景装饰，扫描 `UserData/CustomRecords/` 下的音频文件，为每个文件克隆一张 RecordDisk 并替换音轨与封面。 |

热重载的关键点：Logic 程序集从内存字节加载（不锁住磁盘 dll），装进 `isCollectible` 的 `AssemblyLoadContext`；重载时先 `Shutdown`（撤销 Harmony 补丁、停止协程、清空 IL2CPP 引用）再卸载旧 ALC，最后从磁盘重新加载新版本。详见 [LogicReloader.cs](IronNestFCS/LogicReloader.cs) 与 [FSC.cs](IronNestFCS.Logic/FSC.cs) 中的注释。

## 构建与安装

### 前置条件

- 已安装 **.NET 6 SDK**（见 [global.json](global.json)）。
- 游戏本体，并已为其安装 **MelonLoader**（IL2CPP）。

### 配置游戏路径

各 `.csproj` 通过 `GameDir` 属性定位游戏目录下的 MelonLoader 程序集。请把以下三个文件里的 `GameDir` 改成你本机的游戏安装路径：

- [IronNestFCS/IronNestFCS.csproj](IronNestFCS/IronNestFCS.csproj)
- [IronNestFCS.Logic/IronNestFCS.Logic.csproj](IronNestFCS.Logic/IronNestFCS.Logic.csproj)
- [IronNestFCS.CustomRecords/IronNestFCS.CustomRecords.csproj](IronNestFCS.CustomRecords/IronNestFCS.CustomRecords.csproj)

```xml
<GameDir>你的路径\Iron Nest Heavy Turret Simulator</GameDir>
```

### 构建

```bash
dotnet build IronNestFCS.sln -c Release
```

单文件安装器源码位于 `IronNestFCS.Installer`，使用 Windows .NET Framework MSBuild 构建。安装器的 `Payload` 目录只用于编译时嵌入资源，不会在玩家发布包中单独分发。

各程序集的输出位置：

- **宿主 Mod**（`IronNestFCS.dll`）：放入游戏的 `Mods/` 目录，由 MelonLoader 自动加载。
- **火控逻辑**（`IronNestFCS.Logic.dll`）：输出到 `UserData/IronNestFCS/`（不放进 `Mods/`，由宿主在运行时反射加载）。
- **契约**（`IronNestFCS.Abstractions.dll`）：放入 `UserLibs/`，确保宿主与逻辑共用同一份接口。
- **自定义唱片机**（`IronNestFCS.CustomRecords.dll`）：放入 `Mods/`。把音频文件（`.mp3` / `.wav` / `.flac`）放入游戏的 `UserData/CustomRecords/` 目录，封面两种方式任选：① 同名图片文件（`song.mp3` 配 `song.png`/`.jpg`/`.jpeg`，音频与图片分开存放）；② 音频文件内嵌封面（TagLib 标签）。进场景后自动为每个文件克隆一张 RecordDisk；两种封面都没有的文件会被跳过。目录不存在时首次运行会自动创建，无需手工准备素材。构建时依赖（CSCore、TagLibSharp）会自动拷贝到 `UserLibs/`（不能留在 `Mods/`，否则会被 MelonLoader 误当作 Mod 加载）。

> `IronNestFCS.Logic.csproj` 默认已把 `OutputPath` 指向 `$(GameDir)\UserData\IronNestFCS\`，构建即就位，改完代码进游戏按 F9 即可生效。

## 使用

1. 启动已安装 MelonLoader 与本 Mod 的游戏。
2. 进入包含炮塔与地图桌的关卡场景。若火控面板提示 `Dial 未绑定`，按 **F9** 在当前场景重新绑定。
3. 在控制台旁的按钮上选择弹种（默认 HE），并按需开启 `Auto Fire`。
4. 拖动地图上的目标标记 (1~4) 到目标位置。
5. 点击地图右侧的目标按钮（T1~T4）下达打击任务，Mod 会自动完成解算、装填、瞄准与击发。
6. 左上角面板实时显示两管炮的任务进度与队列情况。

### 开发热重载

修改 `IronNestFCS.Logic` 内的代码后，重新构建该项目（dll 会直接输出到游戏的 `UserData/IronNestFCS/`），切回游戏按 **F9** 即可加载新逻辑，无需重启游戏。

## 贡献

欢迎提交 Issue 和 Pull Request。

- 发现 Bug、有功能建议或疑问，请[提交 Issue](../../issues)。
- 改进代码请[提交 Pull Request](../../pulls)。改动火控逻辑时请留意 `FSC.cs` 中关于热重载与协程的约定（不要在 Logic 中注册新的 IL2CPP 类型、协程必须登记以便卸载时停止、跨 ALC 只能传递 `IFcsModule`）。

## 免责声明

本项目为非官方的第三方 Mod，与游戏开发商无关。仅供学习与单机娱乐使用，使用风险自负。
