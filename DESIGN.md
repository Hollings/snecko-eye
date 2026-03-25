# Snecko Eye -- HTTP API Mod for Slay the Spire 2

## Overview

A mod that exposes Slay the Spire 2's game state and actions through a local
HTTP API. Any program (Claude Code, a bot, a script) can play the game by
polling state and sending actions via curl/HTTP.

STS2 is fully turn-based with zero timing requirements. The game waits
indefinitely for player input at every decision point. This mod simply reads
the current state and submits actions through the game's own internal APIs.

```bash
# See what's happening
curl localhost:9000/state

# Take an action
curl -X POST localhost:9000/action -d '{"type": "play_card", "card_index": 0, "target_index": 0}'

# End turn
curl -X POST localhost:9000/action -d '{"type": "end_turn"}'
```

---

## Architecture

```
SneckoEye/
  SneckoEye.json                 # Mod manifest (affects_gameplay: false)
  SneckoEye.csproj               # .NET 9, refs sts2.dll + 0Harmony + GodotSharp
  src/
    ModEntry.cs                  # [ModInitializer] entry, starts HTTP server
    HttpServer.cs                # HttpListener on localhost:9000, background thread
    GameStateReader.cs           # Reads game state -> JSON
    ActionExecutor.cs            # Parses action JSON, calls game APIs
    ApiCardSelector.cs           # ICardSelector impl for mid-action choices
    GamePhase.cs                 # Phase detection logic
```

### Threading Model

- **HTTP server** runs on a background thread (HttpListener)
- **Game state reads** must happen on the main thread (Godot scene tree)
- **Action execution** must happen on the main thread
- Bridge: HTTP handler queues work to main thread via `Godot.Callable` +
  `CallDeferred`, waits for result via `TaskCompletionSource`

### Why This Is Safe

The action queue (`RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue`)
is the same system used for multiplayer sync. Our mod is essentially a "remote
player" feeding actions through the exact same pipeline as a real multiplayer
client. The game validates all actions before executing them.

---

## API

### GET /state

Returns the complete current game state as JSON.

**Response:**
```json
{
  "phase": "combat_player_turn",
  "run": {
    "character": "CHARACTER.IRONCLAD",
    "act": 1,
    "floor": 7,
    "seed": "ABC123",
    "ascension": 0
  },
  "player": {
    "hp": 65,
    "max_hp": 80,
    "block": 0,
    "energy": 3,
    "max_energy": 3,
    "gold": 99
  },
  "deck": [
    {"id": "CARD.STRIKE_IRONCLAD", "name": "Strike", "upgraded": false},
    {"id": "CARD.DEFEND_IRONCLAD", "name": "Defend", "upgraded": false}
  ],
  "relics": [
    {"id": "RELIC.BURNING_BLOOD", "name": "Burning Blood"}
  ],
  "potions": [
    {"slot": 0, "id": "POTION.FIRE_POTION", "name": "Fire Potion", "requires_target": true}
  ],
  "combat": {
    "turn": 2,
    "hand": [
      {"hand_index": 0, "id": "CARD.STRIKE_IRONCLAD", "cost": 1, "playable": true,
       "requires_target": true},
      {"hand_index": 1, "id": "CARD.DEFEND_IRONCLAD", "cost": 1, "playable": true,
       "requires_target": false},
      {"hand_index": 2, "id": "CARD.BASH", "cost": 2, "playable": true,
       "requires_target": true}
    ],
    "enemies": [
      {"enemy_index": 0, "id": "MONSTER.JAW_WORM", "name": "Jaw Worm",
       "hp": 30, "max_hp": 44, "block": 0,
       "intent": "attack",
       "powers": []}
    ],
    "draw_pile_count": 4,
    "discard_pile": [
      {"id": "CARD.STRIKE_IRONCLAD", "name": "Strike"}
    ],
    "exhaust_pile": []
  },
  "available_actions": [
    {"type": "play_card", "card_index": 0, "requires_target": true,
     "valid_targets": [0]},
    {"type": "play_card", "card_index": 1, "requires_target": false},
    {"type": "play_card", "card_index": 2, "requires_target": true,
     "valid_targets": [0]},
    {"type": "use_potion", "potion_slot": 0, "requires_target": true,
     "valid_targets": [0]},
    {"type": "end_turn"}
  ]
}
```

