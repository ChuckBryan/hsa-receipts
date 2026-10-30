using HsaReceipts.Contracts;

namespace HsaReceipts.Tests;

public sealed class ReceiptRulesTests
{
    [Fact]
    public void NormalizesValidFields()
    {
        var fields = new ReceiptFields
        {
            Provider = " Clinic ",
            Patient = " Pat ",
            ServiceDate = "2026-10-27",
            Amount = 12.34m,
            Notes = " note "
        };

        var valid = ReceiptRules.TryNormalize(fields, out var normalized, out var errors);

        Assert.True(valid);
        Assert.Empty(errors);
        Assert.Equal("Clinic", normalized.Provider);
        Assert.Equal("Pat", normalized.Patient);
        Assert.Equal(1234, normalized.Cents);
        Assert.Equal("note", normalized.Notes);
    }

    [Theory]
    [InlineData("2026/10/27", 12.34)]
    [InlineData("2026-10-27", 12.345)]
    [InlineData("2026-10-27", 0)]
    public void RejectsInvalidFields(string date, decimal amount)
    {
        var fields = new ReceiptFields
        {
            Provider = "Clinic",
            Patient = "Pat",
            ServiceDate = date,
            Amount = amount
        };

        Assert.False(ReceiptRules.TryNormalize(fields, out _, out var errors));
        Assert.NotEmpty(errors);
    }
}
