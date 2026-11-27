# 当前开发进度（2026-11-27）

> 用户原问题：继续
> 当前版本：`0.8.35`（`VERSION`）

## 已完成里程碑

- **核心流程与测试体系（v0.8.1 ~ v0.8.34）**：CLI/GUI 解压缩全管线、7z/SharpCompress/SystemZip/Rar 引擎矩阵、五语言本地化（`387` 键完全对齐）、非破坏性 `GuiPreflightService` 预检试运行、自定义密码默认掩码保护（`PasswordChar="●"` 与显隐切换）、`VolumeAwarePathComparer` 跨大小写卷统一比较。
- **最新完成（`v0.8.35` / 批次 45，2026-11-27）**：
  - 核心大文件按职责拆分为 `partial class`：`MainWindowViewModel.cs`（`1156` -> `745` 行，拆出 `MainWindowViewModel.Preferences.cs` `424` 行）、`CommandLineHandler.cs`（`1099` -> `647` 行，拆出 `CommandLineHandler.Validation.cs` `283` 行并将帮助文本与换行工具迁移至 `CommandLineHandler.HelpTexts.cs` `622` 行）。
  - `CommandLineParseOutcome` 显式暴露 `LanguageExplicitlySet` 属性并保持向后兼容。
  - `App.axaml.cs` 增加 `desktop.Exit += OnDesktopExit` 确定性清理（注销本地化属性监听与释放 `_statusBarHelper`）。
  - 更新回归测试文件行数与生命周期断言，Release 编译 `0 Warning / 0 Error`，`123` 项单元测试（真实 Case-sensitive APFS `0 SKIP`）、`158` 项全功能测试、`43` 项经验验证、`5` 项冒烟检查及 `4` 项 macOS 后台 UI 冒烟全部通过。

## 质量基准

- 单元测试：`123/123` 通过（真实 Case-sensitive APFS 挂载验证，`0 SKIP`）。
- 全功能测试：`158/158` 通过。
- 经验验证：`43/43` 通过。
- 冒烟检查：`5/5` 通过；macOS 打包与 UI 冒烟：`4/4` 通过（后台隐身运行中）。

## 下一步入口

详见 [下一步待实现](file:///Users/x/code/compress/docs/20261127-1308-下一步待实现-Gemini-4-Hydrogen.md) 与 [整体分析与下一步开发建议（0.8.33）](file:///Users/x/code/compress/docs/20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md)。
