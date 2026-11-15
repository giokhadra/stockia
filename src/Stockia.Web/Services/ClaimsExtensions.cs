using System.Security.Claims;

namespace Stockia.Web.Services;

public static class ClaimsExtensions
{
    public const string CompanyIdClaim = "CompanyId";

    /// <summary>The signed-in user's company (SR-33). Every query on business data must filter by it.</summary>
    public static int? GetCompanyId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(CompanyIdClaim), out var id) ? id : null;
}
