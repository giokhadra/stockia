using System.Security.Claims;

namespace Stockia.Web.Services;

public static class ClaimsExtensions
{
    public const string CompanyIdClaim = "CompanyId";

    /// <summary>The signed-in user's id (SR-33).</summary>
    public static int? GetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>The signed-in user's chosen company (SR-33). Every query on business data must filter by it.</summary>
    public static int? GetCompanyId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(CompanyIdClaim), out var id) ? id : null;
}
