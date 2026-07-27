using System;
using System.ComponentModel.DataAnnotations;

namespace BAOCODETOOL.Models;

public sealed class ProxyPackage
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Category { get; set; } = "VNPT"; // VNPT, Viettel, FPT, ALL...

    public int ProxyType { get; set; } = 0; // 0: HTTP/HTTPS, 1: SOCKS5

    public int PeriodDays { get; set; } = 1; // 1, 3, 7, 30 days

    public decimal OriginalPrice { get; set; } = 800; // Giá gốc Proxy.vn (Xu)

    public decimal SellingPrice { get; set; } = 2000; // Giá bán ra cho Bot (Xu)

    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
