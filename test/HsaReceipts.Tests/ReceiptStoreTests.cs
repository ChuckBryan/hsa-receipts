namespace HsaReceipts.Tests;

public sealed class ReceiptStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "hsa-receipts-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RepeatedSubmissionReturnsOriginalReceipt()
    {
        var store = new ReceiptStore(root);
        var firstStaged = Stage(store, "first");
        var receipt = NewReceipt();

        var created = await store.CreateAsync(
            receipt,
            "user",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "digest",
            firstStaged,
            CancellationToken.None);
        var repeated = await store.CreateAsync(
            receipt with { Id = Guid.NewGuid().ToString("N") },
            "user",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "digest",
            Stage(store, "second"),
            CancellationToken.None);

        Assert.True(created.Created);
        Assert.False(repeated.Created);
        Assert.Equal(receipt.Id, repeated.Receipt.Id);
        Assert.Single(store.All());
        Assert.Equal("first", await File.ReadAllTextAsync(store.FilePath(receipt.Id)));
    }

    [Fact]
    public async Task ChangedPayloadWithSameKeyConflicts()
    {
        var store = new ReceiptStore(root);
        var receipt = NewReceipt();
        await store.CreateAsync(
            receipt,
            "user",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "first-digest",
            Stage(store, "first"),
            CancellationToken.None);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            store.CreateAsync(
                receipt with { Id = Guid.NewGuid().ToString("N") },
                "user",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "changed-digest",
                Stage(store, "second"),
                CancellationToken.None));

        Assert.Single(store.All());
    }

    [Fact]
    public async Task ExistingDatabaseSupportsEditsAndReimbursement()
    {
        var store = new ReceiptStore(root);
        var receipt = NewReceipt();
        await store.CreateAsync(
            receipt,
            "user",
            "cccccccccccccccccccccccccccccccc",
            "digest",
            Stage(store, "file"),
            CancellationToken.None);

        Assert.True(store.Edit(receipt.Id, "Dentist", "Pat", "2026-10-01", 4500, "Updated"));
        Assert.True(store.Reimburse(receipt.Id, "2026-10-27"));

        var updated = store.Find(receipt.Id);
        Assert.NotNull(updated);
        Assert.Equal("Dentist", updated.Provider);
        Assert.Equal(4500, updated.Cents);
        Assert.Equal("2026-10-27", updated.ReimbursedDate);
    }

    private string Stage(ReceiptStore store, string content)
    {
        var path = store.NewStagingPath();
        File.WriteAllText(path, content);
        return path;
    }

    private static Receipt NewReceipt() =>
        new(
            Guid.NewGuid().ToString("N"),
            "Clinic",
            "Pat",
            "2026-10-27",
            1234,
            "",
            "receipt.pdf",
            "application/pdf",
            "developer",
            DateTimeOffset.UtcNow.ToString("O"),
            null);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
