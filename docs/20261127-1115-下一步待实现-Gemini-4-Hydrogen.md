# 下一步待实现

> 当前版本：`0.8.30`

本机可执行的软件功能、安全加固、偏好管理、五语言帮助与全套自动化验证均已完成；当前仅余依赖物理硬件或人工视觉确认的外部项：

1. **Windows 真机一键验证（待在 Windows 实体机执行）**：
   - 运行 `powershell -ExecutionPolicy Bypass -File scripts/verify-windows.ps1`，自动完成 Release 构建、120 项单元测试、文件独占占用返回码 1 判失败与 `--strict-extract` 闭环验证。
2. **Linux 真机验证（可选）**：
   - 在目标 Linux 发行版上运行 `bash scripts/smoke-test.sh` 与 `dotnet run -c Release --project BatchCompress.Avalonia.Tests/BatchCompress.Avalonia.Tests.csproj`。
3. **人工视觉巡检（可选）**：
   - 按需打开界面核对五语言切换、顶部「重置偏好」按钮及新滚动帮助窗口在不同窗口宽度下的实际渲染排版。
