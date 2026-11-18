namespace Stockia.Web.Domain;

/// <summary>SR-20: a user works in a company with one role.</summary>
public class Assignment
{
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int CompanyId { get; set; }
    public Company? Company { get; set; }
    public int RoleId { get; set; }
    public Role? Role { get; set; }
    public DateOnly Since { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);
}