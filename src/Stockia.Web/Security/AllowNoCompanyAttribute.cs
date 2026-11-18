namespace Stockia.Web.Security;

/// <summary>SR-33: the page still requires a signed-in user but may run before a company is chosen
/// (the company chooser and sign-out). Every other page is checked against the chosen company.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowNoCompanyAttribute : Attribute
{
}
