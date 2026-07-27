using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BAOCODETOOL.Models;

public sealed class Job
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User? User { get; set; }

    [Required]
    [MaxLength(100)]
    public string Provider { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = "Running"; // Running, Success, Failed

    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    public DateTime? EndTime { get; set; }

    public string? Logs { get; set; }
}
