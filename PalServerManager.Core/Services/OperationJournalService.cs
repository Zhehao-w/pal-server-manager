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
        if (File.Exists(context.StatePaths.PendingOperationPath)) File.Delete(context.StatePaths.PendingOperationPath);
        var previous = context.StatePaths.PendingOperationPath + ".previous";
        if (File.Exists(previous)) File.Delete(previous);
    }
}
