# 当前开发进度（2026-11-27）

> 用户原问题：继续
> 当前版本：`0.8.36`（`VERSION`）

## 已完成里程碑

- **核心流程与测试体系（`v0.8.1 ~ v0.8.35`）**：CLI/GUI 解压缩全管线、7z/SharpCompress/SystemZip/Rar 引擎矩阵、五语言本地化（`387` 键）、`GuiPreflightService` 非破坏性预检试运行、自定义密码默认掩码与显示切换、`VolumeAwarePathComparer` 全仓按卷路径一致性、核心大文件拆解及 `desktop.Exit` 确定性清理。
- **最新完成（`v0.8.36` / 批次 46，2026-11-27）**：
  - [BatchCompress.Avalonia.Tests.csproj](file:///Users/x/code/compress/BatchCompress.Avalonia.Tests/BatchCompress.Avalonia.Tests.csproj) 升级为目录通配符引入 `Core/`、`Localization/`、`CommandLineHandler*.cs` 与 `Program*.cs`（仅排除依赖 Avalonia 窗口系统的 `SystemIntegrationService.cs`），并新增 `TestCoreIsolationAndProjectHygiene` 自动守护核心分层零 UI 依赖与全仓 `.cs` 文件 `< 1000` 行上限。
  - `DiagnosticsExportService.Create` 统一经由 `_archiveEngine`（支持 `ArchiveEngineRouter` 解包）构造诊断导出器，消除 `MainWindowViewModel.Execution.cs` 与 `HeadlessBatchRunner.cs` 绕过注入重复 instantiation；`Program.cs` 显式同步 `parseOutcome.LanguageExplicitlySet`。
  - 从 `Views/MainWindow.axaml.cs`（`935` -> `795` 行）拆出 `Views/MainWindow.WindowState.cs`（`149` 行）；将 `Program.PipelineTests.cs`（`1886` 行）拆为 `Program.PipelineTests.cs`（`590` 行）、`Program.SourceAndVolumeTests.cs`（`684` 行）、`Program.ConcurrencyAndRollbackTests.cs`（`629` 行），全仓 `.cs` 文件无一超千行。
  - 清理本地未跟踪历史构建缓存与临时产物，发布基线 Release 编译 `0 Warning / 0 Error`，真实 `Case-sensitive APFS` `124/124`（`0 SKIP`）、`158` 项全功能测试、`43` 项经验验证、`5` 项冒烟检查及 `4` 项 macOS 后台 UI 冒烟全部通过。

## 质量基准

- 单元测试：`124/124` 通过（真实 `Case-sensitive APFS` 卷验证，`0 SKIP`）。
- 全功能测试：`158/158` 通过。
- 经验验证：`43/43` 通过。
- 冒烟检查：`5/5` 通过；macOS 打包与 UI 冒烟：`4/4` 通过（后台隐身运行中）。

## 下一步入口

详见 [下一步待实现](file:///Users/x/code/compress/docs/20261127-1822-下一步待实现-Gemini-4-Hydrogen.md) 与 [整体分析与下一步开发建议（0.8.33）](file:///Users/x/code/compress/docs/20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md)。
