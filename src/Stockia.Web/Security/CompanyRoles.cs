namespace Stockia.Web.Security;

/// <summary>SR-20: the four role names stored in the Role table.</summary>
public static class CompanyRoles
{
    public const string Administrator = "Administrator";
    public const string ChiefAccountant = "Chief accountant";
    public const string Accountant = "Accountant";
    public const string Viewer = "Viewer";

    /// <summary>Roles that may view a company's accounting pages.</summary>
    public static readonly string[] Readers =
        { Administrator, ChiefAccountant, Accountant, Viewer };

    /// <summary>Roles that may create, edit or deactivate accounting data.</summary>
    public static readonly string[] AccountingWriters =
        { Administrator, ChiefAccountant, Accountant };
}
