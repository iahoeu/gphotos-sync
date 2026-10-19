using System.Text.Json;
using GPhotosSync.Data;
using GPhotosSync.Services;
using Xunit;

namespace GPhotosSync.Tests;

public class AuditLogicTests : IDisposable
{
    private readonly string _tempRoot;

    public AuditLogicTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"GPhotosSyncTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { }
    }

    // -------------------------------------------------------------------------
    // P3-17: Parsování data z aria-label (včetně přechodu roku bez uvedeného roku)
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseDateFromAriaLabel_WithExplicitYear_ParsesCorrectly()
    {
        var refTime = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var dt = GooglePhotosEngine.ParseDateFromAriaLabel("Fotka – 14. října 2025, 16:45", refTime);

        Assert.NotNull(dt);
        Assert.Equal(new DateTime(2025, 10, 14), dt!.Value.Date);
    }

    [Fact]
    public void ParseDateFromAriaLabel_WithoutYear_RollsBackToPreviousYearWhenInFuture()
    {
        // V lednu 2026 se zobrazí fotka z prosince bez uvedeného roku -> musí dostat rok 2025, ne budoucí prosinec 2026
        var refTime = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc);
        var dt = GooglePhotosEngine.ParseDateFromAriaLabel("Fotka – 28. prosince", refTime);

        Assert.NotNull(dt);
        Assert.Equal(2025, dt!.Value.Year);
        Assert.Equal(12, dt.Value.Month);
        Assert.Equal(28, dt.Value.Day);
    }

    [Fact]
    public void ParseDateFromAriaLabel_WithoutYear_KeepsCurrentYearWhenInPast()
    {
        var refTime = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc);
        var dt = GooglePhotosEngine.ParseDateFromAriaLabel("Photo - Oct 16", refTime);

        Assert.NotNull(dt);
        Assert.Equal(2026, dt!.Value.Year);
        Assert.Equal(10, dt.Value.Month);
        Assert.Equal(16, dt.Value.Day);
    }

    // -------------------------------------------------------------------------
    // P2-13: Ověření aktualizační URL a výběru assetu (pouze HTTPS a povolený Gitea server)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("https://git.iaho.eu/iaho/gphotos-sync/releases/download/v1.0.3/GPhotosSync_Setup.exe", "https://git.iaho.eu", true)]
    [InlineData("http://git.iaho.eu/iaho/gphotos-sync/releases/download/v1.0.3/GPhotosSync_Setup.exe", "https://git.iaho.eu", false)]
    [InlineData("https://evil.example.com/GPhotosSync_Setup.exe", "https://git.iaho.eu", false)]
    [InlineData("https://git.iaho.eu:8443/GPhotosSync_Setup.exe", "https://git.iaho.eu", false)]
    [InlineData("https://git.iaho.eu/GPhotosSync_Setup.exe", "http://git.iaho.eu", false)]
    public void IsAllowedUpdateUrl_EnforcesHttpsAndSameHostPort(string candidateUrl, string serverUrl, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsAllowedUpdateUrl(candidateUrl, serverUrl));
    }

    [Fact]
    public void SelectInstallerAssetUrl_PicksSetupExeOnAllowedHost_RejectsExternalOrHttp()
    {
        var json = """
        [
          {
            "name": "GPhotosSync_Setup.exe",
            "browser_download_url": "https://evil.example.com/GPhotosSync_Setup.exe"
          },
          {
            "name": "other_tool.exe",
            "browser_download_url": "https://git.iaho.eu/iaho/gphotos-sync/releases/download/v1.1.0/other_tool.exe"
          },
          {
            "name": "GPhotosSync_Setup.exe",
            "browser_download_url": "https://git.iaho.eu/iaho/gphotos-sync/releases/download/v1.1.0/GPhotosSync_Setup.exe"
          }
        ]
        """;

        using var doc = JsonDocument.Parse(json);
        var selected = UpdateService.SelectInstallerAssetUrl(doc.RootElement, "https://git.iaho.eu");

        Assert.Equal("https://git.iaho.eu/iaho/gphotos-sync/releases/download/v1.1.0/GPhotosSync_Setup.exe", selected);
    }

    // -------------------------------------------------------------------------
    // P0-1, P0-2, P0-3, P1-6, P1-11, P1-12, P3-18: Rozhodování o mazání a integrita
    // -------------------------------------------------------------------------

    [Fact]
    public void VerifyDownloadFolderIdentity_MissingFolderWithExistingDbRecords_ReturnsFalse()
    {
        var missingDir = Path.Combine(_tempRoot, "NonExistentDiskFolder");
        var cfg = new AppConfig
        {
            DownloadFolder = missingDir,
            FolderId = "folder-123"
        };

        var (ok, err) = cfg.VerifyDownloadFolderIdentity(hasExistingDownloadedRecords: true);
        Assert.False(ok);
        Assert.False(Directory.Exists(missingDir)); // P0-2: Nesmí složku znovu vytvořit!
        Assert.Contains("nebyla nalezena", err);
    }

    [Fact]
    public void VerifyDownloadFolderIdentity_MismatchedMarker_ReturnsFalse()
    {
        var folderA = Path.Combine(_tempRoot, "FolderA");
        var folderB = Path.Combine(_tempRoot, "FolderB");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);

        var cfg = new AppConfig { DownloadFolder = folderA };
        cfg.InitializeFolderMarker(folderA);
        var expectedFolderId = cfg.FolderId;

        // Simulace podvržené / jiné složky s jiným ID
        File.WriteAllText(Path.Combine(folderB, AppConfig.FolderMarkerFileName), "different-id-999");
        cfg.DownloadFolder = folderB;
        cfg.FolderId = expectedFolderId;

        var (ok, err) = cfg.VerifyDownloadFolderIdentity(hasExistingDownloadedRecords: true);
        Assert.False(ok);
        Assert.Contains("neodpovídá", err);
    }

    [Fact]
    public void DetectMissingLocalFiles_FileMovedToSubfolderOrRenamed_UpdatesPathAndDoesNotDelete()
    {
        var photosDir = Path.Combine(_tempRoot, "Photos");
        var subDir = Path.Combine(photosDir, "Upravene_2026");
        Directory.CreateDirectory(subDir);

        var cfg = new AppConfig
        {
            DownloadFolder = photosDir,
            TwoWayDelete = true,
            MaxAutoDeletesPerCycle = 50
        };
        cfg.InitializeFolderMarker(photosDir);

        var dbPath = Path.Combine(_tempRoot, "sync_move.db");
        var db = new SyncDatabase(dbPath);

        // Vytvoříme soubor přesunutý a přejmenovaný v podsložce se stejným obsahem (hash + velikost)
        var movedFilePath = Path.Combine(subDir, "prejmenovana_fotka.jpg");
        var content = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        File.WriteAllBytes(movedFilePath, content);
        var hash = ExifService.ComputeFastFileHash(movedFilePath);

        db.UpsertItem(new SyncItemRecord
        {
            MediaKey = "AF1QipMovedPhotoKey123",
            PhotoUrl = "https://photos.google.com/photo/AF1QipMovedPhotoKey123",
            LocalFileName = "puvodni_fotka.jpg",
            FileSize = content.Length,
            FileHash = hash,
            Status = SyncItemStatus.Downloaded,
            UpdatedAt = DateTime.Now
        });

        SyncEngine.DetectMissingLocalFilesForTwoWayDelete(cfg, db);

        var pendingDeletes = db.GetItemsByStatus(SyncItemStatus.PendingRemoteDelete);
        Assert.Empty(pendingDeletes);

        // Ověříme, že se v DB aktualizovala relativní cesta na podsložku
        var updated = db.GetByMediaKey("AF1QipMovedPhotoKey123");
        Assert.NotNull(updated);
        Assert.Equal(Path.Combine("Upravene_2026", "prejmenovana_fotka.jpg"), updated!.LocalFileName);
        Assert.Equal(SyncItemStatus.Downloaded, updated.Status);
    }

    [Fact]
    public void DetectMissingLocalFiles_TrulyDeletedFiles_AreMarkedPendingRemoteDelete()
    {
        var photosDir = Path.Combine(_tempRoot, "PhotosBulk");
        Directory.CreateDirectory(photosDir);

        var cfg = new AppConfig
        {
            DownloadFolder = photosDir,
            TwoWayDelete = true,
            MaxAutoDeletesPerCycle = 3
        };
        cfg.InitializeFolderMarker(photosDir);

        var dbPath = Path.Combine(_tempRoot, "sync_bulk.db");
        var db = new SyncDatabase(dbPath);

        for (int i = 1; i <= 5; i++)
        {
            db.UpsertItem(new SyncItemRecord
            {
                MediaKey = $"AF1QipBulkKey_{i}",
                PhotoUrl = $"https://photos.google.com/photo/AF1QipBulkKey_{i}",
                LocalFileName = $"missing_{i}.jpg",
                FileSize = 1024 + i,
                Status = SyncItemStatus.Downloaded,
                UpdatedAt = DateTime.Now.AddMinutes(-5)
            });
        }

        SyncEngine.DetectMissingLocalFilesForTwoWayDelete(cfg, db);

        var pendingDeletes = db.GetItemsByStatus(SyncItemStatus.PendingRemoteDelete);
        Assert.Equal(5, pendingDeletes.Count);
        Assert.True(pendingDeletes.Count > cfg.MaxAutoDeletesPerCycle);
    }

    [Fact]
    public void IsPhotoUnavailablePage_DoesNotFalsePositiveOn404InBodyText()
    {
        // P1-6: Fotka s názvem IMG_0404.jpg má aktivní prohlížeč -> nesmí být vyhodnocena jako smazaná 404
        Assert.False(GooglePhotosEngine.IsPhotoUnavailablePage(
            currentUrl: "https://photos.google.com/photo/AF1QipTestKey",
            mediaKey: "AF1QipTestKey",
            pageTitle: "Fotky Google",
            headingText: "IMG_0404.jpg",
            hasActivePhotoViewerOrDeleteButton: true));

        // Skutečná chybová stránka 404 bez prohlížeče a bez tlačítka Smazat
        Assert.True(GooglePhotosEngine.IsPhotoUnavailablePage(
            currentUrl: "https://photos.google.com/photo/AF1QipTestKey",
            mediaKey: "AF1QipTestKey",
            pageTitle: "Chyba 404 (Nenalezeno)!!1",
            headingText: "404. That's an error.",
            hasActivePhotoViewerOrDeleteButton: false));
    }

    [Fact]
    public void ValidateDownloadedMediaFile_RejectsStaticJpegOrHtmlWhenVideoExpected()
    {
        var fakeVideoJpg = Path.Combine(_tempRoot, "fake_video.mp4");
        File.WriteAllBytes(fakeVideoJpg, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 0 });

        Assert.False(ExifService.ValidateDownloadedMediaFile(fakeVideoJpg, "fake_video.mp4", expectedVideo: true).IsValid);
        Assert.True(ExifService.ValidateDownloadedMediaFile(fakeVideoJpg, "fake_video.jpg", expectedVideo: false).IsValid);

        var validMp4 = Path.Combine(_tempRoot, "real_video.mp4");
        File.WriteAllBytes(validMp4, new byte[] { 0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' });

        Assert.True(ExifService.ValidateDownloadedMediaFile(validMp4, "real_video.mp4", expectedVideo: true).IsValid);
    }

    [Fact]
    public void ClearSkippedVideos_ResetsOnlyVideoDisabledItems_PreservesBaselineAndCutoff()
    {
        var dbPath = Path.Combine(_tempRoot, "sync_videos.db");
        var db = new SyncDatabase(dbPath);

        db.UpsertItem(new SyncItemRecord
        {
            MediaKey = "key_video",
            PhotoUrl = "https://photos.google.com/photo/key_video",
            LocalFileName = "",
            Status = SyncItemStatus.SkippedInitial,
            SkipReason = SkipReason.VideoDisabled,
            UpdatedAt = DateTime.Now
        });
        db.UpsertItem(new SyncItemRecord
        {
            MediaKey = "key_baseline",
            PhotoUrl = "https://photos.google.com/photo/key_baseline",
            LocalFileName = "",
            Status = SyncItemStatus.SkippedInitial,
            SkipReason = SkipReason.Baseline,
            UpdatedAt = DateTime.Now
        });
        db.UpsertItem(new SyncItemRecord
        {
            MediaKey = "key_cutoff",
            PhotoUrl = "https://photos.google.com/photo/key_cutoff",
            LocalFileName = "",
            Status = SyncItemStatus.SkippedInitial,
            SkipReason = SkipReason.CutoffDate,
            UpdatedAt = DateTime.Now
        });

        int cleared = db.ClearSkippedVideos();
        Assert.Equal(1, cleared);
        Assert.Null(db.GetByMediaKey("key_video"));
        Assert.NotNull(db.GetByMediaKey("key_baseline"));
        Assert.NotNull(db.GetByMediaKey("key_cutoff"));
    }

    [Fact]
    public void SyncDatabase_UnicodeNoCaseCollation_MatchesCzechDiacriticsCaseInsensitively()
    {
        var dbPath = Path.Combine(_tempRoot, "sync_czech.db");
        var db = new SyncDatabase(dbPath);

        db.UpsertItem(new SyncItemRecord
        {
            MediaKey = "AF1QipCzechKey",
            PhotoUrl = "https://photos.google.com/photo/AF1QipCzechKey",
            LocalFileName = "Žluťoučký_kůň_Šumava_ČeskýKrumlov.jpg",
            FileSize = 5000,
            Status = SyncItemStatus.Downloaded,
            UpdatedAt = DateTime.Now
        });

        var found = db.GetByLocalFileName("žluťoučký_kůň_šumava_českýkrumlov.JPG");
        Assert.NotNull(found);
        Assert.Equal("AF1QipCzechKey", found!.MediaKey);
    }

    // -------------------------------------------------------------------------
    // 2. KOLO AUDITU (Claude + ChatGPT + Doplněk): Testy bodů 1, 2, 6, 7
    // -------------------------------------------------------------------------

    [Fact]
    public void ComputeFastFileHash_HashesEntireFileAbove8MiB_DetectsMiddleByteChange()
    {
        // Bod 1: Hash se musí počítat z celého souboru, nikoliv jen ze začátku a konce nad 8 MiB
        int size = 9 * 1024 * 1024; // 9 MiB
        var fileA = Path.Combine(_tempRoot, "largeA.bin");
        var fileB = Path.Combine(_tempRoot, "largeB.bin");

        var buffer = new byte[size];
        buffer[100] = 0xAA;
        buffer[size / 2] = 0x11; // Uprostřed souboru (mimo prvních i posledních 4 MiB)
        File.WriteAllBytes(fileA, buffer);

        buffer[size / 2] = 0x22; // Změna jediného bajtu uprostřed
        File.WriteAllBytes(fileB, buffer);

        var hashA = ExifService.ComputeFastFileHash(fileA);
        var hashB = ExifService.ComputeFastFileHash(fileB);

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void DetectMissingLocalFiles_HashTakesPriorityOverNameAndSize()
    {
        // Bod 1: Hash musí mít přednost před názvem + velikostí
        var photosDir = Path.Combine(_tempRoot, "PhotosHashPriority");
        var subWrong = Path.Combine(photosDir, "SubWrong");
        var subRight = Path.Combine(photosDir, "SubRight");
        Directory.CreateDirectory(subWrong);
        Directory.CreateDirectory(subRight);

        var cfg = new AppConfig
        {
            DownloadFolder = photosDir,
            TwoWayDelete = true,
            MaxAutoDeletesPerCycle = 50
        };
        cfg.InitializeFolderMarker(photosDir);

        var dbPath = Path.Combine(_tempRoot, "sync_hash_prio.db");
        var db = new SyncDatabase(dbPath);

        // V SubWrong je soubor se STEJNÝM názvem a STEJNOU velikostí, ale JINÝM obsahem (hashem)
        var wrongPath = Path.Combine(subWrong, "IMG_100.jpg");
        var wrongBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 99 };
        File.WriteAllBytes(wrongPath, wrongBytes);

        // V SubRight je přejmenovaný soubor se SPRÁVNÝM obsahem (hashem) a velikostí
        var rightPath = Path.Combine(subRight, "renamed_holiday.jpg");
        var rightBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 42 };
        File.WriteAllBytes(rightPath, rightBytes);
        var rightHash = ExifService.ComputeFastFileHash(rightPath);

        db.UpsertItem(new SyncItemRecord
        {
            MediaKey = "AF1QipHashPrioKey",
            PhotoUrl = "https://photos.google.com/photo/AF1QipHashPrioKey",
            LocalFileName = "IMG_100.jpg",
            FileSize = rightBytes.Length,
            FileHash = rightHash,
            Status = SyncItemStatus.Downloaded,
            UpdatedAt = DateTime.Now.AddMinutes(-10)
        });

        SyncEngine.DetectMissingLocalFilesForTwoWayDelete(cfg, db);

        var updated = db.GetByMediaKey("AF1QipHashPrioKey");
        Assert.NotNull(updated);
        Assert.Equal(SyncItemStatus.Downloaded, updated!.Status);
        Assert.Equal(Path.Combine("SubRight", "renamed_holiday.jpg"), updated.LocalFileName);
    }

    [Fact]
    public void DetectMissingLocalFiles_AmbiguousCandidatesWithoutHash_DoesNotDeleteAndReports()
    {
        // Bod 1: Nejednoznačná shoda (víc kandidátů, chybí hash) = nemazat a nahlásit
        var photosDir = Path.Combine(_tempRoot, "PhotosAmbiguous");
        var sub1 = Path.Combine(photosDir, "A");
        var sub2 = Path.Combine(photosDir, "B");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);

        var cfg = new AppConfig
        {
            DownloadFolder = photosDir,
            TwoWayDelete = true,
            MaxAutoDeletesPerCycle = 50
        };
        cfg.InitializeFolderMarker(photosDir);

        var dbPath = Path.Combine(_tempRoot, "sync_ambig.db");
        var db = new SyncDatabase(dbPath);

        File.WriteAllBytes(Path.Combine(sub1, "cand1.jpg"), new byte[128]);
        File.WriteAllBytes(Path.Combine(sub2, "cand2.jpg"), new byte[128]);

        db.UpsertItem(new SyncItemRecord
        {
            MediaKey = "AF1QipNoHashAmbiguous",
            PhotoUrl = "https://photos.google.com/photo/AF1QipNoHashAmbiguous",
            LocalFileName = "original_missing.jpg",
            FileSize = 128,
            FileHash = "", // Chybí hash
            Status = SyncItemStatus.Downloaded,
            UpdatedAt = DateTime.Now.AddMinutes(-10)
        });

        var res = SyncEngine.DetectMissingLocalFilesForTwoWayDelete(cfg, db);

        Assert.Equal(0, res.MissingMarkedCount);
        Assert.Equal(1, res.AmbiguousSkippedCount);
        Assert.Empty(db.GetItemsByStatus(SyncItemStatus.PendingRemoteDelete));
        Assert.Equal(SyncItemStatus.Downloaded, db.GetByMediaKey("AF1QipNoHashAmbiguous")!.Status);
    }

    [Theory]
    [InlineData(@"C:\Photos", @"C:\Photos", @"C:\Users\Test\AppData\Local\GPhotosSync", false)] // Shodná složka
    [InlineData(@"C:\Photos\App", @"C:\Photos", @"C:\Users\Test\AppData\Local\GPhotosSync", false)] // Instalační složka je podsložkou fotek
    [InlineData(@"C:\App", @"C:\App\Photos", @"C:\Users\Test\AppData\Local\GPhotosSync", false)] // Složka fotek je podsložkou instalace
    [InlineData(@"C:\App", @"C:\Users\Test\AppData\Local\GPhotosSync\Fotky", @"C:\Users\Test\AppData\Local\GPhotosSync", false)] // Fotky uvnitř LocalAppData\GPhotosSync
    [InlineData(@"C:\", @"D:\Photos", @"C:\Users\Test\AppData\Local\GPhotosSync", false)] // Kořen disku
    [InlineData(@"C:\Users\Test\AppData\Local\Programs\GPhotosSync", @"D:\MojeFotky", @"C:\Users\Test\AppData\Local\GPhotosSync", true)] // Oddělené bezpečné složky
    public void InstallerValidateInstallationPaths_BlocksOverlappingAndRootPaths(
        string installDir,
        string photosDir,
        string appDataDir,
        bool expectedValid)
    {
        // Bod 2: Instalátor nesmí povolit překrývající se složky ani kořen disku
        var check = GPhotosSync.Installer.InstallerForm.ValidateInstallationPaths(installDir, photosDir, appDataDir);
        Assert.Equal(expectedValid, check.IsValid);
    }

    [Fact]
    public void NormalizePathKey_PreservesLeadingDotsInFileNames_StripsOnlyDotSlashPrefix()
    {
        // Bod 6: NormalizePathKey nesmí mazat úvodní tečky z názvu souboru (.hidden.jpg), pouze prefix .\ nebo ./
        Assert.Equal(".hidden.jpg", SyncDatabase.NormalizePathKey(".hidden.jpg"));
        Assert.Equal("..double_dot.jpg", SyncDatabase.NormalizePathKey("..double_dot.jpg"));
        Assert.Equal(".hidden.jpg", SyncDatabase.NormalizePathKey(@".\.hidden.jpg"));
        Assert.Equal(".hidden.jpg", SyncDatabase.NormalizePathKey("./.hidden.jpg"));
        Assert.Equal(Path.Combine("Podslozka", ".hidden.jpg"), SyncDatabase.NormalizePathKey(@".\Podslozka\.hidden.jpg"));
    }

    [Fact]
    public void BulkDeleteSafeguard_PersistsBatchInDb_SurvivesPartialRestore_AndScopesApproveAndCancel()
    {
        // Bod 7 + Doplněk:
        // - Blokovanou dávku uložit trvale do DB
        // - Částečná obnova souborů pojistku neuvolní
        // - Potvrzení platí jen pro uloženou dávku (nerozšíří se na další smazané fotky)
        // - Zrušení odpojí jen tuto dávku (Status 2), nikoliv Status 0
        var dbPath = Path.Combine(_tempRoot, "sync_batch_persist.db");
        var db = new SyncDatabase(dbPath);

        // 1 položka zůstává normálně stažená (Status 0)
        db.UpsertItem(new SyncItemRecord
        {
            MediaKey = "keep_downloaded_1",
            PhotoUrl = "https://photos.google.com/photo/keep_downloaded_1",
            LocalFileName = "keep.jpg",
            Status = SyncItemStatus.Downloaded,
            UpdatedAt = DateTime.Now
        });

        // 4 položky odebrány najednou (limit pojistky nastavíme na 3)
        for (int i = 1; i <= 4; i++)
        {
            db.UpsertItem(new SyncItemRecord
            {
                MediaKey = $"batch_item_{i}",
                PhotoUrl = $"https://photos.google.com/photo/batch_item_{i}",
                LocalFileName = $"del_{i}.jpg",
                Status = SyncItemStatus.PendingRemoteDelete,
                UpdatedAt = DateTime.Now
            });
        }

        var eval1 = db.EvaluateOrLockBulkDeleteBatch(maxAutoDeletes: 3);
        Assert.True(eval1.IsBlocked);
        Assert.False(string.IsNullOrWhiteSpace(eval1.BlockedBatchId));
        Assert.Equal(4, eval1.BlockedItems.Count);
        string batchId = eval1.BlockedBatchId!;

        // Simulace částečné obnovy 2 souborů na disku (zůstávají jen 2 < maxAutoDeletes 3) -> pojistka se NESMÍ uvolnit!
        db.UpdateStatus("batch_item_1", SyncItemStatus.Downloaded);
        db.UpdateStatus("batch_item_2", SyncItemStatus.Downloaded);

        // Simulace restartu aplikace (nová instance SyncDatabase nad stejným souborem)
        var dbAfterRestart = new SyncDatabase(dbPath);
        var evalAfterPartialRestore = dbAfterRestart.EvaluateOrLockBulkDeleteBatch(maxAutoDeletes: 3);
        Assert.True(evalAfterPartialRestore.IsBlocked);
        Assert.Equal(batchId, evalAfterPartialRestore.BlockedBatchId);
        Assert.Equal(2, evalAfterPartialRestore.BlockedItems.Count);

        // Mezitím přibyla 1 další smazaná fotka mimo zablokovanou dávku
        dbAfterRestart.UpsertItem(new SyncItemRecord
        {
            MediaKey = "extra_new_delete",
            PhotoUrl = "https://photos.google.com/photo/extra_new_delete",
            LocalFileName = "extra.jpg",
            Status = SyncItemStatus.PendingRemoteDelete,
            UpdatedAt = DateTime.Now
        });

        // Uživatel potvrdí zablokovanou dávku -> schválí se POUZE 2 položky v této dávce, nikoliv extra_new_delete!
        int approvedCount = dbAfterRestart.ApproveBulkDeleteBatch(batchId);
        Assert.Equal(2, approvedCount);

        var evalApproved = dbAfterRestart.EvaluateOrLockBulkDeleteBatch(maxAutoDeletes: 3);
        Assert.False(evalApproved.IsBlocked);
        Assert.Equal(2, evalApproved.ReadyToDeleteItems.Count);
        Assert.DoesNotContain(evalApproved.ReadyToDeleteItems, i => i.MediaKey == "extra_new_delete");

        // Test zrušení dávky: odpojí jen danou dávku, nikdy nezasáhne Status 0 (keep_downloaded_1) ani extra_new_delete
        int canceledCount = dbAfterRestart.CancelBulkDeleteBatch(batchId);
        Assert.Equal(2, canceledCount);
        Assert.Equal(SyncItemStatus.Downloaded, dbAfterRestart.GetByMediaKey("keep_downloaded_1")!.Status);
        Assert.Equal(SyncItemStatus.PendingRemoteDelete, dbAfterRestart.GetByMediaKey("extra_new_delete")!.Status);
        Assert.Equal(SyncItemStatus.SkippedInitial, dbAfterRestart.GetByMediaKey("batch_item_3")!.Status);
    }
}