The `combat`, `map`, `rewards`, `event`, `shop`, `rest_site`, and
`card_selection` fields are present only when relevant to the current phase.
`available_actions` always lists every valid action for the current state.

### POST /action

Executes a game action. Request body is JSON.

**Response:**
```json
{"success": true}
```
or
```json
{"success": false, "error": "Cannot play card: not enough energy"}
```

---

## Game Phases and Actions

### phase: "game_not_started"

The game is at the main menu or not in a run.

```json
{"available_actions": [
  {"type": "start_run"}
]}
```

Action: `{"type": "start_run"}`

### phase: "character_select"

Choosing a character for a new run.

```json
{
  "characters": [
    {"index": 0, "id": "CHARACTER.IRONCLAD", "name": "Ironclad"},
    {"index": 1, "id": "CHARACTER.SILENT", "name": "Silent"}
  ],
  "available_actions": [
    {"type": "select_character", "character_index": 0},
    {"type": "select_character", "character_index": 1}
  ]
}
```

### phase: "neow"

Neow/Ancient event at run start.

```json
{
  "event": {
    "title": "Neow",
    "description": "...",
    "options": [
      {"index": 0, "text": "Obtain a random common relic", "available": true},
      {"index": 1, "text": "Lose 10% max HP, gain a rare relic", "available": true}
    ]
  },
  "available_actions": [
    {"type": "choose_option", "option_index": 0},
    {"type": "choose_option", "option_index": 1}
  ]
}
```

### phase: "map_selection"

Choosing the next room on the map.

```json
{
  "map": {
    "current_act": 1,
    "current_floor": 3,
    "nodes": [
      {"col": 0, "row": 4, "type": "monster", "reachable": true},
      {"col": 1, "row": 4, "type": "elite", "reachable": true},
      {"col": 2, "row": 4, "type": "rest_site", "reachable": false}
    ]
  },
  "available_actions": [
    {"type": "select_node", "col": 0, "row": 4},
    {"type": "select_node", "col": 1, "row": 4}
  ]
}
```

Only reachable nodes appear in `available_actions`.

### phase: "combat_player_turn"

Player's turn in combat. See full example above.

Actions:
- `{"type": "play_card", "card_index": N, "target_index": M}` -- target_index
  only needed if requires_target is true
- `{"type": "use_potion", "potion_slot": N, "target_index": M}`
- `{"type": "end_turn"}`

### phase: "combat_enemy_turn"

Enemy is acting. No available actions. Poll until phase changes.

```json
{
  "phase": "combat_enemy_turn",
  "available_actions": []
}
```

### phase: "awaiting_card_selection"

A card or effect requires the player to select cards (discard, exhaust,
upgrade, scry, etc.). This interrupts combat or rest site flow.

```json
{
  "phase": "awaiting_card_selection",
  "card_selection": {
    "prompt": "Choose a card to exhaust.",
    "min_select": 1,
    "max_select": 1,
    "cancelable": false,
    "options": [
      {"index": 0, "id": "CARD.STRIKE_IRONCLAD", "name": "Strike"},
      {"index": 1, "id": "CARD.DEFEND_IRONCLAD", "name": "Defend"},
      {"index": 2, "id": "CARD.BASH", "name": "Bash"}
    ]
  },
  "available_actions": [
    {"type": "select_cards", "indices": [0]},
    {"type": "select_cards", "indices": [1]},
    {"type": "select_cards", "indices": [2]}
  ]
}
```

When `max_select > 1`, the client can send multiple indices:
`{"type": "select_cards", "indices": [0, 2]}`

When `cancelable` is true, the client can send:
`{"type": "cancel_selection"}`

### phase: "combat_rewards"

Post-combat reward screen. Multiple rewards may be available.

