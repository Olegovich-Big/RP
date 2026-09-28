using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StackExchange.Redis;
using Valuator.Services;

namespace Valuator.Pages.Account;

public class LoginModel(UserStore users, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty, Required, StringLength(32)] public string Login { get; set; } = "";
    [BindProperty, Required, StringLength(128), DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public bool Registered { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        try
        {
            var account = await users.AuthenticateAsync(Login, Password);
            if (account is null)
            {
                ModelState.AddModelError("", "Неверный логин или пароль.");
                return Page();
            }
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, account.Id), new Claim(ClaimTypes.Name, account.Login)
            }, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
        }
        catch (RedisException ex)
        {
            logger.LogError(ex, "Login storage unavailable.");
            Response.StatusCode = 503;
            ModelState.AddModelError("", "Хранилище недоступно. Повторите попытку позже.");
            return Page();
        }
    }
}
