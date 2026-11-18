using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Stockia.Web.Services;

namespace Stockia.Web.Security;

/// <summary>SR-33 / d5: runs on every MVC page and API. A signed-in user with no chosen company is
/// sent to the company chooser; a chosen company the user is not assigned to, or a role that is not
/// allowed, gets 403. Public pages opt out with [AllowAnonymous]; the chooser and sign-out with
/// [AllowNoCompany].</summary>
public sealed class CompanyAccessFilter : IAsyncAuthorizationFilter
{
    private readonly CompanyAccessService _access;
    public CompanyAccessFilter(CompanyAccessService access) => _access = access;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var metadata = context.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any()) return;
        if (metadata.OfType<AllowNoCompanyAttribute>().Any()) return;

        if (context.HttpContext.User.GetUserId() is not int userId)
        {
            context.Result = new ChallengeResult();
            return;
        }

        var requiredRoles = metadata.OfType<RequireCompanyRoleAttribute>()
            .SelectMany(a => a.Roles)
            .ToArray();

        var result = await _access.CheckAsync(
            userId, context.HttpContext.User.GetCompanyId(), requiredRoles);

        context.Result = result switch
        {
            CompanyAccessResult.Allowed => null,
            CompanyAccessResult.NoCompanyChosen =>
                new RedirectToActionResult("ChooseCompany", "Account", null),
            _ => new StatusCodeResult(StatusCodes.Status403Forbidden)
        };
    }
}
