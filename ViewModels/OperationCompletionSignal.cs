using System.Threading.Tasks;

namespace BatchCompress.Avalonia.ViewModels;

// WorkBuddy, 2026-11-17：0.8.15——关窗流程需要等待 OperationSession 的 finally 真正执行完，
// 不能只等一个布尔值。这个纯类把“开始/完成/等待”抽出来，便于单元测试。
public sealed class OperationCompletionSignal
{
    private TaskCompletionSource? _pending;

    public Task WaitAsync() => _pending?.Task ?? Task.CompletedTask;

    public void Begin()
    {
        // 同一时间只允许一个批处理；重复开始先把旧等待者唤醒，避免悬挂。
        _pending?.TrySetResult();
        _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Complete() => _pending?.TrySetResult();
}
