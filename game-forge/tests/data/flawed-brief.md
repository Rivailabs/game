# Glyph Clash

A duel game for a single phone.

## Players

- Two players take part.
- Real-time dodging happens between turns.

## Match structure

- A match is at most 8 rounds.
- After round 12 the player with more health wins.

## Starting state

- Each player starts with 20 health.
- Players begin the match with 25 health.

## Glyphs

- There are four glyph types: Sun, Moon, Star.
- Sun beats Moon: the Moon player loses 2 health.
- Moon beats Star: the Star player loses 2 health.
- Bonus damage is TBD.

## Actions

- A player can skip a turn.
- A player cannot skip a turn.
- On a turn each player chooses one glyph.

## Winning

- A player wins when the opponent has 0 health.