```json
{
  "rewards": [
    {"index": 0, "type": "gold", "value": 25},
    {"index": 1, "type": "potion", "id": "POTION.FIRE_POTION"},
    {"index": 2, "type": "card", "cards": [
      {"index": 0, "id": "CARD.INFLAME"},
      {"index": 1, "id": "CARD.CLEAVE"},
      {"index": 2, "id": "CARD.ANGER"}
    ]},
    {"index": 3, "type": "relic", "id": "RELIC.GOLDEN_IDOL"}
  ],
  "available_actions": [
    {"type": "take_reward", "reward_index": 0},
    {"type": "take_reward", "reward_index": 1},
    {"type": "pick_card", "reward_index": 2, "card_index": 0},
    {"type": "pick_card", "reward_index": 2, "card_index": 1},
    {"type": "pick_card", "reward_index": 2, "card_index": 2},
    {"type": "skip_card_reward", "reward_index": 2},
    {"type": "take_reward", "reward_index": 3},
    {"type": "proceed"}
  ]
}
```

`proceed` leaves the reward screen (skipping unclaimed rewards).

### phase: "event"

An event room with choices.

```json
{
  "event": {
    "title": "Big Fish",
    "description": "You find a big fish...",
    "options": [
      {"index": 0, "text": "Eat (heal 5 HP)", "available": true},
      {"index": 1, "text": "Feed (max HP +5)", "available": true},
      {"index": 2, "text": "Leave", "available": true}
    ]
  },
  "available_actions": [
    {"type": "choose_option", "option_index": 0},
    {"type": "choose_option", "option_index": 1},
    {"type": "choose_option", "option_index": 2}
  ]
}
```

### phase: "rest_site"

Rest site with heal/upgrade options.

```json
{
  "rest_site": {
    "options": [
      {"index": 0, "type": "rest", "description": "Heal 30% of max HP"},
      {"index": 1, "type": "smith", "description": "Upgrade a card"}
    ]
  },
  "available_actions": [
    {"type": "choose_rest_option", "option_index": 0},
    {"type": "choose_rest_option", "option_index": 1}
  ]
}
```

Choosing "smith" transitions to `awaiting_card_selection` phase with
upgradeable cards as options.

### phase: "shop"

In the shop.

```json
{
  "shop": {
    "cards": [
      {"index": 0, "id": "CARD.HEADBUTT", "cost": 75, "affordable": true},
      {"index": 1, "id": "CARD.SHRUG_IT_OFF", "cost": 75, "affordable": true}
    ],
    "relics": [
      {"index": 0, "id": "RELIC.VAJRA", "cost": 150, "affordable": false}
    ],
    "potions": [
      {"index": 0, "id": "POTION.BLOCK_POTION", "cost": 50, "affordable": true}
    ],
    "can_remove_card": true,
    "remove_cost": 75,
    "can_afford_remove": true
  },
  "available_actions": [
    {"type": "buy_card", "card_index": 0},
    {"type": "buy_card", "card_index": 1},
    {"type": "buy_potion", "potion_index": 0},
    {"type": "remove_card"},
    {"type": "leave_shop"}
  ]
}
```

`remove_card` transitions to `awaiting_card_selection` with the full deck.

### phase: "boss_relic"

Choosing from boss relics after a boss fight.

```json
{
  "boss_relics": [
    {"index": 0, "id": "RELIC.ASTROLABE", "name": "Astrolabe"},
    {"index": 1, "id": "RELIC.COFFEE_DRIPPER", "name": "Coffee Dripper"},
    {"index": 2, "id": "RELIC.CURSED_KEY", "name": "Cursed Key"}
  ],
  "available_actions": [
    {"type": "pick_boss_relic", "relic_index": 0},
    {"type": "pick_boss_relic", "relic_index": 1},
    {"type": "pick_boss_relic", "relic_index": 2}
  ]
}
```

### phase: "game_over"

Run ended (victory or death).

```json
{
  "game_over": {
    "result": "defeat",
    "killed_by": "MONSTER.BOOK_OF_STABBING",
    "floor_reached": 23,
    "score": 412
  },
  "available_actions": [
    {"type": "return_to_menu"}
  ]
}
```

---

## Mid-Action Card Selection

The game's `ICardSelector` test interface (`CardSelectCmd.UseSelector()`)
lets us intercept ALL card selection UI. The mod pushes an `ApiCardSelector`
onto the selector stack at initialization.

When the game requests a card selection:

