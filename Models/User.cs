using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BAOCODETOOL.Models;

public sealed class User
{
    [Key]
    public int Id { get; set; }

    public long ChatId { get; set; }

    [MaxLength(255)]
    public string? Username { get; set; }

    public decimal Balance { get; set; }

    public decimal TotalSpent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    public ICollection<Job> Jobs { get; set; } = new List<Job>();
}
