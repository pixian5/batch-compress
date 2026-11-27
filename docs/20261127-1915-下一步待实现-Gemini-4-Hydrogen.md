# 下一步待实现清单（基于 0.8.37）

> 用户原问题：继续
> 完整背景分析见：[20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md](file:///Users/x/code/compress/docs/20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md)

本机可实施的代码可靠性、跨卷路径一致性、预检一致性与工程架构重构建议已全部落实（截至 `v0.8.37`）。当前剩余事项仅依赖外部物理机器环境或按需演进：

## 1. 跨平台真机实体环境验证（待外部机器执行）

1. **Windows 10 / 11 物理机验证**
   - 运行一键验证脚本并核查真实 WinRAR、联网自动安装 7-Zip 与独占文件锁回滚路径：
     ```powershell
     powershell -ExecutionPolicy Bypass -File scripts/verify-windows.ps1
     ```
   - 手工启动 GUI 验证托盘行为、五语言切换、拖拽 TXT/目录与预检弹窗滚动排版。
2. **Linux 桌面物理机（Ubuntu / Debian X11 & Wayland）验证**
   - 运行单元测试、冒烟检查与携带 `tools/rarLinux` 的便携包在真实 `ext4` / 外接 `FAT32` 挂载卷下验证：
     ```bash
     bash scripts/smoke-test.sh
     dotnet run -c Release --project BatchCompress.Avalonia.Tests/BatchCompress.Avalonia.Tests.csproj
     ```

## 2. 按需后续发布或新特性

1. 如准备发布正式版本（例如更新 `VERSION` 并推送 `v0.9.0` / `v1.0.0` Git 标签），GitHub Actions [release.yml](file:///Users/x/code/compress/.github/workflows/release.yml) 将自动在三平台运行测试门禁、打包并实测分发包后发布 GitHub Release。
