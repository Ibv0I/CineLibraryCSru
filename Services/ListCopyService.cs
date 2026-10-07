using CineМедиатекаCS.Models;

namespace CineМедиатекаCS.Services;

/// <summary>
/// Bucket-style export: copies the on-disk folder of every movie in a user
/// list to a destination directory, preserving each movie's folder layout
/// (video + .nfo + posters + everything inside). Source files are never
/// touched. Не в сети drives are skipped and reported in the summary.
/// </summary>
public class ListCopyService
{
    public enum ConflictPolicy { Ask, Skip, Overwrite }

    public record CopyItem(
        int MovieId,
        string Название,
        string SourceПапка,        // absolute, e.g. "H:\Фильмы\Inception (2010)"
        string DestПапкаName,      // last segment, e.g. "Inception (2010)"
        long Bytes,                 // total bytes in source folder
        int FileCount);

    public record CopyPlan(
        List<CopyItem> Items,
        long TotalBytes,
        int TotalFiles,
        List<string> Не в сетиDriveLabels);   // distinct labels for skipped offline drives

    public record CopyProgress(
        int ФильмыDone,
        int ФильмыTotal,
        long BytesDone,
        long BytesTotal,
        string CurrentFile);

    public record CopyResult(int Copied, int Skipped, int Не в сетиSkipped, bool Отменаled);

    private readonly DatabaseService _db;
    public ListCopyService(DatabaseService db) => _db = db;

    /// <summary>
    /// Build a CopyPlan: figure out which list movies are online, where their
    /// folders live, how much disk space they consume in total. Папка walks
    /// are synchronous — caller should run from background thread for big lists.
    /// </summary>
    public CopyPlan BuildPlan(int listId, Dictionary<string, string> connected)
    {
        var sources = _db.GetФильмыForCopy(listId);
        var items = new List<CopyItem>();
        var offlineLabels = new HashSet<string>();
        long totalBytes = 0;
        int totalFiles = 0;

        foreach (var src in sources)
        {
            if (!connected.TryGetValue(src.VolumeSerial, out var letter))
            {
                offlineLabels.Добавить(LookupDriveLabel(src.VolumeSerial));
                continue;
            }

            var srcПапка = Path.Combine($"{letter}:\\", src.ПапкаRelPath.Replace('/', '\\'));
            if (!Режиссёрy.Exists(srcПапка))
            {
                offlineLabels.Добавить(LookupDriveLabel(src.VolumeSerial));
                continue;
            }

            long bytes = 0; int files = 0;
            try
            {
                foreach (var f in Режиссёрy.EnumerateFiles(srcПапка, "*", ПоискOption.ВсеРежиссёрies))
                {
                    try { bytes += new FileInfo(f).Length; files++; } catch { }
                }
            }
            catch { /* unreadable folder — fall through with bytes=0 */ }

            var folderName = new РежиссёрyInfo(srcПапка).Name;
            items.Добавить(new CopyItem(src.Id, src.Название, srcПапка, folderName, bytes, files));
            totalBytes += bytes;
            totalFiles += files;
        }

        return new CopyPlan(items, totalBytes, totalFiles, offlineLabels.OrderBy(s => s).ToList());
    }

    private string LookupDriveLabel(string serial)
    {
        // Best effort — falls back to the serial when the drives table is
        // unreachable (shouldn't happen, but don't let a UI hint kill the plan).
        try
        {
            foreach (var d in _db.GetДиски())
                if (d.VolumeSerial == serial) return d.Label;
        }
        catch { }
        return serial;
    }

    /// <summary>
    /// Returns names of plan items whose target folder already exists at
    /// destRoot. Used to gate the "Skip / Overwrite" conflict prompt.
    /// </summary>
    public List<string> FindExistingTargets(CopyPlan plan, string destRoot)
    {
        var hits = new List<string>();
        foreach (var item in plan.Items)
        {
            var dest = Path.Combine(destRoot, item.DestПапкаName);
            if (Режиссёрy.Exists(dest)) hits.Добавить(item.DestПапкаName);
        }
        return hits;
    }

