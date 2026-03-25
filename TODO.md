# Snecko Eye -- TODO

## Working Now

- [x] HTTP server on localhost:9000 with main thread dispatch
- [x] In-game overlay showing API URL
- [x] GET /help -- full API schema
- [x] GET /state -- phases: main_menu, map_selection, combat_player_turn, combat_enemy_turn, combat_rewards, event, rest_site, shop, game_over, awaiting_card_selection
- [x] GET /state -- combat: hand cards (playability/cost/target/description), enemies (HP/block/intent/powers), energy, draw/discard/exhaust piles, player powers
- [x] GET /state -- map nodes with reachability, types, edges (children), current position
- [x] GET /state -- events with options + descriptions, is_finished, proceed
- [x] GET /state -- rest site options
- [x] GET /state -- shop items with costs/affordability
- [x] GET /state -- rewards from NRewardsScreen UI children (NRewardButton.Reward)
- [x] GET /state -- main menu with has_saved_run
- [x] GET /state -- card selection overlays with card descriptions (all 3 screen types)
- [x] GET /state -- relic descriptions (DynamicDescription)
- [x] GET /state -- deck card descriptions (GetDescriptionForPile)
- [x] LocString resolution via GetFormattedText()
- [x] POST /action -- start_run, continue_run, abandon_run
- [x] POST /action -- play_card, end_turn, use_potion
- [x] POST /action -- select_node (with mapGenerationCount + rewards guard)
- [x] POST /action -- choose_option (events)
- [x] POST /action -- choose_rest_option (with per-floor guard against infinite smith)
- [x] POST /action -- buy_card, buy_relic, buy_potion, remove_card
- [x] POST /action -- leave_shop (opens NMapScreen)
- [x] POST /action -- take_reward (claims via Reward.OnSelectWrapper)
- [x] POST /action -- proceed (ProceedFromTerminalRewardsScreen + event finish)
- [x] POST /action -- return_to_menu
- [x] POST /action -- select_cards (calls OnCardClicked on NCardGridSelectionScreen)
- [x] POST /log -- event log sidebar for AI thinking/info display

## Python CLI (sts2.py)

- [x] SDK functions: state(), action(), play_card(), end_turn(), etc.
- [x] CLI with all commands: show, play, end, take, pick, skip, node, choose, rest, buy, etc.
- [x] All state-changing commands auto-show updated state
- [x] `play` polls for state change (no stale indices)
- [x] `node` polls for phase change (no fixed sleep)
- [x] ASCII map visualization with node types, edges, reachable markers
- [x] BBCode/Godot markup stripped from all descriptions
- [x] Card descriptions shown in hand, deck, card rewards, and card selection
- [x] Relic descriptions shown via `relics` command
- [x] Event option descriptions shown
- [x] `skip` blocks if unclaimed gold/potion/relic rewards exist (--force to override)

## Vakuu - AI Player (vakuu/)

- [x] Headless Claude Code launcher (run.bat)
- [x] Stream watcher posts thinking + info to in-game log (watcher.py)
- [x] Prompt with game knowledge, combat strategy, card evaluation

## Verified End-to-End Flow

Tested multiple autonomous runs through Act 1:
```
main_menu -> start_run -> Neow event -> proceed -> map -> combat x5 ->
rewards (gold + cards collected) -> events -> shop -> rest sites -> game_over
```

## Recently Fixed

- [x] **Infinite smith upgrades** -- rest site now tracks floor, blocks repeated option selection
- [x] **Rewards skipped** -- select_node blocked while NRewardsScreen is active and incomplete
- [x] **Stale play state** -- `play` command polls until hand size or phase changes
- [x] **Silent commands** -- take, pick, end all auto-show state after executing
- [x] **BBCode in descriptions** -- strip_markup() removes [gold], [purple], [img] tags
- [x] **Card reward descriptions** -- all 3 card selection screens now include card text
- [x] **Event descriptions** -- shown in CLI alongside option names
- [x] **Command chaining** -- prompt prohibits && chaining (output lost in stream-json)
- [x] **Node sleep** -- replaced fixed 3s sleep with phase-change polling

## Open Issues

- [ ] **Boss relic choice** -- overlay detection needed for boss relic selection screen
- [ ] **Treasure room** -- may already work via rewards overlay, needs verification
- [ ] **Hand selection during combat** -- cards like "discard 1" use NCombatRoom.Ui.Hand.SelectCards, not an overlay screen. Different detection needed.
- [ ] **Relic reward from events** -- needs verification that relic rewards display/collect properly
- [ ] **Potion use in combat** -- potion slot command exists but untested by AI player
- [ ] **Multi-act support** -- only Act 1 tested so far

## Design Philosophy

**Detect -> Input, never Intercept.**
- ApiCardSelector approach was REMOVED -- it intercepted ALL card selections globally and caused hangs
- Instead: detect overlay screens via NOverlayStack.Peek(), simulate clicks via reflection
- Game UI continues working normally, API is a parallel control channel
