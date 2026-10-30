using Microsoft.Data.Sqlite;

namespace HsaReceipts;

public sealed record Receipt(
    string Id,
    string Provider,
    string Patient,
    string ServiceDate,
    long Cents,
    string Notes,
    string FileName,
    string ContentType,
    string UploadedBy,
    string CreatedAt,
    string? ReimbursedDate);

public sealed record CreateReceiptResult(Receipt Receipt, bool Created);

public sealed class ReceiptStore
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private string ConnectionString { get; }
    public string Root { get; }

    public ReceiptStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(Path.Combine(root, "receipts"));
        Directory.CreateDirectory(Path.Combine(root, "staging"));
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "receipts.db"),
            ForeignKeys = true,
            Pooling = false
        }.ToString();

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS receipts (
                id TEXT PRIMARY KEY,
                provider TEXT NOT NULL,
                patient TEXT NOT NULL,
                service_date TEXT NOT NULL,
                cents INTEGER NOT NULL CHECK(cents > 0),
                notes TEXT NOT NULL,
                file_name TEXT NOT NULL,
                content_type TEXT NOT NULL,
                uploaded_by TEXT NOT NULL,
                created_at TEXT NOT NULL,
                reimbursed_date TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS receipt_submissions (
                user_key TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                digest TEXT NOT NULL,
                receipt_id TEXT NOT NULL REFERENCES receipts(id),
                created_at TEXT NOT NULL,
                PRIMARY KEY (user_key, idempotency_key)
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(ConnectionString);
        db.Open();
        return db;
    }

    public List<Receipt> All()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id, provider, patient, service_date, cents, notes, file_name,
                   content_type, uploaded_by, created_at, reimbursed_date
            FROM receipts
            ORDER BY service_date DESC, created_at DESC
            """;
        using var reader = command.ExecuteReader();
        var result = new List<Receipt>();
        while (reader.Read())
        {
            result.Add(ReadReceipt(reader));
        }

        return result;
    }

    public Receipt? Find(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id, provider, patient, service_date, cents, notes, file_name,
                   content_type, uploaded_by, created_at, reimbursed_date
            FROM receipts
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadReceipt(reader) : null;
    }

    public string FilePath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
        {
            throw new ArgumentException("Invalid receipt ID.", nameof(id));
        }

        return Path.Combine(Root, "receipts", id);
    }

    public string NewStagingPath() =>
        Path.Combine(Root, "staging", Guid.NewGuid().ToString("N"));

    public async Task<CreateReceiptResult> CreateAsync(
        Receipt receipt,
        string userKey,
        string idempotencyKey,
        string digest,
        string stagedFilePath,
        CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        var finalPath = FilePath(receipt.Id);
        var moved = false;
        try
        {
            using var db = Open();
            using var transaction = db.BeginTransaction();
            Receipt? existingReceipt = null;

            using (var existingCommand = db.CreateCommand())
            {
                existingCommand.Transaction = transaction;
                existingCommand.CommandText = """
                    SELECT s.digest, r.id, r.provider, r.patient, r.service_date, r.cents,
                           r.notes, r.file_name, r.content_type, r.uploaded_by,
                           r.created_at, r.reimbursed_date
                    FROM receipt_submissions s
                    JOIN receipts r ON r.id = s.receipt_id
                    WHERE s.user_key = $userKey AND s.idempotency_key = $idempotencyKey
                    """;
                existingCommand.Parameters.AddWithValue("$userKey", userKey);
                existingCommand.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
                using var reader = existingCommand.ExecuteReader();
                if (reader.Read())
                {
                    if (!string.Equals(reader.GetString(0), digest, StringComparison.Ordinal))
                    {
                        throw new IdempotencyConflictException();
                    }

                    existingReceipt = ReadReceipt(reader, 1);
                }
            }

            if (existingReceipt is not null)
            {
                transaction.Commit();
                File.Delete(stagedFilePath);
                return new CreateReceiptResult(existingReceipt, Created: false);
            }

            File.Move(stagedFilePath, finalPath);
            moved = true;

            using (var insertReceipt = db.CreateCommand())
            {
                insertReceipt.Transaction = transaction;
                insertReceipt.CommandText = """
                    INSERT INTO receipts
                        (id, provider, patient, service_date, cents, notes, file_name,
                         content_type, uploaded_by, created_at, reimbursed_date)
                    VALUES
                        ($id, $provider, $patient, $date, $cents, $notes, $file,
                         $type, $by, $created, NULL)
                    """;
                insertReceipt.Parameters.AddWithValue("$id", receipt.Id);
                insertReceipt.Parameters.AddWithValue("$provider", receipt.Provider);
                insertReceipt.Parameters.AddWithValue("$patient", receipt.Patient);
                insertReceipt.Parameters.AddWithValue("$date", receipt.ServiceDate);
                insertReceipt.Parameters.AddWithValue("$cents", receipt.Cents);
                insertReceipt.Parameters.AddWithValue("$notes", receipt.Notes);
                insertReceipt.Parameters.AddWithValue("$file", receipt.FileName);
                insertReceipt.Parameters.AddWithValue("$type", receipt.ContentType);
                insertReceipt.Parameters.AddWithValue("$by", receipt.UploadedBy);
                insertReceipt.Parameters.AddWithValue("$created", receipt.CreatedAt);
                insertReceipt.ExecuteNonQuery();
            }

            using (var insertSubmission = db.CreateCommand())
            {
                insertSubmission.Transaction = transaction;
                insertSubmission.CommandText = """
                    INSERT INTO receipt_submissions
                        (user_key, idempotency_key, digest, receipt_id, created_at)
                    VALUES
                        ($userKey, $idempotencyKey, $digest, $receiptId, $created)
                    """;
                insertSubmission.Parameters.AddWithValue("$userKey", userKey);
                insertSubmission.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
                insertSubmission.Parameters.AddWithValue("$digest", digest);
                insertSubmission.Parameters.AddWithValue("$receiptId", receipt.Id);
                insertSubmission.Parameters.AddWithValue("$created", receipt.CreatedAt);
                insertSubmission.ExecuteNonQuery();
            }

            transaction.Commit();
            return new CreateReceiptResult(receipt, Created: true);
        }
        catch
        {
            if (moved && File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            if (File.Exists(stagedFilePath))
            {
                File.Delete(stagedFilePath);
            }

            throw;
        }
        finally
        {
            writeLock.Release();
        }
    }

    public bool Edit(
        string id,
        string provider,
        string patient,
        string date,
        long cents,
        string notes)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE receipts
            SET provider = $provider,
                patient = $patient,
                service_date = $date,
                cents = $cents,
                notes = $notes
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$patient", patient);
        command.Parameters.AddWithValue("$date", date);
        command.Parameters.AddWithValue("$cents", cents);
        command.Parameters.AddWithValue("$notes", notes);
        return command.ExecuteNonQuery() == 1;
    }

    public bool Reimburse(string id, string? date)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE receipts SET reimbursed_date = $date WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$date", (object?)date ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    private static Receipt ReadReceipt(SqliteDataReader reader, int offset = 0) =>
        new(
            reader.GetString(offset),
            reader.GetString(offset + 1),
            reader.GetString(offset + 2),
            reader.GetString(offset + 3),
            reader.GetInt64(offset + 4),
            reader.GetString(offset + 5),
            reader.GetString(offset + 6),
            reader.GetString(offset + 7),
            reader.GetString(offset + 8),
            reader.GetString(offset + 9),
            reader.IsDBNull(offset + 10) ? null : reader.GetString(offset + 10));
}

public sealed class IdempotencyConflictException : Exception;
