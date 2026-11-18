using Microsoft.AspNetCore.Mvc;

namespace Stockia.Web.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
}
