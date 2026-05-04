using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DynamoCopilot.Server.Pages.Dashboard;

[Authorize(AuthenticationSchemes = "AdminCookie")]
public abstract class DashboardPageModel : PageModel { }
