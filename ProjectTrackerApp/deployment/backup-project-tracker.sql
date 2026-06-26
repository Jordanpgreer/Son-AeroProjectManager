DECLARE @BackupDirectory nvarchar(4000) = N'C:\Backups\ProjectTracker';
DECLARE @BackupFile nvarchar(4000) =
    @BackupDirectory + N'\ProjectTracker_' +
    CONVERT(nvarchar(8), GETDATE(), 112) + N'_' +
    REPLACE(CONVERT(nvarchar(8), GETDATE(), 108), N':', N'') + N'.bak';

BACKUP DATABASE [ProjectTracker]
TO DISK = @BackupFile
WITH INIT, CHECKSUM, STATS = 10;

