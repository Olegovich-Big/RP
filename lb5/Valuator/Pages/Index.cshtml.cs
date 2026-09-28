using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StackExchange.Redis;
using Valuator.Services;
using Valuator.Shared;
namespace Valuator.Pages;

public class IndexModel(EvaluationStore store, RankPublisher publisher, EventPublisher events,
    ILogger<IndexModel> logger) : PageModel
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
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await events.PublishAsync(CalculationEvent.SimilarityCalculated, id, timeout.Token);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Similarity event {Id} remains in the outbox.", id);
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await publisher.PublishAsync(id, timeout.Token);
            }
            catch (Exception exception)
            {
                // The durable Redis outbox retries publication if the broker is unavailable.
                logger.LogWarning(exception, "Rank job {Id} is queued in the outbox.", id);
            }
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
