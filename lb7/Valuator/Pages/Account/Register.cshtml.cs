using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StackExchange.Redis;
using Valuator.Services;

namespace Valuator.Pages.Account;

public class RegisterModel(UserStore users, ILogger<RegisterModel> logger) : PageModel
{
    [BindProperty, Required, RegularExpression("[a-zA-Z0-9_]{3,32}", ErrorMessage = "Логин: 3–32 латинские буквы, цифры или знак _.")]
    public string Login { get; set; } = "";
    [BindProperty, Required, StringLength(128, MinimumLength = 8), DataType(DataType.Password)]
    public string Password { get; set; } = "";
    [BindProperty, Required, Compare(nameof(Password), ErrorMessage = "Пароли не совпадают."), DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = "";

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try
        {
            if (await users.RegisterAsync(Login, Password) is null)
            {
                ModelState.AddModelError(nameof(Login), "Этот логин уже занят.");
                return Page();
            }
            return RedirectToPage("/Account/Login", new { registered = true });
        }
        catch (RedisException ex)
        {
            logger.LogError(ex, "Registration storage unavailable.");
            Response.StatusCode = 503;
            ModelState.AddModelError("", "Хранилище недоступно. Повторите попытку позже.");
            return Page();
        }
    }
}
