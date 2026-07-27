using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BAOCODETOOL.Models;

public sealed class Transaction
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    public decimal Amount { get; set; }

    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(100)]
    public string? ReferenceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
