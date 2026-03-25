using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace SneckoEye;

public static class GameStateReader
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ReadState()
    {
        try
        {
            var state = BuildState();
            return JsonSerializer.Serialize(state, s_jsonOptions);
        }
        catch (Exception ex)
        {
            ModEntry.Log("Error reading state: " + ex.Message);
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["phase"] = "error",
                ["error"] = ex.Message,
                ["available_actions"] = Array.Empty<object>(),
            }, s_jsonOptions);
        }
    }

    private static RunState? GetRunState() => RunManager.Instance?.DebugOnlyGetState();

    private static Dictionary<string, object?> BuildState()
    {
        var state = new Dictionary<string, object?>();
        var actions = new List<Dictionary<string, object>>();

        // Card selection overlay takes highest priority
        var topOverlay = NOverlayStack.Instance?.Peek();
        if (topOverlay is NCardRewardSelectionScreen cardRewardScreen)
        {
            state["phase"] = "awaiting_card_selection";
            state["card_selection"] = ReadCardRewardSelection(cardRewardScreen);
            actions.AddRange(BuildCardRewardSelectionActions(cardRewardScreen));
            AddRunAndPlayerInfo(state);
            state["available_actions"] = actions;
            return state;
        }
        if (topOverlay is NChooseACardSelectionScreen chooseScreen)
        {
            state["phase"] = "awaiting_card_selection";
            state["card_selection"] = ReadChooseCardSelection(chooseScreen);
            actions.AddRange(BuildChooseCardSelectionActions(chooseScreen));
            AddRunAndPlayerInfo(state);
            state["available_actions"] = actions;
            return state;
        }
        if (topOverlay is NCardGridSelectionScreen gridScreen)
        {
            state["phase"] = "awaiting_card_selection";
            state["card_selection"] = ReadGridCardSelection(gridScreen);
            actions.AddRange(BuildGridCardSelectionActions(gridScreen));
            AddRunAndPlayerInfo(state);
            state["available_actions"] = actions;
            return state;
        }

        if (RunManager.Instance == null || !RunManager.Instance.IsInProgress)
        {
            state["phase"] = "main_menu";

            bool hasSavedRun = false;
            try
            {
                var save = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.LoadRunSave();
                hasSavedRun = save != null && save.Success && save.SaveData != null;
            }
            catch { }

            state["main_menu"] = new Dictionary<string, object>
            {
                ["has_saved_run"] = hasSavedRun,
            };

            actions.Add(new Dictionary<string, object>
            {
                ["type"] = "start_run",
                ["characters"] = new[] { "ironclad", "silent", "defect", "regent", "necrobinder" },
            });

            if (hasSavedRun)
            {
                actions.Add(Act("continue_run"));
                actions.Add(Act("abandon_run"));
            }

            state["available_actions"] = actions;
            return state;
        }

        var runState = GetRunState();
        if (runState == null)
        {
            state["phase"] = "unknown";
            state["available_actions"] = actions;
            return state;
        }

        AddRunAndPlayerInfo(state);
        AddDeckRelicsPotions(state, runState);

        // Determine phase -- check overlays first, then map screen, then room type
        var room = runState.CurrentRoom;
        var overlay = NOverlayStack.Instance?.Peek();
        bool mapIsOpen = NMapScreen.Instance?.IsOpen == true;

        if (overlay is NRewardsScreen rewardsScreen && !(rewardsScreen.IsComplete && mapIsOpen))
        {
            // Rewards overlay takes priority over map screen
            // (unless rewards are complete AND map has opened on top)
            BuildRewardsPhase(state, actions, rewardsScreen);
        }
        else if (mapIsOpen && !(CombatManager.Instance?.IsInProgress == true))
        {
            // Map screen is open -- this is map selection regardless of underlying room
            state["phase"] = "map_selection";
            BuildMapPhase(state, actions, runState);
        }
        else if (CombatManager.Instance != null && CombatManager.Instance.IsInProgress)
        {
            BuildCombatPhase(state, actions, runState);
        }
        else if (runState.IsGameOver)
        {
            bool isVictory = room != null && room.RoomType == RoomType.Event
                && runState.Players.Any(p => p.Creature?.IsAlive == true);
            state["phase"] = "game_over";
            state["game_over"] = new Dictionary<string, object>
            {
                ["result"] = isVictory ? "victory" : "defeat",
                ["floor_reached"] = runState.TotalFloor,
            };
            actions.Add(Act("return_to_menu"));
        }
        else if (room is EventRoom eventRoom)
        {
            BuildEventPhase(state, actions, eventRoom);
        }
        else if (room is RestSiteRoom restRoom)
        {
            BuildRestPhase(state, actions, restRoom);
        }
        else if (room is MerchantRoom shopRoom)
        {
            BuildShopPhase(state, actions, shopRoom, runState);
        }
        else
        {
            // Default: map selection (between rooms, or no room active)
            state["phase"] = "map_selection";
            BuildMapPhase(state, actions, runState);
        }

        state["available_actions"] = actions;
        return state;
    }

    // ---------------------------------------------------------------
    // Phase builders
    // ---------------------------------------------------------------

    private static void BuildCombatPhase(Dictionary<string, object?> state,
        List<Dictionary<string, object>> actions, RunState runState)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null) return;

        bool isPlayerTurn = CombatManager.Instance.IsPlayPhase;
        state["phase"] = isPlayerTurn ? "combat_player_turn" : "combat_enemy_turn";
        state["combat"] = ReadCombatState(combatState, runState);

        if (isPlayerTurn)
        {
            var player = runState.Players.FirstOrDefault();
            if (player?.PlayerCombatState == null) return;

            var pcs = player.PlayerCombatState;
            var hand = pcs.Hand.Cards;
            var livingEnemies = combatState.Enemies.Where(e => e.IsAlive).ToList();

            // Card play actions
            for (int i = 0; i < hand.Count; i++)
            {
                var card = hand[i];
                if (!card.CanPlay(out _, out _)) continue;

                bool needsTarget = card.TargetType == TargetType.AnyEnemy
                    || card.TargetType == TargetType.AnyAlly;

                if (needsTarget)
                {
                    var targets = card.TargetType == TargetType.AnyEnemy
                        ? livingEnemies
                        : combatState.Allies.Where(a => a.IsAlive).ToList();

                    for (int t = 0; t < targets.Count; t++)
                    {
                        actions.Add(new Dictionary<string, object>
                        {
                            ["type"] = "play_card",
                            ["card_index"] = i,
                            ["target_index"] = t,
                        });
                    }
                }
                else
                {
                    actions.Add(new Dictionary<string, object>
                    {
                        ["type"] = "play_card",
                        ["card_index"] = i,
                    });
                }
            }

            // Potion actions
            int potionIdx = 0;
            foreach (var potion in player.Potions)
            {
                if (potion != null)
                {
                    actions.Add(new Dictionary<string, object>
                    {
                        ["type"] = "use_potion",
                        ["potion_slot"] = potionIdx,
                    });
                }
                potionIdx++;
            }

            actions.Add(Act("end_turn"));
        }
    }

    private static void BuildRewardsPhase(Dictionary<string, object?> state,
        List<Dictionary<string, object>> actions, NRewardsScreen rewardsScreen)
    {
        state["phase"] = "combat_rewards";

        var rewardList = new List<Dictionary<string, object?>>();

        // Read rewards from the NRewardButton children in the screen's container
        try
        {
            var container = rewardsScreen.GetNode<Godot.Control>("%RewardsContainer");
            if (container != null)
            {
                int idx = 0;
                foreach (var child in container.GetChildren())
                {
                    if (child is NRewardButton rewardButton && rewardButton.Reward != null)
                    {
                        rewardList.Add(SerializeReward(rewardButton.Reward, idx));
                        idx++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.Log("Error reading rewards: " + ex.Message);
        }

        state["rewards"] = rewardList;

        // Add actions for each reward
        for (int i = 0; i < rewardList.Count; i++)
        {
            var r = rewardList[i];
            string rType = r["type"]?.ToString() ?? "";

            if (rType == "card")
            {
                actions.Add(new Dictionary<string, object>
                {
                    ["type"] = "take_reward",
                    ["reward_index"] = i,
                });
            }
            else
            {
                actions.Add(new Dictionary<string, object>
                {
                    ["type"] = "take_reward",
                    ["reward_index"] = i,
                });
            }
        }

        actions.Add(Act("proceed"));
    }

    private static Dictionary<string, object?> SerializeReward(Reward reward, int index)
    {
        var data = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["description"] = Loc(reward.Description),
        };

        switch (reward)
        {
            case GoldReward gold:
                data["type"] = "gold";
                data["value"] = gold.Amount;
                break;
            case PotionReward potion:
                data["type"] = "potion";
                data["id"] = potion.Potion?.Id.ToString();
                data["name"] = Loc(potion.Potion?.Title);
                break;
            case CardReward card:
                data["type"] = "card";
                data["cards"] = card.Cards.Select((c, i) => new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = c.Id.ToString(),
                    ["name"] = Loc(c.Title),
                }).ToList();
                break;
            case RelicReward relic:
                data["type"] = "relic";
                data["name"] = Loc(relic.Description);
                break;
            default:
                data["type"] = "other";
                break;
        }

        return data;
    }

    private static void BuildEventPhase(Dictionary<string, object?> state,
        List<Dictionary<string, object>> actions, EventRoom eventRoom)
    {
        state["phase"] = "event";

        var evt = eventRoom.LocalMutableEvent;
        var options = evt.CurrentOptions;

        var optionList = new List<Dictionary<string, object?>>();
        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            optionList.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["text"] = Loc(opt.Title),
                ["description"] = Loc(opt.Description),
                ["available"] = !opt.IsLocked && !opt.WasChosen,
                ["is_proceed"] = opt.IsProceed,
            });

            if (!opt.IsLocked && !opt.WasChosen)
            {
                actions.Add(new Dictionary<string, object>
                {
                    ["type"] = "choose_option",
                    ["option_index"] = i,
                });
            }
        }

        state["event"] = new Dictionary<string, object?>
        {
            ["title"] = Loc(evt.Title),
            ["options"] = optionList,
            ["is_finished"] = evt.IsFinished,
        };

        // When the event is finished with no remaining options, the game shows a
        // "Proceed" button. Expose this as an action.
        if (evt.IsFinished && optionList.Count == 0)
        {
            actions.Add(Act("proceed"));
        }
    }

    private static void BuildRestPhase(Dictionary<string, object?> state,
        List<Dictionary<string, object>> actions, RestSiteRoom restRoom)
    {
        state["phase"] = "rest_site";

        var options = restRoom.Options;
        var optionList = new List<Dictionary<string, object?>>();

        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            optionList.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["type"] = opt.OptionId,
                ["title"] = Loc(opt.Title),
                ["description"] = Loc(opt.Description),
                ["enabled"] = opt.IsEnabled,
            });

            if (opt.IsEnabled)
            {
                actions.Add(new Dictionary<string, object>
                {
                    ["type"] = "choose_rest_option",
                    ["option_index"] = i,
                });
            }
        }

        state["rest_site"] = new Dictionary<string, object?> { ["options"] = optionList };
    }

    private static void BuildShopPhase(Dictionary<string, object?> state,
        List<Dictionary<string, object>> actions, MerchantRoom shopRoom, RunState runState)
    {
        state["phase"] = "shop";

        var inv = shopRoom.Inventory;
        var player = runState.Players.FirstOrDefault();
        int gold = player?.Gold ?? 0;

        var cards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
            .Where(e => e.IsStocked)
            .Select((e, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = e.CreationResult?.Card?.Id.ToString(),
                ["name"] = Loc(e.CreationResult?.Card?.Title),
                ["cost"] = e.Cost,
                ["affordable"] = e.EnoughGold,
            })
            .ToList();

        var relics = inv.RelicEntries
            .Where(e => e.IsStocked)
            .Select((e, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = e.Model?.Id.ToString(),
                ["name"] = Loc(e.Model?.Title),
                ["cost"] = e.Cost,
                ["affordable"] = e.EnoughGold,
            })
            .ToList();

        var potions = inv.PotionEntries
            .Where(e => e.IsStocked)
            .Select((e, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = e.Model?.Id.ToString(),
                ["name"] = Loc(e.Model?.Title),
                ["cost"] = e.Cost,
                ["affordable"] = e.EnoughGold,
            })
            .ToList();

        var removal = inv.CardRemovalEntry;

        state["shop"] = new Dictionary<string, object?>
        {
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["can_remove_card"] = removal != null && removal.IsStocked,
            ["remove_cost"] = removal?.Cost,
            ["can_afford_remove"] = removal?.EnoughGold ?? false,
        };

        for (int i = 0; i < cards.Count; i++)
            if ((bool)(cards[i]["affordable"] ?? false))
                actions.Add(new Dictionary<string, object> { ["type"] = "buy_card", ["card_index"] = i });

        for (int i = 0; i < relics.Count; i++)
            if ((bool)(relics[i]["affordable"] ?? false))
                actions.Add(new Dictionary<string, object> { ["type"] = "buy_relic", ["relic_index"] = i });

        for (int i = 0; i < potions.Count; i++)
            if ((bool)(potions[i]["affordable"] ?? false))
                actions.Add(new Dictionary<string, object> { ["type"] = "buy_potion", ["potion_index"] = i });

        if (removal != null && removal.IsStocked && removal.EnoughGold)
            actions.Add(Act("remove_card"));

        actions.Add(Act("leave_shop"));
    }

    private static void BuildMapPhase(Dictionary<string, object?> state,
        List<Dictionary<string, object>> actions, RunState runState)
    {
        var map = runState.Map;
        if (map == null) return;

        var nodes = new List<Dictionary<string, object?>>();

        foreach (var point in map.GetAllMapPoints())
        {
            bool reachable = IsNodeReachable(point, runState);

            var children = point.Children
                .Select(c => new Dictionary<string, object?> { ["col"] = c.coord.col, ["row"] = c.coord.row })
                .ToList();

            nodes.Add(new Dictionary<string, object?>
            {
                ["col"] = point.coord.col,
                ["row"] = point.coord.row,
                ["type"] = point.PointType.ToString().ToLowerInvariant(),
                ["reachable"] = reachable,
                ["children"] = children,
            });

            if (reachable)
            {
                actions.Add(new Dictionary<string, object>
                {
                    ["type"] = "select_node",
                    ["col"] = point.coord.col,
                    ["row"] = point.coord.row,
                });
            }
        }

        var currentCoord = runState.CurrentMapCoord;
        state["map"] = new Dictionary<string, object?>
        {
            ["current_act"] = runState.CurrentActIndex + 1,
            ["current_floor"] = runState.TotalFloor,
            ["current_col"] = currentCoord?.col,
            ["current_row"] = currentCoord?.row,
            ["nodes"] = nodes,
        };
    }

    // ---------------------------------------------------------------
    // Combat state serialization
    // ---------------------------------------------------------------

    private static Dictionary<string, object?> ReadCombatState(CombatState combatState, RunState runState)
    {
        var player = runState.Players.FirstOrDefault();
        var pcs = player?.PlayerCombatState;

        var result = new Dictionary<string, object?>
        {
            ["turn"] = combatState.RoundNumber,
        };

        // Energy
        if (pcs != null)
        {
            result["energy"] = pcs.Energy;
            result["max_energy"] = pcs.MaxEnergy;
        }

        // Hand
        if (pcs != null)
        {
            result["hand"] = pcs.Hand.Cards.Select((c, i) => SerializeHandCard(c, i)).ToList();
            result["draw_pile_count"] = pcs.DrawPile.Cards.Count;
            result["discard_pile"] = pcs.DiscardPile.Cards.Select(c => SerializeCardBrief(c)).ToList();
            result["exhaust_pile"] = pcs.ExhaustPile.Cards.Select(c => SerializeCardBrief(c)).ToList();
        }

        // Enemies
        result["enemies"] = combatState.Enemies.Select((e, i) => SerializeEnemy(e, i)).ToList();

        // Player powers
        if (player?.Creature?.Powers != null && player.Creature.Powers.Count > 0)
        {
            result["player_powers"] = player.Creature.Powers
                .Select(p => new Dictionary<string, object?>
                {
                    ["id"] = p.Id.ToString(),
                    ["stacks"] = p.Amount,
                }).ToList();
        }

        return result;
    }

    private static Dictionary<string, object?> SerializeHandCard(CardModel card, int index)
    {
        bool canPlay = card.CanPlay(out var reason, out _);
        int cost = card.EnergyCost?.Canonical ?? -1;

        string? description = null;
        try { description = card.GetDescriptionForPile(PileType.Hand); } catch { }

        return new Dictionary<string, object?>
        {
            ["hand_index"] = index,
            ["id"] = card.Id.ToString(),
            ["name"] = Loc(card.Title),
            ["description"] = description,
            ["cost"] = cost,
            ["type"] = card.Type.ToString(),
            ["rarity"] = card.Rarity.ToString(),
            ["playable"] = canPlay,
            ["unplayable_reason"] = canPlay ? null : reason.ToString(),
            ["target_type"] = card.TargetType.ToString(),
            ["upgraded"] = card.CurrentUpgradeLevel > 0,
        };
    }

    private static Dictionary<string, object?> SerializeCardBrief(CardModel card)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = card.Id.ToString(),
            ["name"] = Loc(card.Title),
            ["upgraded"] = card.CurrentUpgradeLevel > 0,
        };
    }

    private static Dictionary<string, object?> SerializeEnemy(Creature enemy, int index)
    {
        var data = new Dictionary<string, object?>
        {
            ["enemy_index"] = index,
            ["id"] = enemy.ModelId.ToString(),
            ["name"] = enemy.Name,
            ["hp"] = enemy.CurrentHp,
            ["max_hp"] = enemy.MaxHp,
            ["block"] = enemy.Block,
            ["is_alive"] = enemy.IsAlive,
        };

        // Intent
        try
        {
            var nextMove = enemy.Monster?.NextMove;
            if (nextMove != null)
            {
                data["intent"] = nextMove.Id;
                var intentList = new List<Dictionary<string, object?>>();
                if (nextMove.Intents != null)
                {
                    foreach (var intent in nextMove.Intents)
                    {
                        var intentData = new Dictionary<string, object?>
                        {
                            ["type"] = intent.IntentType.ToString().ToLowerInvariant(),
                        };

                        if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent attackIntent)
                        {
                            try
                            {
                                // Get targets for damage calc
                                var combatState = CombatManager.Instance?.DebugOnlyGetState();
                                var targets = combatState?.Players
                                    .Select(p => p.Creature)
                                    .Where(c => c != null && c.IsAlive)
                                    .Cast<MegaCrit.Sts2.Core.Entities.Creatures.Creature>();
                                if (targets != null)
                                {
                                    intentData["damage"] = attackIntent.GetSingleDamage(targets, enemy);
                                    intentData["hits"] = attackIntent.Repeats;
                                    intentData["total_damage"] = attackIntent.GetTotalDamage(targets, enemy);
                                }
                            }
                            catch { }
                        }

                        intentList.Add(intentData);
                    }
                }
                data["intents"] = intentList;
                // Convenience top-level fields for simple cases
                data["intent_types"] = intentList.Select(i => i["type"]).ToList();
                var firstAttack = intentList.FirstOrDefault(i => (string?)i["type"] == "attack");
                if (firstAttack != null)
                {
                    data["intent_damage"] = firstAttack.GetValueOrDefault("damage");
                    data["intent_hits"] = firstAttack.GetValueOrDefault("hits");
                    data["intent_total_damage"] = firstAttack.GetValueOrDefault("total_damage");
                }
            }
        }
        catch { }

        // Powers
        if (enemy.Powers != null && enemy.Powers.Count > 0)
        {
            data["powers"] = enemy.Powers.Select(p => new Dictionary<string, object?>
            {
                ["id"] = p.Id.ToString(),
                ["stacks"] = p.Amount,
            }).ToList();
        }

        return data;
    }

    // ---------------------------------------------------------------
    // Card selection state
    // ---------------------------------------------------------------

    private static Dictionary<string, object?> ReadGridCardSelection(NCardGridSelectionScreen screen)
    {
        // _cards is protected, access via reflection
        var cardsField = typeof(NCardGridSelectionScreen).GetField("_cards",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cards = cardsField?.GetValue(screen) as IReadOnlyList<CardModel>;

        return new Dictionary<string, object?>
        {
            ["options"] = cards?.Select((c, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["id"] = c.Id.ToString(),
                ["name"] = Loc(c.Title),
                ["upgraded"] = c.CurrentUpgradeLevel > 0,
            }).ToList() ?? new List<Dictionary<string, object?>>(),
        };
    }

    private static List<Dictionary<string, object>> BuildGridCardSelectionActions(NCardGridSelectionScreen screen)
    {
        var actions = new List<Dictionary<string, object>>();

        var cardsField = typeof(NCardGridSelectionScreen).GetField("_cards",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cards = cardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null) return actions;

        for (int i = 0; i < cards.Count; i++)
        {
            actions.Add(new Dictionary<string, object>
            {
                ["type"] = "select_cards",
                ["indices"] = new[] { i },
            });
        }

        return actions;
    }

    private static Dictionary<string, object?> ReadCardRewardSelection(NCardRewardSelectionScreen screen)
    {
        var optionsField = typeof(NCardRewardSelectionScreen).GetField("_options",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var options = optionsField?.GetValue(screen) as IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>;

        return new Dictionary<string, object?>
        {
            ["screen_type"] = "card_reward",
            ["cancelable"] = true, // card rewards can always be skipped
            ["options"] = options?.Select((o, i) =>
            {
                string? desc = null;
                try { desc = o.Card.GetDescriptionForPile(PileType.None); } catch { }
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = o.Card.Id.ToString(),
                    ["name"] = Loc(o.Card.Title),
                    ["upgraded"] = o.Card.CurrentUpgradeLevel > 0,
                    ["description"] = desc,
                };
            }).ToList() ?? new List<Dictionary<string, object?>>(),
        };
    }

    private static List<Dictionary<string, object>> BuildCardRewardSelectionActions(NCardRewardSelectionScreen screen)
    {
        var actions = new List<Dictionary<string, object>>();

        var cardRowField = typeof(NCardRewardSelectionScreen).GetField("_cardRow",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cardRow = cardRowField?.GetValue(screen) as Godot.Control;

        int count = cardRow?.GetChildren().OfType<NGridCardHolder>().Count() ?? 0;
        for (int i = 0; i < count; i++)
        {
            actions.Add(new Dictionary<string, object>
            {
                ["type"] = "select_cards",
                ["indices"] = new[] { i },
            });
        }

        // Can always skip card rewards
        actions.Add(new Dictionary<string, object>
        {
            ["type"] = "cancel_selection",
        });

        return actions;
    }

    private static Dictionary<string, object?> ReadChooseCardSelection(NChooseACardSelectionScreen screen)
    {
        var cardsField = typeof(NChooseACardSelectionScreen).GetField("_cards",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cards = cardsField?.GetValue(screen) as IReadOnlyList<CardModel>;

        var canSkipField = typeof(NChooseACardSelectionScreen).GetField("_canSkip",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        bool canSkip = canSkipField?.GetValue(screen) is true;

        return new Dictionary<string, object?>
        {
            ["screen_type"] = "choose_card",
            ["cancelable"] = canSkip,
            ["options"] = cards?.Select((c, i) =>
            {
                string? desc = null;
                try { desc = c.GetDescriptionForPile(PileType.None); } catch { }
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = c.Id.ToString(),
                    ["name"] = Loc(c.Title),
                    ["upgraded"] = c.CurrentUpgradeLevel > 0,
                    ["description"] = desc,
                };
            }).ToList() ?? new List<Dictionary<string, object?>>(),
        };
    }

    private static List<Dictionary<string, object>> BuildChooseCardSelectionActions(NChooseACardSelectionScreen screen)
    {
        var actions = new List<Dictionary<string, object>>();

        var cardsField = typeof(NChooseACardSelectionScreen).GetField("_cards",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cards = cardsField?.GetValue(screen) as IReadOnlyList<CardModel>;
        if (cards == null) return actions;

        for (int i = 0; i < cards.Count; i++)
        {
            actions.Add(new Dictionary<string, object>
            {
                ["type"] = "select_cards",
                ["indices"] = new[] { i },
            });
        }

        var canSkipField = typeof(NChooseACardSelectionScreen).GetField("_canSkip",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (canSkipField?.GetValue(screen) is true)
        {
            actions.Add(new Dictionary<string, object>
            {
                ["type"] = "cancel_selection",
            });
        }

        return actions;
    }

    // ---------------------------------------------------------------
    // Shared info builders
    // ---------------------------------------------------------------

    private static void AddRunAndPlayerInfo(Dictionary<string, object?> state)
    {
        try
        {
            var runState = GetRunState();
            if (runState == null) return;

            var player = runState.Players.FirstOrDefault();

            state["run"] = new Dictionary<string, object?>
            {
                ["character"] = player?.Character?.Id.ToString(),
                ["act"] = runState.CurrentActIndex + 1,
                ["floor"] = runState.TotalFloor,
                ["seed"] = runState.Rng?.StringSeed,
                ["ascension"] = runState.AscensionLevel,
            };

            if (player != null)
            {
                state["player"] = new Dictionary<string, object>
                {
                    ["hp"] = player.Creature?.CurrentHp ?? 0,
                    ["max_hp"] = player.Creature?.MaxHp ?? 0,
                    ["block"] = player.Creature?.Block ?? 0,
                    ["gold"] = player.Gold,
                };
            }
        }
        catch { }
    }

    private static void AddDeckRelicsPotions(Dictionary<string, object?> state, RunState runState)
    {
        var player = runState.Players.FirstOrDefault();
        if (player == null) return;

        try
        {
            state["deck"] = player.Deck.Cards.Select(c =>
            {
                string? desc = null;
                try { desc = c.GetDescriptionForPile(PileType.None); } catch { }
                return new Dictionary<string, object?>
                {
                    ["id"] = c.Id.ToString(),
                    ["name"] = Loc(c.Title),
                    ["upgraded"] = c.CurrentUpgradeLevel > 0,
                    ["description"] = desc,
                };
            }).ToList();
        }
        catch { }

        try
        {
            state["relics"] = player.Relics.Select(r =>
            {
                string? desc = null;
                try { desc = Loc(r.DynamicDescription); } catch { }
                return new Dictionary<string, object?>
                {
                    ["id"] = r.Id.ToString(),
                    ["name"] = Loc(r.Title),
                    ["description"] = desc,
                };
            }).ToList();
        }
        catch { }

        try
        {
            state["potions"] = player.Potions
                .Select((p, i) => new Dictionary<string, object?>
                {
                    ["slot"] = i,
                    ["id"] = p?.Id.ToString(),
                    ["name"] = Loc(p?.Title),
                })
                .Where(p => p["id"] != null)
                .ToList();
        }
        catch { }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static bool IsNodeReachable(MapPoint point, RunState runState)
    {
        var currentCoord = runState.CurrentMapCoord;
        if (currentCoord.HasValue)
        {
            var currentPoint = runState.Map?.GetPoint(currentCoord.Value);
            return currentPoint?.Children.Contains(point) ?? false;
        }
        // No current position -- starting row is reachable
        return point.coord.row == 0;
    }

    private static Dictionary<string, object> Act(string type)
    {
        return new Dictionary<string, object> { ["type"] = type };
    }

    /// <summary>
    /// Resolve a LocString to its displayed text. LocString.ToString() returns the
    /// type name; GetFormattedText() returns the actual localized string.
    /// </summary>
    private static string? Loc(MegaCrit.Sts2.Core.Localization.LocString? loc)
    {
        if (loc == null) return null;
        try { return loc.GetFormattedText(); }
        catch { return null; }
    }

    /// <summary>
    /// Passthrough for properties that already return string.
    /// </summary>
    private static string? Loc(string? s) => s;
}
