namespace Stockia.Web.Security;

/// <summary>SR-33: names the roles that may call this page or API in the chosen company.
/// Enforced by <see cref="CompanyAccessFilter"/>; an action without it allows any assigned role.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireCompanyRoleAttribute : Attribute
{
    public RequireCompanyRoleAttribute(params string[] roles) => Roles = roles;

    public IReadOnlyList<string> Roles { get; }
}
