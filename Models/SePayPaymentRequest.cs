using System.ComponentModel.DataAnnotations;

namespace BAOCODETOOL.Models;

public sealed class SePayPaymentRequest
{
    [Key]
    public int Id { get; set; }

    public long ChatId { get; set; }

    [MaxLength(64)]
    public string TransferCode { get; set; } = string.Empty;

    public decimal ExpectedAmount { get; set; }

    [MaxLength(30)]
    public string Status { get; set; } = "Pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public long? SePayTransactionId { get; set; }
}
