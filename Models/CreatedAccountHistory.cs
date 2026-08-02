using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BAOCODETOOL.Models;

public sealed class CreatedAccountHistory
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    public int? JobId { get; set; }

    [ForeignKey(nameof(JobId))]
    public Job? Job { get; set; }

    [Required]
    [MaxLength(255)]
    public string Sites { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string Password { get; set; } = string.Empty;

    [MaxLength(255)]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string PhoneNumber { get; set; } = string.Empty;

    [MaxLength(255)]
    public string BankName { get; set; } = string.Empty;

    [MaxLength(50)]
    public string BankCode { get; set; } = string.Empty;

    [MaxLength(100)]
    public string AccountNumber { get; set; } = string.Empty;

    [MaxLength(255)]
    public string Level2Password { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
