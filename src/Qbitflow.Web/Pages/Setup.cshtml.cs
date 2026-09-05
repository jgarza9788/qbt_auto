using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Qbitflow.Infrastructure.Auth;
using Qbitflow.Infrastructure.Persistence;

namespace Qbitflow.Web.Pages;

public class SetupModel(AppDbContext db, IUserService userService, ISetupStateAccessor setupState) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await setupState.IsSetupCompleteAsync(db, ct))
        {
            return RedirectToPage("/Index");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await setupState.IsSetupCompleteAsync(db, ct))
        {
            return RedirectToPage("/Index");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var user = await userService.CreateUserAsync(Input.Username, Input.Password, ct);

            var settings = await db.AppSettings.SingleAsync(s => s.Id == 1, ct);
            settings.SetupCompleted = true;
            await db.SaveChangesAsync(ct);
            setupState.MarkComplete();

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username)
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return RedirectToPage("/Index");
        }
        catch (ArgumentException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public class InputModel
    {
        [Required]
        [Display(Name = "Username")]
        public string Username { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Password")]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
        public string Password { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Confirm password")]
        [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
