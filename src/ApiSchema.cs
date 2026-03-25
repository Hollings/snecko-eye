using System.Text.Json;
using System.Text.Json.Serialization;

namespace SneckoEye;

public static class ApiSchema
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetSchema()
    {
        var schema = new
        {
            name = "STS2 AutoPlay API",
            version = "0.1.0",
            description = "HTTP API for programmatic control of Slay the Spire 2. Poll /state for game state, POST /action to take actions.",
            endpoints = new object[]
            {
                new
                {
                    path = "/state",
                    method = "GET",
                    description = "Returns full game state as JSON including current phase, available actions, combat state, map, deck, relics, potions, and more.",
                },
                new
                {
                    path = "/action",
                    method = "POST",
                    description = "Execute a game action. Body is JSON with a 'type' field. Returns {success: true} or {success: false, error: '...'}.",
                },
                new
                {
                    path = "/help",
                    method = "GET",
                    description = "This endpoint. Returns API schema and documentation.",
                },
            },
            phases = new object[]
            {
                new
                {
                    phase = "main_menu",
                    description = "Game is at the main menu, no run active.",
                    actions = new[] { "start_run" },
                },
                new
                {
                    phase = "map_selection",
                    description = "Choosing the next room on the map.",
                    actions = new[] { "select_node" },
                    state_fields = new[] { "map.nodes[]", "map.current_act", "map.current_floor" },
                },
                new
                {
                    phase = "combat_player_turn",
                    description = "Player's turn in combat. Play cards, use potions, or end turn.",
                    actions = new[] { "play_card", "use_potion", "end_turn" },
                    state_fields = new[] { "combat.hand[]", "combat.enemies[]", "combat.energy", "combat.draw_pile_count", "combat.discard_pile[]", "combat.exhaust_pile[]" },
                },
                new
                {
                    phase = "combat_enemy_turn",
                    description = "Enemy is acting. No actions available. Poll until phase changes.",
                    actions = System.Array.Empty<string>(),
                },
                new
                {
                    phase = "awaiting_card_selection",
                    description = "A card or effect requires selecting cards (discard, exhaust, upgrade, etc).",
                    actions = new[] { "select_cards", "cancel_selection" },
                    state_fields = new[] { "card_selection.options[]", "card_selection.min_select", "card_selection.max_select", "card_selection.cancelable" },
                },
                new
                {
                    phase = "event",
                    description = "An event room with interactive choices.",
                    actions = new[] { "choose_option" },
                    state_fields = new[] { "event.title", "event.options[]" },
                },
                new
                {
                    phase = "rest_site",
                    description = "Rest site. Choose to heal, upgrade a card, etc.",
                    actions = new[] { "choose_rest_option" },
                    state_fields = new[] { "rest_site.options[]" },
                },
                new
                {
                    phase = "shop",
                    description = "In the shop. Buy cards, relics, potions, or remove a card.",
                    actions = new[] { "buy_card", "buy_relic", "buy_potion", "remove_card", "leave_shop" },
                    state_fields = new[] { "shop.cards[]", "shop.relics[]", "shop.potions[]", "shop.can_remove_card", "shop.remove_cost" },
                },
                new
                {
                    phase = "game_over",
                    description = "Run ended (victory or defeat).",
                    actions = new[] { "return_to_menu" },
                    state_fields = new[] { "game_over.result", "game_over.floor_reached" },
                },
            },
            actions = new object[]
            {
                new
                {
                    type = "play_card",
                    description = "Play a card from hand.",
                    fields = new object[]
                    {
                        new { name = "card_index", type = "int", required = true, description = "Index into combat.hand[]" },
                        new { name = "target_index", type = "int", required = false, description = "Index into combat.enemies[] (required if card has target_type AnyEnemy)" },
                    },
                },
                new
                {
                    type = "end_turn",
                    description = "End the player's turn.",
                    fields = System.Array.Empty<object>(),
                },
                new
                {
                    type = "select_node",
                    description = "Select a map node to navigate to.",
                    fields = new object[]
                    {
                        new { name = "col", type = "int", required = true, description = "Column of the map node" },
                        new { name = "row", type = "int", required = true, description = "Row of the map node" },
                    },
                },
                new
                {
                    type = "use_potion",
                    description = "Use a potion.",
                    fields = new object[]
                    {
                        new { name = "potion_slot", type = "int", required = true, description = "Potion slot index" },
                        new { name = "target_index", type = "int", required = false, description = "Enemy index if potion requires target" },
                    },
                },
                new
                {
                    type = "select_cards",
                    description = "Select cards for a mid-action choice (discard, exhaust, upgrade, etc).",
                    fields = new object[]
                    {
                        new { name = "indices", type = "int[]", required = true, description = "Array of card indices from card_selection.options[]" },
                    },
                },
                new
                {
                    type = "cancel_selection",
                    description = "Cancel a card selection (only if card_selection.cancelable is true).",
                    fields = System.Array.Empty<object>(),
                },
                new
                {
                    type = "choose_option",
                    description = "Choose an event option.",
                    fields = new object[]
                    {
                        new { name = "option_index", type = "int", required = true, description = "Index into event.options[]" },
                    },
                },
                new
                {
                    type = "choose_rest_option",
                    description = "Choose a rest site option (rest, smith, etc). Smith triggers awaiting_card_selection.",
                    fields = new object[]
                    {
                        new { name = "option_index", type = "int", required = true, description = "Index into rest_site.options[]" },
                    },
                },
                new
                {
                    type = "buy_card",
                    description = "Buy a card from the shop.",
                    fields = new object[]
                    {
                        new { name = "card_index", type = "int", required = true, description = "Index into shop.cards[]" },
                    },
                },
                new
                {
                    type = "buy_relic",
                    description = "Buy a relic from the shop.",
                    fields = new object[]
                    {
                        new { name = "relic_index", type = "int", required = true, description = "Index into shop.relics[]" },
                    },
                },
                new
                {
                    type = "buy_potion",
                    description = "Buy a potion from the shop.",
                    fields = new object[]
                    {
                        new { name = "potion_index", type = "int", required = true, description = "Index into shop.potions[]" },
                    },
                },
                new
                {
                    type = "remove_card",
                    description = "Pay to remove a card at the shop. Triggers awaiting_card_selection with deck cards.",
                    fields = System.Array.Empty<object>(),
                },
                new
                {
                    type = "leave_shop",
                    description = "Leave the shop and return to the map.",
                    fields = System.Array.Empty<object>(),
                },
            },
            usage = new
            {
                example_session = new[]
                {
                    "# Check game state",
                    "curl localhost:9000/state",
                    "",
                    "# Play the first card on the first enemy",
                    "curl -X POST localhost:9000/action -d '{\"type\": \"play_card\", \"card_index\": 0, \"target_index\": 0}'",
                    "",
                    "# End turn",
                    "curl -X POST localhost:9000/action -d '{\"type\": \"end_turn\"}'",
                    "",
                    "# Navigate to a map node",
                    "curl -X POST localhost:9000/action -d '{\"type\": \"select_node\", \"col\": 1, \"row\": 4}'",
                    "",
                    "# Select a card to discard (mid-action choice)",
                    "curl -X POST localhost:9000/action -d '{\"type\": \"select_cards\", \"indices\": [2]}'",
                },
                tips = new[]
                {
                    "Poll /state to detect the current phase before sending actions.",
                    "Only actions listed in available_actions are valid for the current state.",
                    "The game is fully turn-based -- no timing pressure. Poll at whatever rate you want.",
                    "During combat_enemy_turn, poll until phase changes to combat_player_turn.",
                    "When phase is awaiting_card_selection, a card/effect needs you to choose cards before combat resumes.",
                    "Smith at rest sites triggers awaiting_card_selection with upgradeable cards.",
                    "Card removal at shops triggers awaiting_card_selection with deck cards.",
                },
            },
        };

        return JsonSerializer.Serialize(schema, s_jsonOptions);
    }
}
