using System.Text;
using Microsoft.Data.Sqlite;

namespace GPhotosSync.Data;

public enum SyncItemStatus
{
    Downloaded = 0,
    SkippedInitial = 1,
    PendingRemoteDelete = 2,
    DeletedLocalAndRemote = 3
}

public enum SkipReason
{
    None = 0,
    Baseline = 1,
    CutoffDate = 2,
    VideoDisabled = 3,
    FolderUnlinked = 4,
    DeleteUnconfirmed = 5,
    RestoreRequested = 6
}

public class SyncItemRecord
{
    public string MediaKey { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;
    public string LocalFileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public DateTime? TakenAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public SyncItemStatus Status { get; set; }
    public SkipReason SkipReason { get; set; }
    public string BulkDeleteBatchId { get; set; } = string.Empty;
    public bool BulkDeleteApproved { get; set; }
}

public class SyncDatabase
{
    private readonly string _connectionString;
    private readonly object _dbLock = new();

    public SyncDatabase(string? customDbPath = null)
    {
        var dbPath = customDbPath ?? AppConfig.DatabaseFilePath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeSchema();
    }

    public static bool HasExistingDatabaseWithRecords(string? customDbPath = null)
    {
        try
        {
            var dbPath = customDbPath ?? AppConfig.DatabaseFilePath;
            if (!File.Exists(dbPath))
                return false;

            var fi = new FileInfo(dbPath);
            if (fi.Length == 0)
                return false;

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            using var conn = new SqliteConnection(cs);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='SyncItems'";
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
                return false;

            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(1) FROM SyncItems";
            return Convert.ToInt32(countCmd.ExecuteScalar()) > 0;
        }
        catch
        {
            return true; // V případě nejasnosti předpokládat existující evidenci (fail-safe)
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.CreateCollation("UNICODE_NOCASE", static (x, y) =>
            string.Compare(
                NormalizePathKey(x),
                NormalizePathKey(y),
                StringComparison.OrdinalIgnoreCase));
        return conn;
    }

    /// <summary>
    /// Bod 6: Normalizuje relativní cestu souboru. Nikdy neodstraňuje úvodní tečku ze samotného názvu souboru
    /// (např. ".hidden.jpg"), odstraňuje pouze explicitní prefix relativní cesty ".\".
    /// </summary>
    public static string NormalizePathKey(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var s = input.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Trim();
        var dotSlash = "." + Path.DirectorySeparatorChar;
        while (s.StartsWith(dotSlash, StringComparison.Ordinal))
        {
            s = s[dotSlash.Length..];
        }

        s = s.TrimStart(Path.DirectorySeparatorChar);
        return s.Normalize(NormalizationForm.FormC);
    }

    private void InitializeSchema()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS SyncItems (
                        MediaKey TEXT PRIMARY KEY,
                        PhotoUrl TEXT NOT NULL,
                        LocalFileName TEXT NOT NULL,
                        FileSize INTEGER NOT NULL DEFAULT 0,
                        FileHash TEXT NOT NULL DEFAULT '',
                        TakenAt TEXT NULL,
                        UpdatedAt TEXT NOT NULL,
                        Status INTEGER NOT NULL DEFAULT 0,
                        SkipReason INTEGER NOT NULL DEFAULT 0,
                        BulkDeleteBatchId TEXT NOT NULL DEFAULT '',
                        BulkDeleteApproved INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS IX_SyncItems_LocalFileName ON SyncItems(LocalFileName);
                    CREATE INDEX IF NOT EXISTS IX_SyncItems_Status ON SyncItems(Status);

                    CREATE TABLE IF NOT EXISTS SyncState (
                        Key TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    );
                ";
                cmd.ExecuteNonQuery();
            }

            EnsureColumnExists(conn, "SyncItems", "FileHash", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, "SyncItems", "SkipReason", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumnExists(conn, "SyncItems", "BulkDeleteBatchId", "TEXT NOT NULL DEFAULT ''");
            EnsureColumnExists(conn, "SyncItems", "BulkDeleteApproved", "INTEGER NOT NULL DEFAULT 0");
        }
    }