1. `ApiCardSelector.GetSelectedCards()` is called with the options, min, max
2. The selector stores the pending choice and sets phase to `awaiting_card_selection`
3. The game's async/await system naturally pauses waiting for our Task to resolve
4. The next `GET /state` poll sees the pending choice with options listed
5. Client sends `POST /action {"type": "select_cards", "indices": [1]}`
6. The selector resolves its `TaskCompletionSource` with the chosen cards
7. The game resumes execution seamlessly

No freezing, no hacks. The game's own test infrastructure handles the flow.

---

## Implementation Details

### Game Phase Detection

```csharp
public static string DetectPhase()
{
    // Check in priority order
    if (PendingCardSelection != null)
        return "awaiting_card_selection";
    if (CombatManager.Instance.IsInProgress)
    {
        if (CombatManager.Instance.IsPlayPhase)
            return "combat_player_turn";
        else
            return "combat_enemy_turn";
    }
    // Check for reward screen, event, rest site, shop, map, etc.
    // by inspecting RunManager.Instance state
}
```

### Action Execution (key APIs)

```csharp
// Play card on target
var card = combatState.GetCardInHand(cardIndex);
var target = combatState.Enemies[targetIndex];
var action = new PlayCardAction(card, target);
RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

// End turn
PlayerCmd.EndTurn(player, canBackOut: true);

// Select map node
var vote = new MapVote { coord = new MapCoord(col, row) };
var action = new VoteForMapCoordAction(player, currentLocation, vote);
RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

// Use potion
var potion = player.GetPotionAtSlotIndex(slot);
var action = new UsePotionAction(potion, target, inCombat);
RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action);

// Event choice
await eventOption.Chosen();

// Rest site option
await restOption.OnSelect();

// Shop purchase
await merchantEntry.OnTryPurchaseWrapper(inventory);

// Card reward (pick specific card)
await cardReward.OnSelectWrapper(); // triggers card selection UI -> our selector

// Mid-action card selection (resolved via ApiCardSelector)
pendingSelection.Resolve(selectedCards);
```

### HTTP Server

Uses `System.Net.HttpListener` (built into .NET 9):

```csharp
var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:9000/");
listener.Start();

while (running)
{
    var context = await listener.GetContextAsync();
    // Route to handler based on path
    // Marshal to main thread for game state access
}
```

All game API calls are marshaled to Godot's main thread via
`Callable.From(() => ...).CallDeferred()` or similar pattern.

### Thread Safety

- `GET /state`: queues a read on main thread, blocks HTTP thread until
  result is ready via `TaskCompletionSource`
- `POST /action`: queues action on main thread, blocks until executed
- `ApiCardSelector`: resolves `TaskCompletionSource` from HTTP thread,
  game resumes on main thread (already awaiting the Task)

---

## Limitations

- **Single player only.** Multiplayer would require handling multiple players'
  turns and choices. Could be added later.
- **No visual feedback.** The game UI still runs and shows everything, but the
  API client doesn't see rendered graphics. The JSON state is the source of
  truth for the API client.
- **Enemy intents.** The game shows intent icons (attack, defend, buff). We
  expose the intent type but not exact damage numbers (unless we can read
  them from the creature's intent data).
- **Animations.** The game plays animations between actions. The API client
  should poll until the phase settles before sending the next action. A small
  delay between actions (~200ms) is sufficient.

---

## Development Plan

1. Scaffold mod project (manifest, csproj, ModEntry)
2. Implement HttpServer (background thread, routing, main thread dispatch)
3. Implement GamePhase detection
4. Implement GameStateReader (JSON serialization of full game state)
5. Implement ActionExecutor (all action types)
6. Implement ApiCardSelector (mid-action card choices)
7. Test with curl: play through a full run manually via API
8. Hook up Claude Code and see what happens

---

## Reference

- STS2 modding: See decompiled game code for hooks reference
- STS2 decompiled: Use ILSpy/dnSpy on `sts2.dll` from game install
- Key game APIs:
  - `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(action)`
  - `CombatManager.Instance.IsInProgress / IsPlayPhase`
  - `CardSelectCmd.UseSelector(selector)` -- test card selection interface
  - `PlayerCmd.EndTurn(player, canBackOut: true)`
  - `PlayCardAction`, `VoteForMapCoordAction`, `UsePotionAction`
  - `Reward.OnSelectWrapper()`, `EventOption.Chosen()`, `RestSiteOption.OnSelect()`
