using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StackExchange.Redis;
using Valuator.Services;
namespace Valuator.Pages;

public class IndexModel(EvaluationStore store, ILogger<IndexModel> logger) : PageModel
{
    [BindProperty]
    public string? Text { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        // Preserve whitespace: model binding would convert whitespace-only input to null.
        Text = Request.Form["Text"].ToString();
        if (string.IsNullOrEmpty(Text))
        {
            ModelState.AddModelError(nameof(Text), "Введите текст для оценки.");
            return Page();
        }
        try
        {
            string id = await store.SaveAsync(Text);
            return RedirectToPage("/Summary", new { id });
        }
        catch (RedisException exception)
        {
            logger.LogError(exception, "Не удалось сохранить оценку в Redis.");
            ModelState.AddModelError(string.Empty, "Хранилище Redis недоступно. Повторите попытку позже.");
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Page();
        }
    }
}
