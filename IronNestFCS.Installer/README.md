# IronNestFCS 单文件安装器

该项目将 MelonLoader、离线 Unity 依赖、Cpp2IL 和 IronNestFCS 的三个 DLL 作为资源嵌入一个 Windows EXE。终端用户不需要 PowerShell、CMD 脚本或外部 `payload` 文件夹。

构建环境：Windows + .NET Framework 4.8 Developer Pack/MSBuild；运行环境为 64 位 Windows 10/11 自带的 .NET Framework 4.8。发布前应执行内置资源校验，并在临时游戏目录完成一次安装/卸载往返测试。
