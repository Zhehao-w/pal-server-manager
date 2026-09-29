using HaoHaoTianTian.PalHR.Models;

namespace HaoHaoTianTian.PalHR.Services;

public sealed class OperationJournalService(PalContext context, SafeFileService files)
{
    public async Task WriteAsync(PendingOperation operation, string phase, CancellationToken cancellationToken = default)
    {
        operation.Phase = phase;
        operation.UpdatedUtc = DateTimeOffset.UtcNow;
        await files.WriteJsonAsync(context.StatePaths.PendingOperationPath, operation, keepPrevious: true, cancellationToken: cancellationToken);
    }

    public void Complete()
    {
        var current = context.StatePaths.PendingOperationPath;
        var previous = current + ".previous";
        if (File.Exists(previous)) File.Delete(previous);
        if (File.Exists(current)) File.Delete(current);
    }
}
