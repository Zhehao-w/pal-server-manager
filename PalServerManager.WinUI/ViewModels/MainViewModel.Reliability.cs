namespace HaoHaoTianTian.PalHR.ViewModels;

public partial class MainViewModel
{
    private static readonly TimeSpan IdleAutomaticRetryDelay = TimeSpan.FromMinutes(5);

    partial void OnIsBusyChanged(bool value)
    {
        if (value ||
            Interlocked.CompareExchange(ref _automaticActionStarted, 0, 0) == 0 ||
            _scheduledAt is not null ||
            _emptySince is null ||
            !IdleShutdownEnabled ||
            !IsServerRunning ||
            !_hasPlayerSnapshot ||
            _lastPlayerCount != 0 ||
            !StatusMessage.Contains("失败：", StringComparison.Ordinal)) return;

        var idleDuration = TimeSpan.FromMinutes(Math.Clamp(Math.Round(IdleShutdownMinutes), 1, 1440));
        _emptySince = DateTimeOffset.Now - idleDuration + IdleAutomaticRetryDelay;
        Interlocked.Exchange(ref _automaticActionStarted, 0);
        StatusMessage = $"空服自动关服失败，将在 {FormatCountdown(IdleAutomaticRetryDelay)} 后重试；有玩家加入会取消。";
    }
}
