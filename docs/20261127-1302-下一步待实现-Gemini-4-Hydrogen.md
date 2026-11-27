# 下一步待实现

> 当前版本：`0.8.34`（依据 [整体分析与下一步开发建议（0.8.33）](20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md)）

批次 44（GUI 预检试运行 + 自定义密码掩码）已完成交付；剩余规划如下：

1. **批次 45（P2 工程化重构）：超千行文件拆解与解析器纯净化**
   - 拆分 `MainWindowViewModel.Preferences.cs` 与 `CommandLineHandler.Validation.cs`，将主文件与命令行解析控制到 600–750 行级别；
   - 将 `CommandLineHandler.ParseArguments` 内直接改写全局静态单例 `LocalizationService.Instance` 的副作用收口为返回解析元数据由 `Program.cs` 统一同步；为 `App.axaml.cs` 增加 `desktop.Exit` 事件清理退订。
2. **批次 46（P2 按需）：仓库卫生整理与测试工程引用规范化**
   - 清理本地 236 MB 遗留未跟踪 `BatchCompress.Avalonia/` 构建目录，归档早于 `v0.8.0` 的 `docs/` 历史文档至 `docs/archive/` 并验证零断链；
   - 评估抽出零 UI 依赖的 `BatchCompress.Core.csproj` 供主程序与测试工程通过 `<ProjectReference>` 共享，替代手工列举 50+ 个 `<Compile Include>`。
3. **实体机跨平台真机验证（外部物理环境依赖）**
   - Windows 实体机运行 `powershell -ExecutionPolicy Bypass -File scripts/verify-windows.ps1`；
   - Linux 实体机运行 `bash scripts/smoke-test.sh` 与全量单元测试。
