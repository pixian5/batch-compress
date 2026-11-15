using System;
using System.IO;
using BatchCompress.Avalonia.Core.Services;

// WorkBuddy, 2026-11-16：批次 25（P2）——窗口状态持久化。
// 与 Program.cs 保持一致：测试工程使用全局命名空间。
internal static partial class Program
{
    private static Task TestWindowStateService()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bc-window-" + Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "nested", "window-settings.json");

        try
        {
            var service = new WindowStateService(settingsPath);

            // 1. 文件不存在时返回 null，不能抛异常。
            Assert(service.Load() is null, "设置文件不存在时应返回 null");

            // 2. 保存后读回，尺寸、位置与最大化状态都要一致（含自动建目录）。
            service.Save(new WindowStateSnapshot(1280, 800, 40, 60, IsMaximized: true));
            Assert(File.Exists(settingsPath), "保存后应自动创建目录与文件");

            var loaded = service.Load();
            Assert(loaded is not null, "保存后应能读回");
            AssertEqual(1280d, loaded!.Width);
            AssertEqual(800d, loaded.Height);
            AssertEqual(40, loaded.X);
            AssertEqual(60, loaded.Y);
            Assert(loaded.IsMaximized, "最大化状态应保持");

            // 3. 与旧版本 JSON 格式兼容：WindowState 用 2 表示最大化（Avalonia 数值）。
            File.WriteAllText(settingsPath,
                "{\"Width\":900,\"Height\":700,\"X\":12,\"Y\":24,\"WindowState\":2}");
            var legacy = service.Load();
            Assert(legacy is not null && legacy.IsMaximized, "旧格式的最大化状态应被识别");
            AssertEqual(900d, legacy!.Width);

            File.WriteAllText(settingsPath,
                "{\"Width\":900,\"Height\":700,\"X\":null,\"Y\":null,\"WindowState\":0}");
            var normal = service.Load();
            Assert(normal is not null && !normal.IsMaximized, "旧格式的普通状态应被识别");
            Assert(normal!.X is null && normal.Y is null, "缺失的位置应保持 null");

            // 4. 损坏的 JSON 返回 null，绝不阻止窗口打开。
            File.WriteAllText(settingsPath, "{ not-json");
            Assert(service.Load() is null, "损坏的设置文件应返回 null");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 清理失败不影响结论。
            }
        }

        return Task.CompletedTask;
    }
}
