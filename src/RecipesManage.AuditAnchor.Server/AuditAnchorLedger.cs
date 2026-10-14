using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RecipesManage.AuditAnchor.Protocol;

namespace RecipesManage.AuditAnchor.Server;

public sealed class AuditAnchorSnapshotConflictException(string message) : Exception(message);

public sealed class AuditAnchorLedger
{
    private readonly string _connectionString;
    private readonly IAuditAnchorReceiptSigner _signer;
    private readonly TimeProvider _timeProvider;

    public AuditAnchorLedger(
        string databasePath,
        IAuditAnchorReceiptSigner signer,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("An anchor ledger database path is required.", nameof(databasePath));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        _signer = signer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout=10000;
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS anchor_receipts (
                source_id TEXT NOT NULL,
                sequence INTEGER NOT NULL CHECK (sequence > 0),
                snapshot_id TEXT NOT NULL,
                manifest_json TEXT NOT NULL,
                manifest_sha256 TEXT NOT NULL,
                previous_receipt_hash TEXT NULL,
                receipt_json TEXT NOT NULL,
                receipt_hash TEXT NOT NULL,
                received_at_utc TEXT NOT NULL,
                PRIMARY KEY (source_id, sequence),
                UNIQUE (source_id, snapshot_id)
            );
            CREATE TRIGGER IF NOT EXISTS anchor_receipts_no_update
                BEFORE UPDATE ON anchor_receipts
                BEGIN SELECT RAISE(ABORT, 'ANCHOR_APPEND_ONLY'); END;
            CREATE TRIGGER IF NOT EXISTS anchor_receipts_no_delete
                BEFORE DELETE ON anchor_receipts
                BEGIN SELECT RAISE(ABORT, 'ANCHOR_APPEND_ONLY'); END;
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AuditAnchorReceipt> AcceptAsync(AuditAnchorManifest manifest, CancellationToken ct = default)
    {
        var canonicalManifest = AuditAnchorProtocol.CanonicalManifest(manifest);
        var manifestHash = Convert.ToHexString(SHA256.HashData(canonicalManifest)).ToLowerInvariant();
        var sourceId = Guid.Parse(manifest.SourceId).ToString("D").ToLowerInvariant();
        var snapshotId = Guid.Parse(manifest.SnapshotId).ToString("D").ToLowerInvariant();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT manifest_sha256, receipt_json
                FROM anchor_receipts
                WHERE source_id = $source AND snapshot_id = $snapshot;
                """;
            existing.Parameters.AddWithValue("$source", sourceId);
            existing.Parameters.AddWithValue("$snapshot", snapshotId);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var priorManifestHash = reader.GetString(0);
                var existingReceipt = JsonSerializer.Deserialize<AuditAnchorReceipt>(reader.GetString(1))
                    ?? throw new InvalidDataException("Stored audit anchor receipt is invalid.");
                await reader.DisposeAsync();
                if (!string.Equals(priorManifestHash, manifestHash, StringComparison.Ordinal))
                    throw new AuditAnchorSnapshotConflictException("Snapshot ID was already used for different content.");
                await transaction.CommitAsync(ct);
                return existingReceipt;
            }
        }

        long sequence = 1;
        string? previousReceiptHash = null;
        await using (var latest = connection.CreateCommand())
        {
            latest.Transaction = transaction;
            latest.CommandText = """
                SELECT sequence, receipt_hash
                FROM anchor_receipts
                WHERE source_id = $source
                ORDER BY sequence DESC
                LIMIT 1;
                """;
            latest.Parameters.AddWithValue("$source", sourceId);
            await using var reader = await latest.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                sequence = checked(reader.GetInt64(0) + 1);
                previousReceiptHash = reader.GetString(1);
            }
        }

        var receipt = AuditAnchorProtocol.SignReceipt(
            sourceId,
            sequence,
            snapshotId,
            manifestHash,
            previousReceiptHash,
            _timeProvider.GetUtcNow(),
            _signer.KeyId,
            payload => _signer.Sign(payload));
        var manifestJson = JsonSerializer.Serialize(manifest);
        var receiptJson = JsonSerializer.Serialize(receipt);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO anchor_receipts
                    (source_id, sequence, snapshot_id, manifest_json, manifest_sha256,
                     previous_receipt_hash, receipt_json, receipt_hash, received_at_utc)
                VALUES
                    ($source, $sequence, $snapshot, $manifest, $manifestHash,
                     $previousHash, $receipt, $receiptHash, $receivedAt);
                """;
            insert.Parameters.AddWithValue("$source", sourceId);
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$snapshot", snapshotId);
            insert.Parameters.AddWithValue("$manifest", manifestJson);
            insert.Parameters.AddWithValue("$manifestHash", manifestHash);
            insert.Parameters.AddWithValue("$previousHash", (object?)previousReceiptHash ?? DBNull.Value);
            insert.Parameters.AddWithValue("$receipt", receiptJson);
            insert.Parameters.AddWithValue("$receiptHash", receipt.ReceiptHash);
            insert.Parameters.AddWithValue("$receivedAt",
                DateTimeOffset.ParseExact(receipt.ReceivedAtUtc, "O", CultureInfo.InvariantCulture).ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return receipt;
    }

    public async Task<AuditAnchorReceiptPage> ReadPageAsync(
        string sourceId, long afterSequence, int take, long? throughSequence = null,
        CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(sourceId, "D", out var parsedSourceId))
            throw new ArgumentException("Source ID must be a GUID.", nameof(sourceId));
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(take));
        if (throughSequence < 0) throw new ArgumentOutOfRangeException(nameof(throughSequence));
        var canonicalSource = parsedSourceId.ToString("D").ToLowerInvariant();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        long latestSequence;
        await using (var latest = connection.CreateCommand())
        {
            latest.Transaction = (SqliteTransaction)transaction;
            latest.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM anchor_receipts WHERE source_id = $source;";
            latest.Parameters.AddWithValue("$source", canonicalSource);
            var storedLatestSequence = (long)(await latest.ExecuteScalarAsync(ct) ?? 0L);
            if (throughSequence > storedLatestSequence)
                throw new ArgumentOutOfRangeException(nameof(throughSequence));
            latestSequence = throughSequence ?? storedLatestSequence;
        }

        var entries = new List<AuditAnchorReceiptEntry>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = (SqliteTransaction)transaction;
            query.CommandText = """
                SELECT manifest_json, receipt_json
                FROM anchor_receipts
                WHERE source_id = $source AND sequence > $after AND sequence <= $through
                ORDER BY sequence
                LIMIT $take;
                """;
            query.Parameters.AddWithValue("$source", canonicalSource);
            query.Parameters.AddWithValue("$after", afterSequence);
            query.Parameters.AddWithValue("$through", latestSequence);
            query.Parameters.AddWithValue("$take", take);
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var manifest = JsonSerializer.Deserialize<AuditAnchorManifest>(reader.GetString(0))
                    ?? throw new InvalidDataException("Stored audit anchor manifest is invalid.");
                var receipt = JsonSerializer.Deserialize<AuditAnchorReceipt>(reader.GetString(1))
                    ?? throw new InvalidDataException("Stored audit anchor receipt is invalid.");
                entries.Add(new AuditAnchorReceiptEntry(manifest, receipt));
            }
        }
        await transaction.CommitAsync(ct);
        return new AuditAnchorReceiptPage(canonicalSource, latestSequence, entries);
    }
}
