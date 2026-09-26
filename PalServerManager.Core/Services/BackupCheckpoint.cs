namespace HaoHaoTianTian.PalHR.Services;

// Used by deterministic fake-server tests; production leaves the callback null.
public enum BackupCheckpoint
{
    BeforeProtection,
    DuringCopy,
    AfterCopyBeforeVerify,
    BeforePublish,
    BeforeActiveReplace,
    DuringRestore,
    DuringRecovery
}
