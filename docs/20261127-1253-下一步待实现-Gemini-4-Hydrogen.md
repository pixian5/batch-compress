# 下一步待实现

> 当前版本：`0.8.33`

本机已完成 `0.8.29–0.8.33` 开发与复核收官，无剩余必做代码待办；仅余依赖外部物理硬件与人工确认的检查项：

1. **Windows 真机一键验证（待在 Windows 实体机执行）**：
   - 运行 `powershell -ExecutionPolicy Bypass -File scripts/verify-windows.ps1`，自动完成 Release 构建、122 项单元测试、文件独占占用返回码 1 判失败与 `--strict-extract` 闭环验证。
2. **Linux 真机验证（可选）**：
   - 在目标 Linux 发行版上运行 `bash scripts/smoke-test.sh` 与 `dotnet run -c Release --project BatchCompress.Avalonia.Tests/BatchCompress.Avalonia.Tests.csproj`。
3. **人工视觉巡检（可选）**：
   - 按需打开界面核对五语言切换、顶部「重置偏好」、开始页「复制 CLI」、悬停提示及滚动帮助窗口渲染。
