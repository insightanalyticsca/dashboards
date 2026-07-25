using Microsoft.AspNetCore.Mvc;

namespace corporate_dashboards.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
}
