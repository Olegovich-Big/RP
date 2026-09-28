using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using StackExchange.Redis;
using Valuator.Shared;
namespace Valuator.Pages;

public class SummaryModel(EvaluationStore store, ILogger<SummaryModel> logger) : PageModel
{
    public double? Rank { get; private set; }
    public string? Worker { get; private set; }
    public double Similarity { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        if (!Guid.TryParse(id, out Guid parsedId)) return BadRequest();
        try
        {
            Evaluation? result = await store.GetAsync(parsedId.ToString());
            if (result is null) return NotFound();
            Rank = result.Rank;
            Worker = result.Worker;
            Similarity = result.Similarity;
            return Page();
        }
        catch (RedisException exception)
        {
            logger.LogError(exception, "Не удалось прочитать оценку из Redis.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
