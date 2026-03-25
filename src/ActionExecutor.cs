using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Saves;

namespace SneckoEye;

public static class ActionExecutor
{
    // Guard: track which rest site floor we've already used an option on
    private static int _restSiteUsedOnFloor = -1;

    public static string Execute(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return Error("Missing 'type' field");

            string type = typeProp.GetString() ?? "";
            ModEntry.Log($"Executing action: {type}");
            EventLog.Log("action", type);

            return type switch
            {
                "start_run" => StartRun(root),
                "continue_run" => ContinueRun(),
                "abandon_run" => AbandonRun(),
                "play_card" => PlayCard(root),
                "end_turn" => EndTurn(),
                "select_node" => SelectNode(root),
                "use_potion" => UsePotion(root),
                "select_cards" => SelectCards(root),
                "cancel_selection" => CancelSelection(),
                "choose_option" => ChooseOption(root),
                "choose_rest_option" => ChooseRestOption(root),
                "buy_card" => BuyShopItem(root, ShopItemType.Card),
                "buy_relic" => BuyShopItem(root, ShopItemType.Relic),
                "buy_potion" => BuyShopItem(root, ShopItemType.Potion),
                "take_reward" => TakeReward(root),
                "remove_card" => BuyShopRemoval(),
                "leave_shop" => LeaveShop(),
                "proceed" => Proceed(),
                "return_to_menu" => ReturnToMenu(),
                _ => Error($"Unknown action type: {type}"),
            };
        }
        catch (JsonException ex)
        {
            return Error("Invalid JSON: " + ex.Message);
        }
        catch (Exception ex)
        {
            ModEntry.Log("Action error: " + ex);
            return Error(ex.Message);
        }
    }

    // ---------------------------------------------------------------
    // Main menu / run lifecycle
    // ---------------------------------------------------------------

    private static string StartRun(JsonElement root)
    {
        if (RunManager.Instance?.IsInProgress == true)
            return Error("A run is already in progress");

        var nGame = NGame.Instance;
        if (nGame == null) return Error("NGame.Instance is null");

        // Parse character (default: Ironclad)
        string charName = "ironclad";
        if (root.TryGetProperty("character", out var charProp))
            charName = charProp.GetString()?.ToLowerInvariant() ?? "ironclad";

        CharacterModel? character = charName switch
        {
            "ironclad" => ModelDb.Character<Ironclad>(),
            "silent" => ModelDb.Character<Silent>(),
            "defect" => ModelDb.Character<Defect>(),
            "regent" => ModelDb.Character<Regent>(),
            "necrobinder" => ModelDb.Character<Necrobinder>(),
            _ => null,
        };

        if (character == null)
            return Error($"Unknown character: {charName}. Use: ironclad, silent, defect, regent, necrobinder");

        int ascension = 0;
        if (root.TryGetProperty("ascension", out var ascProp))
            ascension = ascProp.GetInt32();

        string seed = SeedHelper.GetRandomSeed();
        if (root.TryGetProperty("seed", out var seedProp))
            seed = seedProp.GetString() ?? seed;

        var acts = ActModel.GetDefaultList();
        var modifiers = System.Array.Empty<ModifierModel>();

        _ = nGame.StartNewSingleplayerRun(character, shouldSave: true, acts, modifiers, seed, ascension);
        ModEntry.Log($"Starting new run: {charName} A{ascension} seed={seed}");
        return Success();
    }

    private static string ContinueRun()
    {
        if (RunManager.Instance?.IsInProgress == true)
            return Error("A run is already in progress");

        // Simulate the continue button press from the main menu
        // We need to find the NMainMenu instance and call its continue logic
        // For now, the simplest approach: find and click the continue flow
        var nGame = NGame.Instance;
        if (nGame == null) return Error("NGame.Instance is null");

        // Access saved run data via SaveManager
        var save = SaveManager.Instance?.LoadRunSave();
        if (save == null || !save.Success || save.SaveData == null)
            return Error("No saved run found");

        var saveData = save.SaveData;
        var runState = RunState.FromSerializable(saveData);
        RunManager.Instance.SetUpSavedSinglePlayer(runState, saveData);

        _ = nGame.LoadRun(runState, saveData.PreFinishedRoom);
        ModEntry.Log("Continuing saved run");
        return Success();
    }

    private static string AbandonRun()
    {
        if (RunManager.Instance?.IsInProgress == true)
            return Error("Cannot abandon while a run is in progress. Use return_to_menu first.");

        SaveManager.Instance?.DeleteCurrentRun();
        ModEntry.Log("Abandoned saved run");
        return Success();
    }

    // ---------------------------------------------------------------
    // Combat actions
    // ---------------------------------------------------------------

    private static string PlayCard(JsonElement root)
    {
        if (!CombatManager.Instance.IsInProgress || !CombatManager.Instance.IsPlayPhase)
            return Error("Not in combat or not player's turn");

        int cardIndex = GetInt(root, "card_index");
        var player = GetPlayer();
        if (player?.PlayerCombatState == null) return Error("No player combat state");

        var hand = player.PlayerCombatState.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Card index {cardIndex} out of range (hand size: {hand.Count})");

        var card = hand[cardIndex];

        if (!card.CanPlay(out var reason, out _))
            return Error($"Cannot play card: {reason}");

        // Resolve target
        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy || card.TargetType == TargetType.AnyAlly)
        {
            int targetIndex = GetInt(root, "target_index", -1);
            if (targetIndex < 0)
                return Error("Card requires a target (target_index)");

            var combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState == null) return Error("No combat state");

            var targets = card.TargetType == TargetType.AnyEnemy
                ? combatState.Enemies.Where(e => e.IsAlive).ToList()
                : combatState.Allies.Where(a => a.IsAlive).ToList();

            if (targetIndex < 0 || targetIndex >= targets.Count)
                return Error($"Target index {targetIndex} out of range ({targets.Count} targets)");

            target = targets[targetIndex];
        }

        var action = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

        ModEntry.Log($"Played card: {card.Id} on target: {target?.Name ?? "none"}");
        return Success();
    }

    private static string EndTurn()
    {
        if (!CombatManager.Instance.IsInProgress || !CombatManager.Instance.IsPlayPhase)
            return Error("Not in combat or not player's turn");

        var player = GetPlayer();
        if (player == null) return Error("No player found");

        PlayerCmd.EndTurn(player, canBackOut: true);
        ModEntry.Log("Ended turn");
        return Success();
    }

    private static string UsePotion(JsonElement root)
    {
        int slot = GetInt(root, "potion_slot");
        var player = GetPlayer();
        if (player == null) return Error("No player found");

        var potionsList = player.Potions.ToList();
        if (slot < 0 || slot >= potionsList.Count)
            return Error($"Potion slot {slot} out of range");
        var potion = potionsList[slot];
        if (potion == null) return Error($"No potion at slot {slot}");

        bool inCombat = CombatManager.Instance?.IsInProgress ?? false;

        Creature? target = null;
        int targetIndex = GetInt(root, "target_index", -1);
        if (targetIndex >= 0 && inCombat)
        {
            var combatState = CombatManager.Instance!.DebugOnlyGetState();
            var enemies = combatState?.Enemies.Where(e => e.IsAlive).ToList();
            if (enemies != null && targetIndex < enemies.Count)
                target = enemies[targetIndex];
        }

        var action = new UsePotionAction(potion, target, inCombat);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

        ModEntry.Log($"Used potion at slot {slot}");
        return Success();
    }

    // ---------------------------------------------------------------
    // Card selection (mid-action choices)
    // ---------------------------------------------------------------

    private static string SelectCards(JsonElement root)
    {
        if (!root.TryGetProperty("indices", out var indicesProp))
            return Error("Missing 'indices' array");

        var overlay = NOverlayStack.Instance?.Peek();

        // Handle NCardRewardSelectionScreen (combat card rewards)
        if (overlay is NCardRewardSelectionScreen cardRewardScreen)
            return SelectCardsFromCardRewardScreen(cardRewardScreen, indicesProp);

        // Handle NChooseACardSelectionScreen (mid-action card choices)
        if (overlay is NChooseACardSelectionScreen chooseScreen)
            return SelectCardsFromChooseScreen(chooseScreen, indicesProp);

        // Handle NCardGridSelectionScreen (Transform, Upgrade, etc.)
        if (overlay is NCardGridSelectionScreen gridScreen)
            return SelectCardsFromGridScreen(gridScreen, indicesProp);

        return Error("No card selection screen visible");
    }

    private static string SelectCardsFromGridScreen(NCardGridSelectionScreen gridScreen, JsonElement indicesProp)
    {
        var cardsField = typeof(NCardGridSelectionScreen).GetField("_cards",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cards = cardsField?.GetValue(gridScreen) as IReadOnlyList<CardModel>;
        if (cards == null) return Error("Could not read cards from selection screen");

        var onClickMethod = gridScreen.GetType().GetMethod("OnCardClicked",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (onClickMethod == null) return Error("Could not find OnCardClicked method");

        foreach (var item in indicesProp.EnumerateArray())
        {
            int idx = item.GetInt32();
            if (idx < 0 || idx >= cards.Count) continue;
            onClickMethod.Invoke(gridScreen, new object[] { cards[idx] });
            ModEntry.Log($"Selected card: {cards[idx].Id}");
        }

        // NDeckUpgradeSelectScreen shows a preview + confirm/cancel after OnCardClicked.
        // We need to call ConfirmSelection to actually complete the selection.
        var confirmMethod = gridScreen.GetType().GetMethod("ConfirmSelection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (confirmMethod != null)
        {
            confirmMethod.Invoke(gridScreen, new object?[] { null });
            ModEntry.Log("Confirmed card selection");
        }

        return Success();
    }

    private static string SelectCardsFromCardRewardScreen(NCardRewardSelectionScreen screen, JsonElement indicesProp)
    {
        var cardRowField = typeof(NCardRewardSelectionScreen).GetField("_cardRow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cardRow = cardRowField?.GetValue(screen) as Godot.Control;
        if (cardRow == null) return Error("Could not find card row on reward screen");

        var holders = cardRow.GetChildren()
            .OfType<NGridCardHolder>()
            .ToList();
        if (holders.Count == 0) return Error("No card holders found on reward screen");

        var selectMethod = typeof(NCardRewardSelectionScreen).GetMethod("SelectCard",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (selectMethod == null) return Error("Could not find SelectCard method");

        foreach (var item in indicesProp.EnumerateArray())
        {
            int idx = item.GetInt32();
            if (idx < 0 || idx >= holders.Count) continue;
            selectMethod.Invoke(screen, new object[] { holders[idx] });
            ModEntry.Log($"Selected card reward at index {idx}");
        }

        return Success();
    }

    private static string SelectCardsFromChooseScreen(NChooseACardSelectionScreen chooseScreen, JsonElement indicesProp)
    {
        // _cardRow contains NGridCardHolder children; SelectHolder(NCardHolder) picks one
        var cardRowField = typeof(NChooseACardSelectionScreen).GetField("_cardRow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cardRow = cardRowField?.GetValue(chooseScreen) as Godot.Control;
        if (cardRow == null) return Error("Could not find card row on choose screen");

        var holders = cardRow.GetChildren()
            .OfType<NGridCardHolder>()
            .ToList();
        if (holders.Count == 0) return Error("No card holders found on choose screen");

        var selectMethod = typeof(NChooseACardSelectionScreen).GetMethod("SelectHolder",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (selectMethod == null) return Error("Could not find SelectHolder method");

        foreach (var item in indicesProp.EnumerateArray())
        {
            int idx = item.GetInt32();
            if (idx < 0 || idx >= holders.Count) continue;
            selectMethod.Invoke(chooseScreen, new object[] { holders[idx] });
            ModEntry.Log($"Selected card holder at index {idx}");
        }

        return Success();
    }

    private static string CancelSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();

        if (overlay is NCardRewardSelectionScreen rewardScreen)
        {
            // Set empty result to skip card reward
            NOverlayStack.Instance?.Remove(rewardScreen);
            ModEntry.Log("Skipped card reward selection");
            return Success();
        }

        if (overlay is NChooseACardSelectionScreen chooseScreen)
        {
            // Skip button fires OnSkipButtonReleased which sets empty result
            var skipMethod = typeof(NChooseACardSelectionScreen).GetMethod("OnSkipButtonReleased",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (skipMethod != null)
            {
                skipMethod.Invoke(chooseScreen, new object?[] { null });
                ModEntry.Log("Skipped card selection (choose screen)");
                return Success();
            }
        }

        if (overlay is NCardGridSelectionScreen gridScreen)
        {
            // CloseSelection sets empty result and removes overlay
            var closeMethod = gridScreen.GetType().GetMethod("CloseSelection",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (closeMethod != null)
            {
                closeMethod.Invoke(gridScreen, new object?[] { null });
                ModEntry.Log("Cancelled card selection (grid screen)");
                return Success();
            }
        }

        return Error("No card selection screen to cancel");
    }

    // ---------------------------------------------------------------
    // Map navigation
    // ---------------------------------------------------------------

    private static string SelectNode(JsonElement root)
    {
        int col = GetInt(root, "col");
        int row = GetInt(root, "row");

        var runState = GetRunState();
        var player = GetPlayer();
        if (runState == null || player == null) return Error("No active run");

        // Guard: don't allow node selection while rewards screen is active
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NRewardsScreen rewardsScreen && !rewardsScreen.IsComplete)
            return Error("Cannot select node while rewards are still available");

        var vote = new MapVote
        {
            coord = new MapCoord(col, row),
            mapGenerationCount = RunManager.Instance.MapSelectionSynchronizer.MapGenerationCount,
        };

        var action = new VoteForMapCoordAction(player, runState.CurrentLocation, vote);
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

        ModEntry.Log($"Selected map node ({col}, {row})");
        return Success();
    }

    // ---------------------------------------------------------------
    // Event choices
    // ---------------------------------------------------------------

    private static string ChooseOption(JsonElement root)
    {
        int index = GetInt(root, "option_index");
        var runState = GetRunState();
        if (runState?.CurrentRoom is not EventRoom eventRoom)
            return Error("Not in an event room");

        var options = eventRoom.LocalMutableEvent.CurrentOptions;
        if (index < 0 || index >= options.Count)
            return Error($"Option index {index} out of range ({options.Count} options)");

        var option = options[index];
        if (option.IsLocked || option.WasChosen)
            return Error("Option is locked or already chosen");

        // Fire and forget -- the event system handles the async flow
        _ = option.Chosen();
        ModEntry.Log($"Chose event option {index}");
        return Success();
    }

    // ---------------------------------------------------------------
    // Rest site
    // ---------------------------------------------------------------

    private static string ChooseRestOption(JsonElement root)
    {
        int index = GetInt(root, "option_index");
        var runState = GetRunState();
        if (runState?.CurrentRoom is not RestSiteRoom restRoom)
            return Error("Not in a rest site room");

        var options = restRoom.Options;
        if (index < 0 || index >= options.Count)
            return Error($"Option index {index} out of range ({options.Count} options)");

        var option = options[index];
        if (!option.IsEnabled)
            return Error("Rest option is disabled");

        // Guard: only allow one rest option per rest site visit
        int currentFloor = runState.TotalFloor;
        if (_restSiteUsedOnFloor == currentFloor)
            return Error("Already chose a rest option on this floor");
        _restSiteUsedOnFloor = currentFloor;

        // Fire and forget -- smith will trigger card selection via ApiCardSelector
        _ = option.OnSelect();
        ModEntry.Log($"Chose rest option {index}: {option.OptionId}");
        return Success();
    }

    // ---------------------------------------------------------------
    // Shop
    // ---------------------------------------------------------------

    private enum ShopItemType { Card, Relic, Potion }

    private static string BuyShopItem(JsonElement root, ShopItemType itemType)
    {
        var runState = GetRunState();
        if (runState?.CurrentRoom is not MerchantRoom shopRoom)
            return Error("Not in a shop");

        var inv = shopRoom.Inventory;
        MerchantEntry? entry = null;

        switch (itemType)
        {
            case ShopItemType.Card:
            {
                int idx = GetInt(root, "card_index");
                var allCards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
                    .Where(e => e.IsStocked).ToList();
                if (idx < 0 || idx >= allCards.Count)
                    return Error($"Card index {idx} out of range");
                entry = allCards[idx];
                break;
            }
            case ShopItemType.Relic:
            {
                int idx = GetInt(root, "relic_index");
                var available = inv.RelicEntries.Where(e => e.IsStocked).ToList();
                if (idx < 0 || idx >= available.Count)
                    return Error($"Relic index {idx} out of range");
                entry = available[idx];
                break;
            }
            case ShopItemType.Potion:
            {
                int idx = GetInt(root, "potion_index");
                var available = inv.PotionEntries.Where(e => e.IsStocked).ToList();
                if (idx < 0 || idx >= available.Count)
                    return Error($"Potion index {idx} out of range");
                entry = available[idx];
                break;
            }
        }

        if (entry == null) return Error("Could not find shop entry");
        if (!entry.EnoughGold) return Error("Not enough gold");

        _ = entry.OnTryPurchaseWrapper(inv);
        ModEntry.Log($"Purchased {itemType} from shop");
        return Success();
    }

    private static string BuyShopRemoval()
    {
        var runState = GetRunState();
        if (runState?.CurrentRoom is not MerchantRoom shopRoom)
            return Error("Not in a shop");

        var removal = shopRoom.Inventory.CardRemovalEntry;
        if (removal == null || !removal.IsStocked)
            return Error("Card removal not available");
        if (!removal.EnoughGold)
            return Error("Not enough gold for card removal");

        // This will trigger card selection via ApiCardSelector
        _ = removal.OnTryPurchaseWrapper(shopRoom.Inventory);
        ModEntry.Log("Initiated card removal from shop");
        return Success();
    }

    private static string LeaveShop()
    {
        var runState = GetRunState();
        if (runState?.CurrentRoom is not MerchantRoom)
            return Error("Not in a shop");

        NMapScreen.Instance?.SetTravelEnabled(enabled: true);
        NMapScreen.Instance?.Open();
        ModEntry.Log("Left shop -> map");
        return Success();
    }

    // ---------------------------------------------------------------
    // Rewards and lifecycle
    // ---------------------------------------------------------------

    private static string TakeReward(JsonElement root)
    {
        int index = GetInt(root, "reward_index");
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NRewardsScreen rewardsScreen)
            return Error("Not on rewards screen");

        try
        {
            var container = rewardsScreen.GetNode<Godot.Control>("%RewardsContainer");
            if (container == null) return Error("Could not find rewards container");

            var buttons = container.GetChildren()
                .OfType<NRewardButton>()
                .Where(b => b.Reward != null)
                .ToList();

            if (index < 0 || index >= buttons.Count)
                return Error($"Reward index {index} out of range ({buttons.Count} rewards)");

            var button = buttons[index];

            // Detect -> Input: simulate clicking the reward button via OnRelease().
            // This triggers GetReward() which awaits OnSelectWrapper(), then emits
            // RewardClaimed signal so NRewardsScreen properly removes the button.
            var onRelease = typeof(NRewardButton).GetMethod("OnRelease",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (onRelease == null) return Error("Could not find OnRelease method");

            onRelease.Invoke(button, Array.Empty<object>());
            ModEntry.Log($"Clicked reward button {index}: {Loc(button.Reward!.Description)}");
            return Success();
        }
        catch (Exception ex)
        {
            return Error("Failed to take reward: " + ex.Message);
        }
    }

    private static string? Loc(MegaCrit.Sts2.Core.Localization.LocString? loc)
    {
        if (loc == null) return null;
        try { return loc.GetFormattedText(); }
        catch { return null; }
    }

    private static string Proceed()
    {
        // Rewards screen: simulate clicking the proceed/skip button
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NRewardsScreen rewardsScreen)
        {
            var onProceed = typeof(NRewardsScreen).GetMethod("OnProceedButtonPressed",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (onProceed != null)
            {
                onProceed.Invoke(rewardsScreen, new object?[] { null });
                ModEntry.Log("Clicked proceed button on rewards screen");
                return Success();
            }
            return Error("Could not find OnProceedButtonPressed method");
        }

        // Finished event: open the map (same as NEventRoom.Proceed())
        var runState = GetRunState();
        if (runState?.CurrentRoom is EventRoom eventRoom)
        {
            var evt = eventRoom.LocalMutableEvent;
            if (evt.IsFinished)
            {
                NMapScreen.Instance?.SetTravelEnabled(enabled: true);
                NMapScreen.Instance?.Open();
                ModEntry.Log("Proceeded past finished event -> map");
                return Success();
            }
        }

        return Error("Nothing to proceed past");
    }

    private static string ReturnToMenu()
    {
        var nGame = NGame.Instance;
        if (nGame == null) return Error("NGame.Instance is null");

        _ = nGame.ReturnToMainMenuAfterRun();
        ModEntry.Log("Returning to main menu");
        return Success();
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static RunState? GetRunState() => RunManager.Instance?.DebugOnlyGetState();

    private static Player? GetPlayer() => GetRunState()?.Players.FirstOrDefault();

    private static int GetInt(JsonElement root, string prop, int defaultValue = -1)
    {
        return root.TryGetProperty(prop, out var val) ? val.GetInt32() : defaultValue;
    }

    private static string Success() => "{\"success\": true}";

    private static string Error(string message)
    {
        return $"{{\"success\": false, \"error\": \"{EscapeJson(message)}\"}}";
    }

    private static string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}