    private static void EnsureColumnExists(SqliteConnection conn, string tableName, string columnName, string columnDef)
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = checkCmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();

        using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef};";
        alterCmd.ExecuteNonQuery();
    }

    public string? GetState(string key)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM SyncState WHERE Key = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", key);
            var res = cmd.ExecuteScalar();
            return res == null || res == DBNull.Value ? null : Convert.ToString(res);
        }
    }

    public void SetState(string key, string? value)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            if (value == null)
            {
                cmd.CommandText = "DELETE FROM SyncState WHERE Key = $key";
                cmd.Parameters.AddWithValue("$key", key);
            }
            else
            {
                cmd.CommandText = @"
                    INSERT INTO SyncState (Key, Value) VALUES ($key, $val)
                    ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                ";
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$val", value);
            }
            cmd.ExecuteNonQuery();
        }
    }

    public bool HasAnyRecords()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM SyncItems";
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    /// <summary>
    /// Bod 5: Zjistí, zda v evidenci existují položky ve stavu Downloaded (0) NEBO PendingRemoteDelete (2).
    /// </summary>
    public bool HasDownloadedOrPendingDeleteItems()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM SyncItems WHERE Status IN (0, 2)";
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    public bool Exists(string mediaKey)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM SyncItems WHERE MediaKey = $key";
            cmd.Parameters.AddWithValue("$key", mediaKey);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    public HashSet<string> GetAllKnownMediaKeys()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MediaKey FROM SyncItems";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                set.Add(reader.GetString(0));
            }
        }
        return set;
    }

    public void UpsertItem(SyncItemRecord item)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO SyncItems (MediaKey, PhotoUrl, LocalFileName, FileSize, FileHash, TakenAt, UpdatedAt, Status, SkipReason, BulkDeleteBatchId, BulkDeleteApproved)
                VALUES ($key, $url, $file, $size, $hash, $taken, $updated, $status, $skipReason, $batchId, $batchApproved)
                ON CONFLICT(MediaKey) DO UPDATE SET
                    PhotoUrl = excluded.PhotoUrl,
                    LocalFileName = excluded.LocalFileName,
                    FileSize = excluded.FileSize,
                    FileHash = excluded.FileHash,
                    TakenAt = excluded.TakenAt,
                    UpdatedAt = excluded.UpdatedAt,
                    Status = excluded.Status,
                    SkipReason = excluded.SkipReason,
                    BulkDeleteBatchId = excluded.BulkDeleteBatchId,
                    BulkDeleteApproved = excluded.BulkDeleteApproved;
            ";
            cmd.Parameters.AddWithValue("$key", item.MediaKey);
            cmd.Parameters.AddWithValue("$url", item.PhotoUrl);
            cmd.Parameters.AddWithValue("$file", NormalizePathKey(item.LocalFileName));
            cmd.Parameters.AddWithValue("$size", item.FileSize);
            cmd.Parameters.AddWithValue("$hash", item.FileHash ?? string.Empty);
            cmd.Parameters.AddWithValue("$taken", item.TakenAt?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$status", (int)item.Status);
            cmd.Parameters.AddWithValue("$skipReason", (int)item.SkipReason);
            cmd.Parameters.AddWithValue("$batchId", item.BulkDeleteBatchId ?? string.Empty);
            cmd.Parameters.AddWithValue("$batchApproved", item.BulkDeleteApproved ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateStatus(string mediaKey, SyncItemStatus status, SkipReason? skipReason = null)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            bool resetBatch = status != SyncItemStatus.PendingRemoteDelete;
            if (skipReason.HasValue)
            {
                cmd.CommandText = resetBatch
                    ? "UPDATE SyncItems SET Status = $status, SkipReason = $skipReason, BulkDeleteBatchId = '', BulkDeleteApproved = 0, UpdatedAt = $updated WHERE MediaKey = $key"
                    : "UPDATE SyncItems SET Status = $status, SkipReason = $skipReason, UpdatedAt = $updated WHERE MediaKey = $key";
                cmd.Parameters.AddWithValue("$skipReason", (int)skipReason.Value);
            }
            else
            {
                cmd.CommandText = resetBatch
                    ? "UPDATE SyncItems SET Status = $status, BulkDeleteBatchId = '', BulkDeleteApproved = 0, UpdatedAt = $updated WHERE MediaKey = $key"
                    : "UPDATE SyncItems SET Status = $status, UpdatedAt = $updated WHERE MediaKey = $key";
            }
            cmd.Parameters.AddWithValue("$status", (int)status);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$key", mediaKey);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateLocalFileName(string oldFileName, string newFileName)
    {
        var normOld = NormalizePathKey(oldFileName);
        var normNew = NormalizePathKey(newFileName);
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE SyncItems
                SET LocalFileName = $newFile, UpdatedAt = $updated
                WHERE LocalFileName = $oldFile COLLATE UNICODE_NOCASE AND Status = 0
            ";
            cmd.Parameters.AddWithValue("$newFile", normNew);
            cmd.Parameters.AddWithValue("$oldFile", normOld);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            var affected = cmd.ExecuteNonQuery();
            if (affected > 0)
            {
                Logger.Info($"Soubor přejmenován/přesunut na disku: '{normOld}' -> '{normNew}' (vazba na Google Photos zachována).");
            }
        }
    }

    public void UpdateLocalFileLocationAndHash(string mediaKey, string newRelativeFileName, string? fileHash = null)
    {
        var normNew = NormalizePathKey(newRelativeFileName);
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            if (!string.IsNullOrEmpty(fileHash))
            {
                cmd.CommandText = @"
                    UPDATE SyncItems
                    SET LocalFileName = $newFile, FileHash = $hash, Status = 0, BulkDeleteBatchId = '', BulkDeleteApproved = 0, UpdatedAt = $updated
                    WHERE MediaKey = $key
                ";
                cmd.Parameters.AddWithValue("$hash", fileHash);
            }
            else
            {
                cmd.CommandText = @"
                    UPDATE SyncItems
                    SET LocalFileName = $newFile, Status = 0, BulkDeleteBatchId = '', BulkDeleteApproved = 0, UpdatedAt = $updated
                    WHERE MediaKey = $key
                ";
            }
            cmd.Parameters.AddWithValue("$newFile", normNew);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$key", mediaKey);
            cmd.ExecuteNonQuery();
        }
    }

    public SyncItemRecord? GetByMediaKey(string mediaKey)
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT MediaKey, PhotoUrl, LocalFileName, FileSize, FileHash, TakenAt, UpdatedAt, Status, SkipReason, BulkDeleteBatchId, BulkDeleteApproved
                FROM SyncItems
                WHERE MediaKey = $key
                LIMIT 1
            ";
            cmd.Parameters.AddWithValue("$key", mediaKey);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadRecord(reader);
            }
            return null;
        }
    }

    public SyncItemRecord? GetByLocalFileName(string fileName)
    {
        var normFile = NormalizePathKey(fileName);
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT MediaKey, PhotoUrl, LocalFileName, FileSize, FileHash, TakenAt, UpdatedAt, Status, SkipReason, BulkDeleteBatchId, BulkDeleteApproved
                FROM SyncItems
                WHERE LocalFileName = $file COLLATE UNICODE_NOCASE AND Status = 0
                LIMIT 1
            ";
            cmd.Parameters.AddWithValue("$file", normFile);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadRecord(reader);
            }
            return null;
        }
    }

    public List<SyncItemRecord> GetItemsByStatus(SyncItemStatus status)
    {
        var list = new List<SyncItemRecord>();
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT MediaKey, PhotoUrl, LocalFileName, FileSize, FileHash, TakenAt, UpdatedAt, Status, SkipReason, BulkDeleteBatchId, BulkDeleteApproved
                FROM SyncItems
                WHERE Status = $status
            ";
            cmd.Parameters.AddWithValue("$status", (int)status);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadRecord(reader));
            }
        }
        return list;
    }

    /// <summary>
    /// Bod 7 + Doplněk: Vyhodnotí nebo uzamkne dávku hromadného mazání trvale v databázi.
    /// - Pokud již v DB existuje zablokovaná neschválená dávka (BulkDeleteBatchId != '' AND BulkDeleteApproved = 0),
    ///   zůstává zablokovaná i po restartu aplikace nebo částečné obnově souborů, dokud ji uživatel nepotvrdí nebo nezruší.
    /// - Pokud počet neschválených čekajících položek překročí maxAutoDeletes, přiřadí jim konkrétní BatchId v DB a zablokuje je.
    /// - Schválení platí výhradně pro položky s daným BatchId (nerozšiřuje se na další nově smazané fotky).
    /// </summary>
    public (bool IsBlocked, string BlockedBatchId, List<SyncItemRecord> BlockedItems, List<SyncItemRecord> ReadyToDeleteItems) EvaluateOrLockBulkDeleteBatch(int maxAutoDeletes)
    {
        int limit = Math.Max(1, maxAutoDeletes);
        lock (_dbLock)
        {
            var allPending = GetItemsByStatus(SyncItemStatus.PendingRemoteDelete);
            if (allPending.Count == 0)
            {
                return (false, string.Empty, [], []);
            }

            var approvedItems = allPending.Where(x => x.BulkDeleteApproved && !string.IsNullOrEmpty(x.BulkDeleteBatchId)).ToList();
            if (approvedItems.Count > 0)
            {
                var approvedBatchId = approvedItems[0].BulkDeleteBatchId;
                var sameBatchApproved = approvedItems
                    .Where(x => string.Equals(x.BulkDeleteBatchId, approvedBatchId, StringComparison.Ordinal))
                    .ToList();
                return (false, approvedBatchId, [], sameBatchApproved);
            }

            var unapprovedItems = allPending.Where(x => !x.BulkDeleteApproved).ToList();

            // Zjistit, zda už existuje dříve zablokovaná dávka v DB
            var existingBlockedBatchId = unapprovedItems
                .Select(x => x.BulkDeleteBatchId)
                .FirstOrDefault(id => !string.IsNullOrEmpty(id));

            if (!string.IsNullOrEmpty(existingBlockedBatchId))
            {
                // Existuje zablokovaná dávka -> částečná obnova souborů ani restart aplikace ji nesmí uvolnit!
                var blockedInThisBatch = unapprovedItems
                    .Where(x => string.Equals(x.BulkDeleteBatchId, existingBlockedBatchId, StringComparison.Ordinal))
                    .ToList();

                return (true, existingBlockedBatchId, blockedInThisBatch, []);
            }

            // Žádná dříve zablokovaná dávka není -> zkontrolujeme počet nových neschválených položek
            if (unapprovedItems.Count > limit)
            {
                var newBatchId = Guid.NewGuid().ToString("N");
                using var conn = OpenConnection();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    UPDATE SyncItems
                    SET BulkDeleteBatchId = $batchId, BulkDeleteApproved = 0, UpdatedAt = $updated
                    WHERE MediaKey = $key AND Status = 2
                ";
                var keyParam = cmd.Parameters.Add("$key", SqliteType.Text);
                cmd.Parameters.AddWithValue("$batchId", newBatchId);
                cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));

                foreach (var item in unapprovedItems)
                {
                    keyParam.Value = item.MediaKey;
                    cmd.ExecuteNonQuery();
                    item.BulkDeleteBatchId = newBatchId;
                    item.BulkDeleteApproved = false;
                }

                tx.Commit();
                return (true, newBatchId, unapprovedItems, []);
            }

            // Počet neschválených položek je v limitu a žádná zablokovaná dávka nečeká
            return (false, string.Empty, [], unapprovedItems);
        }
    }

    /// <summary>
    /// Bod 7 + Doplněk: Schválí ke smazání VÝHRADNĚ položky uložené v dané zablokované dávce (BatchId).
    /// </summary>
    public int ApproveBulkDeleteBatch(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return 0;

        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE SyncItems
                SET BulkDeleteApproved = 1, UpdatedAt = $updated
                WHERE Status = 2 AND BulkDeleteBatchId = $batchId
            ";
            cmd.Parameters.AddWithValue("$batchId", batchId);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Bod 7 + Doplněk: Zruší hromadné mazání VÝHRADNĚ pro položky dané zablokované dávky (Status = 2 a shodné BatchId):
    /// v Google Photos se nemažou a označí se ke stažení zpět do složky (RestoreRequested).
    /// Ostatní stažené soubory (Status = 0) nejsou dotčeny.
    /// </summary>
    public int CancelBulkDeleteBatch(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            return 0;

        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE SyncItems
                SET Status = $skippedStatus,
                    SkipReason = $unlinkedReason,
                    BulkDeleteBatchId = '',
                    BulkDeleteApproved = 0,
                    UpdatedAt = $updated
                WHERE Status = 2 AND BulkDeleteBatchId = $batchId
            ";
            cmd.Parameters.AddWithValue("$skippedStatus", (int)SyncItemStatus.SkippedInitial);
            cmd.Parameters.AddWithValue("$unlinkedReason", (int)SkipReason.RestoreRequested);
            cmd.Parameters.AddWithValue("$batchId", batchId);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            return cmd.ExecuteNonQuery();
        }
    }

    public (int downloadedCount, int deletedCount) GetStatistics()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(1) FROM SyncItems WHERE Status = 0";
            var downloaded = Convert.ToInt32(cmd1.ExecuteScalar());

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(1) FROM SyncItems WHERE Status = 3";
            var deleted = Convert.ToInt32(cmd2.ExecuteScalar());

            return (downloaded, deleted);
        }
    }

    /// <summary>
    /// Uvolní dříve přeskočené položky z důvodu časového filtru nebo výchozího stavu (nikoliv odpojené ze staré složky).
    /// </summary>
    public void ClearSkippedInitialItems()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM SyncItems
                WHERE Status = $status AND SkipReason IN (0, 1, 2)
            ";
            cmd.Parameters.AddWithValue("$status", (int)SyncItemStatus.SkippedInitial);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// P1-12: Uvolní dříve přeskočená videa, když uživatel v Nastavení dodatečně zapne stahování videí.
    /// </summary>
    public int ClearSkippedVideos()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM SyncItems
                WHERE Status = $status AND SkipReason = $videoReason
            ";
            cmd.Parameters.AddWithValue("$status", (int)SyncItemStatus.SkippedInitial);
            cmd.Parameters.AddWithValue("$videoReason", (int)SkipReason.VideoDisabled);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// P0-1: Odpojí dosavadní stažené položky od obousměrného mazání (při změně složky bez přesunu souborů).
    /// </summary>
    public int UnlinkDownloadedItemsFromDeletion()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE SyncItems
                SET Status = $skippedStatus, SkipReason = $unlinkedReason, BulkDeleteBatchId = '', BulkDeleteApproved = 0, UpdatedAt = $updated
                WHERE Status IN (0, 2)
            ";
            cmd.Parameters.AddWithValue("$skippedStatus", (int)SyncItemStatus.SkippedInitial);
            cmd.Parameters.AddWithValue("$unlinkedReason", (int)SkipReason.FolderUnlinked);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            return cmd.ExecuteNonQuery();
        }
    }

    public int RevertPendingRemoteDeletesToDownloaded()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE SyncItems
                SET Status = 0, BulkDeleteBatchId = '', BulkDeleteApproved = 0, UpdatedAt = $updated
                WHERE Status = 2
            ";
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            return cmd.ExecuteNonQuery();
        }
    }

    private static SyncItemRecord ReadRecord(SqliteDataReader reader)
    {
        return new SyncItemRecord
        {
            MediaKey = reader.GetString(0),
            PhotoUrl = reader.GetString(1),
            LocalFileName = reader.GetString(2),
            FileSize = reader.GetInt64(3),
            FileHash = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            TakenAt = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
            UpdatedAt = DateTime.Parse(reader.GetString(6)),
            Status = (SyncItemStatus)reader.GetInt32(7),
            SkipReason = reader.IsDBNull(8) ? SkipReason.None : (SkipReason)reader.GetInt32(8),
            BulkDeleteBatchId = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : string.Empty,
            BulkDeleteApproved = reader.FieldCount > 10 && !reader.IsDBNull(10) && reader.GetInt32(10) != 0
        };
    }
}
