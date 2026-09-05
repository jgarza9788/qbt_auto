using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Qbitflow.Infrastructure.Auth;

namespace Qbitflow.Web.Pages.Account;

public class ChangePasswordModel(IUserService userService) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool Success { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        try
        {
            await userService.ChangePasswordAsync(userId, Input.CurrentPassword, Input.NewPassword, ct);
            Success = true;
            Input = new InputModel();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }

        return Page();
    }

    public class InputModel
    {
        [Required]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required]
        [Display(Name = "New password")]
        [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Confirm new password")]
        [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
        public string ConfirmNewPassword { get; set; } = string.Empty;
    }
}
