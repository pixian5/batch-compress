# 当前开发进度（2026-11-27）

> 用户原问题：继续
> 当前版本：`0.8.37`（`VERSION`）

## 已完成里程碑

- **核心流程与质量体系（`v0.8.1 ~ v0.8.36`）**：CLI/GUI 解压缩全管线、7z/SharpCompress/SystemZip/Rar 引擎矩阵、五语言本地化（`387` 键）、`GuiPreflightService` 预检试运行、自定义密码默认掩码保护、全仓大文件拆解（所有源码与测试 `.cs` `< 1000` 行）、通配符测试工程与 `TestCoreIsolationAndProjectHygiene` 守护。
- **最新完成（`v0.8.37` / 批次 47，2026-11-27）**：
  - [BatchPreflightService.cs](file:///Users/x/code/compress/Core/Services/BatchPreflightService.cs) 新增统一无副作用单条目预检器 `InspectDecompressionPath`，合并消除 `GuiPreflightService` 与 `DecompressionItemAdapter` 的重复解析代码，并补齐 `HeadlessBatchRunner.RunDryRun()` 对 `extract --dry-run` 缺卷、缺首卷、重复编号与大小写歧义的阻断（返回退出码 `2`）。
  - 收口 [ArchiveVolumeResolver.cs](file:///Users/x/code/compress/Core/Services/ArchiveVolumeResolver.cs)（`ResolveOutputFiles`）、[BatchOperationService.cs](file:///Users/x/code/compress/Core/Services/BatchOperationService.cs)（密码本去重、压缩清单去重、预检影响项汇总）、[CliCommandBuilder.cs](file:///Users/x/code/compress/Core/Services/CliCommandBuilder.cs)（`--temp-dir` 判定）及 [RarArchiveEngine.cs](file:///Users/x/code/compress/Core/Services/RarArchiveEngine.cs)（`IsUnderBaseDirectory`）的按卷路径比较一致性。
  - 补全 [MainWindow.axaml](file:///Users/x/code/compress/Views/MainWindow.axaml)“添加附件”复选框与 [AboutWindow.axaml](file:///Users/x/code/compress/Views/AboutWindow.axaml)“导出诊断”按钮的悬停提示，新增 `TestExtractDryRunPreflightValidation`（第 `125` 项单元测试）并扩展跨卷大小写回归。
  - Release 编译 `0 Warning / 0 Error`，真实 `Case-sensitive APFS` `125/125`（`0 SKIP`）、`158` 项全功能测试、`43` 项经验验证、`5` 项冒烟检查及 `4` 项 macOS 后台 UI 冒烟全部通过。

## 质量基准

- 单元测试：`125/125` 通过（真实 `Case-sensitive APFS` 卷验证，`0 SKIP`）。
- 全功能测试：`158/158` 通过。
- 经验验证：`43/43` 通过。
- 冒烟检查：`5/5` 通过；macOS 打包与 UI 冒烟：`4/4` 通过（后台隐身运行中）。

## 下一步入口

详见 [下一步待实现](file:///Users/x/code/compress/docs/20261127-1915-下一步待实现-Gemini-4-Hydrogen.md) 与 [整体分析与下一步开发建议（0.8.33）](file:///Users/x/code/compress/docs/20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md)。
