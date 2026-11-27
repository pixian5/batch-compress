# 下一步待实现清单（基于 0.8.36）

> 用户原问题：继续
> 完整背景分析见：[20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md](file:///Users/x/code/compress/docs/20261127-1255-整体分析与下一步开发建议-Gemini-4-Hydrogen.md)

`v0.8.33` 整体分析中的本机代码、架构、交互体验与测试拆解建议（批次 44、批次 45、批次 46 核心工程治理）已全部在本机完成并通过全套自动化验证。当前剩余事项均依赖外部物理环境或按需演进：

## 1. 跨平台真机实体环境验证（待外部机器执行）

1. **Windows 10 / 11 物理机验证**
   - 运行一键验证脚本并检查真实 WinRAR + 联网自动安装 7-Zip 与文件锁定回滚路径：
     ```powershell
     powershell -ExecutionPolicy Bypass -File scripts/verify-windows.ps1
     ```
   - 手工打开 GUI 核对托盘通知、中文/英文/德文/日文/繁体切换、拖拽 TXT/目录与预检弹窗滚动排版。
2. **Linux 桌面物理机（Ubuntu / Debian X11 & Wayland）验证**
   - 运行单元测试、冒烟测试与打包产物的真实压缩/解压（含 `tools/rarLinux` 与真实 `ext4` / 外接大小写不敏感 FAT32 挂载卷）：
     ```bash
     bash scripts/smoke-test.sh
     dotnet run -c Release --project BatchCompress.Avalonia.Tests/BatchCompress.Avalonia.Tests.csproj
     ```

## 2. 可选后续规划

1. 若后续计划引入新功能或发布 `v0.9.0` / `v1.0.0` 正式版标签（触发 `.github/workflows/release.yml` 三平台构建并由 GitHub Actions `verify-artifacts` 自动实测三平台分发包），可随时准备发布说明与签名配置。
