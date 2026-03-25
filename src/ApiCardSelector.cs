using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.TestSupport;

namespace SneckoEye;

/// <summary>
/// Represents a pending card selection that the API client must resolve.
/// </summary>
public class PendingCardSelection
{
    public IReadOnlyList<CardModel> Options { get; }
    public int MinSelect { get; }
    public int MaxSelect { get; }
    public bool Cancelable { get; }
    public TaskCompletionSource<IEnumerable<CardModel>> Completion { get; }

    public PendingCardSelection(IEnumerable<CardModel> options, int minSelect, int maxSelect, bool cancelable)
    {
        Options = options.ToList();
        MinSelect = minSelect;
        MaxSelect = maxSelect;
        Cancelable = cancelable;
        Completion = new TaskCompletionSource<IEnumerable<CardModel>>();
    }

    public void Resolve(IEnumerable<CardModel> selected)
    {
        Completion.TrySetResult(selected);
    }

    public void Cancel()
    {
        Completion.TrySetResult(Enumerable.Empty<CardModel>());
    }
}

/// <summary>
/// Implements the game's ICardSelector test interface to intercept all
/// card selection UI and route it through the HTTP API instead.
/// </summary>
public class ApiCardSelector : ICardSelector
{
    private static ApiCardSelector? _instance;
    private static IDisposable? _selectorScope;

    public static PendingCardSelection? PendingSelection { get; private set; }
    public static bool HasPendingSelection => PendingSelection != null;

    /// <summary>
    /// Push this selector onto the game's selector stack.
    /// Call once at mod init. The selector stays active for the lifetime of the mod.
    /// </summary>
    public static void Activate()
    {
        if (_instance != null) return;
        _instance = new ApiCardSelector();
        _selectorScope = MegaCrit.Sts2.Core.Commands.CardSelectCmd.PushSelector(_instance);
        ModEntry.Log("ApiCardSelector activated");
    }

    public static void ResolveSelection(int[] indices)
    {
        var pending = PendingSelection;
        if (pending == null)
        {
            ModEntry.Log("No pending card selection to resolve");
            return;
        }

        var selected = indices
            .Where(i => i >= 0 && i < pending.Options.Count)
            .Select(i => pending.Options[i])
            .ToList();

        ModEntry.Log($"Resolving card selection with {selected.Count} cards");
        PendingSelection = null;
        pending.Resolve(selected);
    }

    public static void CancelSelection()
    {
        var pending = PendingSelection;
        if (pending == null) return;

        ModEntry.Log("Cancelling card selection");
        PendingSelection = null;
        pending.Cancel();
    }

    // --- ICardSelector implementation ---

    public async Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        ModEntry.Log($"Card selection requested: {options.Count()} options, select {minSelect}-{maxSelect}");

        var pending = new PendingCardSelection(options, minSelect, maxSelect, cancelable: minSelect == 0);
        PendingSelection = pending;

        // Wait for the API client to resolve this
        var result = await pending.Completion.Task;
        return result;
    }

    public CardModel? GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        // Card reward selection -- also route through the pending system
        // For now return null (skip) since card rewards use a different flow
        ModEntry.Log($"Card reward selection requested: {options.Count} options");
        return null;
    }
}
