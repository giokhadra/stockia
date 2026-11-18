using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockia.Web.Services;

namespace Stockia.Web.Controllers;

/// <summary>SR-6: the audit trail of the company. Read-only, so every role assigned to the company may open it (SR-33);
/// the auditor (ST-8) reads it here.</summary>
[Authorize]
public class AuditController : Controller
{
    private readonly AuditService _audit;
    public AuditController(AuditService audit) => _audit = audit;

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (User.GetCompanyId() is not int companyId) return Forbid();
        return View(await _audit.ListAsync(companyId));
    }
}