using Microsoft.AspNetCore.SignalR;
using Valuator.Shared;

namespace Valuator.Hubs;

public sealed record EvaluationUpdate(string TextId, double? Rank, double Similarity, string? Worker);

public sealed class EvaluationHub(EvaluationStore store) : Hub
{
    public static string Group(string id) => "evaluation:" + id;

    public async Task<EvaluationUpdate> Subscribe(string textId)
    {
        if (!Guid.TryParseExact(textId, "D", out var parsed)) throw new HubException("Некорректный ID текста.");
        string id = parsed.ToString();
        // Join first, then read the snapshot: an event racing with subscription cannot be missed.
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(id), Context.ConnectionAborted);
        var result = await store.GetAsync(id);
        if (result is null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(id), Context.ConnectionAborted);
            throw new HubException("Текст не найден.");
        }
        return new EvaluationUpdate(id, result.Rank, result.Similarity, result.Worker);
    }
}
