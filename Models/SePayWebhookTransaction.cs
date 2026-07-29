using System.ComponentModel.DataAnnotations;

namespace BAOCODETOOL.Models;

public sealed class SePayWebhookTransaction
{
    [Key]
    public int Id { get; set; }

    public long SePayTransactionId { get; set; }

    [MaxLength(50)]
    public string Gateway { get; set; } = string.Empty;

    [MaxLength(50)]
    public string AccountNumber { get; set; } = string.Empty;

    [MaxLength(10)]
    public string TransferType { get; set; } = string.Empty;

    public decimal TransferAmount { get; set; }

    [MaxLength(500)]
    public string Content { get; set; } = string.Empty;

    [MaxLength(100)]
    public string ReferenceCode { get; set; } = string.Empty;

    public long? ChatId { get; set; }

    [MaxLength(30)]
    public string Status { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? StatusMessage { get; set; }

    public DateTime TransactionDate { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
