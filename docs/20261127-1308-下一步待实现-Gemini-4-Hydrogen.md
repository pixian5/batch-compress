# 下一步待实现清单（基于 0.8.35）

> 用户原问题：继续
> 完整背景分析见：[20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md](file:///Users/x/code/compress/docs/20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md)

`v0.8.35` 已完成批次 45（核心大文件拆解、`LanguageExplicitlySet` 元数据显式化与 `desktop.Exit` 确定性清理）。后续可按需求安排以下低耦合批次：

## 批次 46（可选）：仓库整洁与项目解耦

1. **`.csproj` 编译项治理与 `Core` 子项目抽离评估**
   - 评估将 `BatchCompress.Avalonia.Tests.csproj` 的显式 `<Compile Include="..\...">` 列表逐步收敛为引用独立的 `BatchCompress.Core.csproj`（承载核心服务与模型，不依赖 Avalonia UI），减少测试工程与主工程间文件清单同步漏改风险。
2. **源码树构建产物隔离核查**
   - 复核仓库内历史包缓存与工具缓存目录是否已严格被 `.gitignore` 屏蔽并清理未跟踪冗余，维持轻量源码仓库结构。

## 跨平台真机验证（依赖外部环境）

1. **Windows / Linux 实机回归**
   - 在真实 Windows（Win11）与 Linux（Ubuntu X11/Wayland）环境验证 GUI 启动、系统托盘/关闭行为、本地化切换、文件拖拽及中英文路径解压缩矩阵。
