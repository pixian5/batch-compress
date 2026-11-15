using BatchCompress.Avalonia.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace BatchCompress.Avalonia.ViewModels;

/// <summary>
/// 硬件感知并发推荐相关的界面状态。
/// WorkBuddy, 2026-11-15：批次 18（P4-2）——单独放分部类，避免继续撑大主视图模型；
/// 推荐值只在界面首次读取时计算一次并缓存，切换语言时文案随 L 重新求值。
/// </summary>
public partial class MainWindowViewModel
{
    private ConcurrencyRecommendation? _concurrencyRecommendation;

    private ConcurrencyRecommendation Recommendation =>
        _concurrencyRecommendation ??= ConcurrencyAdvisor.Recommend();

    /// <summary>推荐并发度（1-16）。</summary>
    public int RecommendedConcurrency => Recommendation.Recommended;

    /// <summary>界面上显示的「推荐 N」短标签。</summary>
    public string RecommendedConcurrencyText =>
        string.Format(L.RecommendedConcurrencyLabel, RecommendedConcurrency);

    /// <summary>
    /// 并发控件的悬浮提示：保留原有解释，并补上当前机器的推荐值与判断依据。
    /// </summary>
    public string RecommendedConcurrencyHint =>
        $"{L.TooltipMaxConcurrency}\n" +
        $"{string.Format(L.RecommendedConcurrencyLabel, RecommendedConcurrency)} " +
        $"({Recommendation.CoreCount} cores, " +
        $"{ConcurrencyAdvisor.ToGigabytes(Recommendation.AvailableMemoryBytes):F1} GB) — " +
        L.TooltipRecommendedConcurrency;

    /// <summary>
    /// 一键把推荐值写回并发度输入框，并在命令日志留痕。
    /// 不改变默认串行行为：只有用户主动点击才会修改设置。
    /// </summary>
    [RelayCommand]
    private void ApplyRecommendedConcurrency()
    {
        MaxConcurrency = RecommendedConcurrency;
        CommandLog += $"{RecommendedConcurrencyText} → {L.ApplyRecommendedConcurrency}\n";
        OnPropertyChanged(nameof(RecommendedConcurrencyText));
    }
}
