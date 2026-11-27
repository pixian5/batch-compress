# 下一步待实现

> 当前版本：`0.8.32`

当前所有可在 macOS 本机与跨平台构建层面落实的功能、安全边界与测试均已完成收官；无新增必做代码待办，仅余依赖物理硬件环境的外部验证项：

1. **Windows 真机一键验证（待在 Windows 实体机执行）**：
   - 运行 `powershell -ExecutionPolicy Bypass -File scripts/verify-windows.ps1`，自动完成 Release 构建、122 项单元测试、文件独占占用返回码 1 判失败与 `--strict-extract` 闭环验证。
2. **Linux 真机验证（可选）**：
   - 在目标 Linux 发行版上运行 `bash scripts/smoke-test.sh` 与 `dotnet run -c Release --project BatchCompress.Avalonia.Tests/BatchCompress.Avalonia.Tests.csproj`。
3. **人工视觉巡检（可选）**：
   - 如需确认五语言下的视觉细节，可自行打开界面巡检主窗口导航栏（含「重置偏好」）、开始页（含「复制 CLI」）与滚动帮助窗口排版。
