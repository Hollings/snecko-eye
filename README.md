# Snecko Eye

An HTTP API mod for Slay the Spire 2 that exposes game state and accepts
actions via `localhost:9000`. Lets any program interact with the game -- bots,
AI agents, scripts, trackers, or anything else.

## Install

1. Build the mod:
   ```
   dotnet build -p:STS2GameDir="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
   ```

2. Copy to game mods folder:
   ```
   mkdir "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\SneckoEye"
   copy bin\SneckoEye.dll "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\SneckoEye\"
   copy SneckoEye.json "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\SneckoEye\"
   ```

3. Launch Slay the Spire 2. Accept mod loading on first launch (game will quit -- relaunch).

4. A small green label appears in the top-right corner confirming the API is running.

## Usage

```bash
# API docs and schema
curl localhost:9000/help

# Current game state
curl localhost:9000/state

# Take an action
curl -X POST localhost:9000/action -d '{"type": "end_turn"}'
```

## How it works

The mod starts a lightweight HTTP server on a background thread. Game state is
read from Godot's main thread via `CallDeferred`. Actions are executed through
the game's own internal APIs -- the same action queue system used for
multiplayer, so everything is safe and validated.

STS2 is fully turn-based. The game waits indefinitely for player input at
every decision point. No timing or speed requirements. Poll `/state` at
whatever rate you want, think as long as you need, then send an action.

### Endpoints

| Endpoint | Method | Description |
|---|---|---|
| `/help` | GET | API schema, all phases, all action types, usage examples |
| `/state` | GET | Full game state as JSON (phase, combat, map, deck, relics, etc.) |
| `/action` | POST | Execute an action (JSON body with `type` field) |
| `/log` | POST | Send a message to the in-game event log sidebar |

### Game phases

The `phase` field in `/state` tells you what the game is waiting for:

| Phase | What's happening | Actions |
|---|---|---|
| `main_menu` | No run active | `start_run`, `continue_run` |
| `map_selection` | Choosing next room | `select_node` |
| `combat_player_turn` | Your turn in combat | `play_card`, `use_potion`, `end_turn` |
| `combat_enemy_turn` | Enemy acting | (poll until it changes) |
| `combat_rewards` | Post-combat rewards | `take_reward`, `proceed` |
| `awaiting_card_selection` | Card choice needed | `select_cards`, `cancel_selection` |
| `event` | Event room | `choose_option` |
| `rest_site` | Rest site | `choose_rest_option` |
| `shop` | In the shop | `buy_card`, `buy_relic`, `buy_potion`, `remove_card`, `leave_shop` |
| `game_over` | Run ended | `return_to_menu` |

### Example: play a combat turn

```bash
# See the state
curl -s localhost:9000/state | jq '.combat.hand'
# [{"hand_index":0,"id":"CARD.STRIKE_IRONCLAD","cost":1,"playable":true,"description":"Deal 6 damage."}, ...]

# Play card 0 on enemy 0
curl -X POST localhost:9000/action -d '{"type":"play_card","card_index":0,"target_index":0}'
# {"success": true}

# End turn
curl -X POST localhost:9000/action -d '{"type":"end_turn"}'
```
