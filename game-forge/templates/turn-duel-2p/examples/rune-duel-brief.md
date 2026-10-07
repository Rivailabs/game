# Rune Duel: game brief

A small, turn-based duel for two players sharing one Android phone, or one player against a bot.
This is the second sample game for the `turn-duel-2p` template (Astra Kingdoms is the first).

## Players

- Exactly two players take part: two humans sharing one phone, or one human against a bot opponent.
- The bot sees only its own hand and the public match state.

## Match structure

- A match is at most 10 rounds.
- In each round both players secretly choose one rune, then both choices are revealed together.
- The first player alternates every round so neither seat always reveals first on screen.

## Starting state

- Each player starts with 20 health.
- Each player starts with a hand of 3 runes drawn from their own shuffled bag.
- A bag holds 12 runes: four of each rune type.

## Runes

- There are three rune types: Fire, Water, Earth.
- Fire beats Earth: the Earth player loses 3 health.
- Earth beats Water: the Water player loses 3 health.
- Water beats Fire: the Fire player loses 3 health.
- When both players reveal the same rune type, each player loses 1 health.
- After the reveal, each player draws one rune from their bag; an empty bag draws nothing.

## Actions

- On a turn a player must choose exactly one rune from their hand.
- A player cannot choose a rune that is not in their hand; the choice is rejected and the player chooses again.

## Turn timer

- Each player has 20 seconds to choose a rune.
- When the timer expires, the leftmost rune in that player's hand is chosen automatically.

## Randomness

- Bags are shuffled with a seeded deterministic generator; the match seed and the ordered choices reproduce the whole match.

## Winning and draws

- A player wins when the opponent's health reaches 0 or below and their own health is above 0.
- If both players reach 0 or below in the same round, the match is a draw.
- After round 10 the player with more health wins; equal health is a draw.

## Rematch

- After the results screen either player can request a rematch; the rematch uses the same settings, a new seed and swaps the first player.

## Replay

- Every match can be replayed from its seed and ordered choices, and the replay ends in the same final state hash.

## Screens

- The menu screen offers: play on one phone, practice against the bot, settings.
- During a match the screen shows both health values, the round number and the current player's hand.
- The results screen shows the winner (or draw), the final health of both players and a rematch button.
- Every button has a touch target of at least 48 by 48 dp.

## Audio

- Use short licensed sound effects for reveal, hit, win and draw; no music in the first release.
- Players can turn sound effects off in settings.

## Languages

- English is the reference language; Hindi is added after fluent-speaker review.
- Every player-facing string comes from the localization table; no text is hard-coded in screens.

## Balance

- No rune type should win more than 55% of decisive reveals in bot-versus-bot screening.

## Release

- Intended release countries: India.
- The first release is an Android build tested on the reference phone.
