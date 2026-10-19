using Microsoft.Data.Sqlite;

namespace GPhotosSync.Data;

public enum SyncItemStatus
{
    Downloaded = 0,
    SkippedInitial = 1,
    PendingRemoteDelete = 2,
    DeletedLocalAndRemote = 3
}

public class SyncItemRecord
{
    public string MediaKey { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;
    public string LocalFileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime? TakenAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public SyncItemStatus Status { get; set; }
}

public class SyncDatabase
{
    private readonly string _connectionString;
    private readonly object _dbLock = new();

    public SyncDatabase()
    {
        Directory.CreateDirectory(AppConfig.AppDataFolder);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppConfig.DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        InitializeSchema();
    }

    private void InitializeSchema()
    {
        lock (_dbLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS SyncItems (
                    MediaKey TEXT PRIMARY KEY,
                    PhotoUrl TEXT NOT NULL,
                    LocalFileName TEXT NOT NULL,
                    FileSize INTEGER NOT NULL DEFAULT 0,
                    TakenAt TEXT NULL,
                    UpdatedAt TEXT NOT NULL,
                    Status INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS IX_SyncItems_LocalFileName ON SyncItems(LocalFileName);
                CREATE INDEX IF NOT EXISTS IX_SyncItems_Status ON SyncItems(Status);
            ";
            cmd.ExecuteNonQuery();
        }
    }

    public bool Exists(string mediaKey)
    {
        lock (_dbLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM SyncItems WHERE MediaKey = $key";
            cmd.Parameters.AddWithValue("$key", mediaKey);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    public void UpsertItem(SyncItemRecord item)
    {
        lock (_dbLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO SyncItems (MediaKey, PhotoUrl, LocalFileName, FileSize, TakenAt, UpdatedAt, Status)
                VALUES ($key, $url, $file, $size, $taken, $updated, $status)
                ON CONFLICT(MediaKey) DO UPDATE SET
                    PhotoUrl = excluded.PhotoUrl,
                    LocalFileName = excluded.LocalFileName,
                    FileSize = excluded.FileSize,
                    TakenAt = excluded.TakenAt,
                    UpdatedAt = excluded.UpdatedAt,
                    Status = excluded.Status;
            ";
            cmd.Parameters.AddWithValue("$key", item.MediaKey);
            cmd.Parameters.AddWithValue("$url", item.PhotoUrl);
            cmd.Parameters.AddWithValue("$file", item.LocalFileName);
            cmd.Parameters.AddWithValue("$size", item.FileSize);
            cmd.Parameters.AddWithValue("$taken", item.TakenAt?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$status", (int)item.Status);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateStatus(string mediaKey, SyncItemStatus status)
    {
        lock (_dbLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE SyncItems SET Status = $status, UpdatedAt = $updated WHERE MediaKey = $key";
            cmd.Parameters.AddWithValue("$status", (int)status);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            cmd.Parameters.AddWithValue("$key", mediaKey);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateLocalFileName(string oldFileName, string newFileName)
    {
        lock (_dbLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE SyncItems
                SET LocalFileName = $newFile, UpdatedAt = $updated
                WHERE LOWER(LocalFileName) = LOWER($oldFile) AND Status = 0
            ";
            cmd.Parameters.AddWithValue("$newFile", newFileName);
            cmd.Parameters.AddWithValue("$oldFile", oldFileName);
            cmd.Parameters.AddWithValue("$updated", DateTime.Now.ToString("O"));
            var affected = cmd.ExecuteNonQuery();
            if (affected > 0)
            {
                Logger.Info($"Soubor přejmenován na disku: '{oldFileName}' -> '{newFileName}' (vazba na Google Photos zachována).");
            }
        }
    }

    public SyncItemRecord? GetByLocalFileName(string fileName)
    {
        lock (_dbLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT MediaKey, PhotoUrl, LocalFileName, FileSize, TakenAt, UpdatedAt, Status
                FROM SyncItems
                WHERE LOWER(LocalFileName) = LOWER($file) AND Status = 0
                LIMIT 1
            ";
            cmd.Parameters.AddWithValue("$file", fileName);
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
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT MediaKey, PhotoUrl, LocalFileName, FileSize, TakenAt, UpdatedAt, Status
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

    public (int downloadedCount, int deletedCount) GetStatistics()
    {
        lock (_dbLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(1) FROM SyncItems WHERE Status = 0";
            var downloaded = Convert.ToInt32(cmd1.ExecuteScalar());

            using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(1) FROM SyncItems WHERE Status = 3";
            var deleted = Convert.ToInt32(cmd2.ExecuteScalar());

            return (downloaded, deleted);
        }
    }

    public void ClearSkippedInitialItems()
    {
        lock (_dbLock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM SyncItems WHERE Status = $status";
            cmd.Parameters.AddWithValue("$status", (int)SyncItemStatus.SkippedInitial);
            cmd.ExecuteNonQuery();
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
            TakenAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
            UpdatedAt = DateTime.Parse(reader.GetString(5)),
            Status = (SyncItemStatus)reader.GetInt32(6)
        };
    }
}