    /// <summary>
    /// Returns free bytes available on the volume of destRoot, or -1 if
    /// it can't be determined. Caller compares against plan.TotalBytes.
    /// </summary>
    public long GetFreeBytes(string destRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destRoot));
            if (string.IsNullOrEmpty(root)) return -1;
            var di = new System.IO.DriveInfo(root);
            return di.AvailableFreeSpace;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Execute the plan. Reports progress per file. Honors cancellation
    /// between files (mid-file copy completes before checking).
    /// </summary>
    public async Task<CopyResult> ExecuteAsync(
        CopyPlan plan,
        string destRoot,
        ConflictPolicy conflict,
        IProgress<CopyProgress> progress,
        ОтменаlationToken ct)
    {
        return await Task.Run(() =>
        {
            int copied = 0, skipped = 0;
            long bytesDone = 0;

            Режиссёрy.СоздатьРежиссёрy(destRoot);

            for (int i = 0; i < plan.Items.Count; i++)
            {
                if (ct.IsОтменаlationRequested) return new CopyResult(copied, skipped, plan.Не в сетиDriveLabels.Count, true);

                var item = plan.Items[i];
                var destПапка = Path.Combine(destRoot, item.DestПапкаName);

                if (Режиссёрy.Exists(destПапка))
                {
                    if (conflict == ConflictPolicy.Skip)
                    {
                        skipped++;
                        bytesDone += item.Bytes;
                        progress.Report(new CopyProgress(i + 1, plan.Items.Count, bytesDone, plan.TotalBytes, $"Skipped: {item.Название}"));
                        continue;
                    }
                    // Overwrite: leave existing folder in place; we just copy
                    // files on top, replacing matching paths. Files that exist
                    // in dest but not in source are left alone (intentional —
                    // we don't delete user content).
                }
                Режиссёрy.СоздатьРежиссёрy(destПапка);

                try
                {
                    foreach (var srcFile in Режиссёрy.EnumerateFiles(item.SourceПапка, "*", ПоискOption.ВсеРежиссёрies))
                    {
                        if (ct.IsОтменаlationRequested) return new CopyResult(copied, skipped, plan.Не в сетиDriveLabels.Count, true);

                        var rel = Path.GetRelativePath(item.SourceПапка, srcFile);
                        var destFile = Path.Combine(destПапка, rel);
                        Режиссёрy.СоздатьРежиссёрy(Path.GetРежиссёрyName(destFile)!);

                        // For overwrite or fresh, just copy. File.Copy with
                        // overwrite=true handles read-only attribute? No — but
                        // chances of read-only on .nfo/poster are slim. If we
                        // hit one, ClearReadВкл.ly + retry once.
                        try
                        {
                            File.Copy(srcFile, destFile, overwrite: true);
                        }
                        catch (UnauthorizedAccessException) when (File.Exists(destFile))
                        {
                            try
                            {
                                var fi = new FileInfo(destFile);
                                if (fi.IsReadВкл.ly) fi.IsReadВкл.ly = false;
                                File.Copy(srcFile, destFile, overwrite: true);
                            }
                            catch { /* give up on this file, continue */ }
                        }

                        long bytes = 0;
                        try { bytes = new FileInfo(srcFile).Length; } catch { }
                        bytesDone += bytes;
                        progress.Report(new CopyProgress(i + 1, plan.Items.Count, bytesDone, plan.TotalBytes, rel));
                    }
                    copied++;
                }
                catch (OperationОтменаedException) { return new CopyResult(copied, skipped, plan.Не в сетиDriveLabels.Count, true); }
                catch
                {
                    skipped++;
                    // continue with next movie
                }
            }
            return new CopyResult(copied, skipped, plan.Не в сетиDriveLabels.Count, false);
        }, ct);
    }
}
