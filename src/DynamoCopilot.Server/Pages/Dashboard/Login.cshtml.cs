using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DynamoCopilot.Server.Pages.Dashboard;

public class LoginModel : PageModel
{
    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Dashboard/Index");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromServices] IConfiguration config)
    {
        var adminKey = config["Admin:ApiKey"];

        if (string.IsNullOrWhiteSpace(adminKey) || Password != adminKey)
        {
            Error = "Invalid admin key.";
            return Page();
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, "admin") };
        var identity = new ClaimsIdentity(claims, "AdminCookie");
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync("AdminCookie", principal, new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        });

        return RedirectToPage("/Dashboard/Index");
    }
}
