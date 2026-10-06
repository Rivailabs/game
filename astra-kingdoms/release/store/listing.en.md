# Google Play store listing (English, en-IN and en-US)

<!--
DRAFT for ticket 78. Every "## " section except "Reviewer notes" is store text and is linted by:
  dotnet run --project tools/AstraKingdoms.Release -- store-text-lint \
    --rules release/store/store-lint-rules.json --file release/store/listing.en.md
The text must describe the exact release candidate. Remove a feature line if the candidate does
not contain it (see Reviewer notes). Owner, cultural review and legal review sign-offs go in the
release decision record.
-->

## Title

Astra Kingdoms: Archer Duel

## Short description

Lock a secret bow shot, watch both arrows fly, then cut land from your rival.

## Full description

Astra Kingdoms is a short two-player strategy game of bows and borders.

Each round, both archers secretly choose a weapon, an angle, a power and a dodge. Nobody sees the
other choice until both are locked. Then both shots fly at the same time, and you see exactly why
each arrow hit, missed, clashed or was blocked.

Win the duel and you choose a formation card, then draw a cut with your finger to take land from
your rival's half of a round board. The bigger your winning margin, the more land you may take.
After at most eight rounds, the player with more land wins.

WHAT IS IN THE GAME
- Two-player matches on one shared phone, with private turns and a handover screen.
- Online matches with a friend using a room code.
- Practice matches against computer opponents, and a guided tutorial.
- 20 weapons across five elements: Agni (fire), Vayu (wind), Prithvi (earth), Vidyut (lightning)
  and Varuna (water). Every element is shown by its own symbol and pattern, not only by colour.
- 6 formation cards and 5 terrain types that change each duel.
- Every match starts with equal land and the same weapon rules for both players.

FAIR BY DESIGN
- Optional purchases are cosmetic only: archer outfits, bow skins and banners. They never change
  damage, health, land, timers, matchmaking or which weapons you can use.
- Optional rewarded ads are offered only outside matches. Ads never interrupt a match.

ACCESSIBILITY
- Reduced camera shake and reduced motion, separate music and effects volume, haptics control,
  larger text, and colour-independent element symbols.

LANGUAGE
- English.

A fantasy setting inspired by Indian epic art, architecture and textiles. All archers are original
fictional characters.

Support: see the developer contact on this page. Privacy policy and account deletion: links on
this page.

## Reviewer notes

- Remove "Online matches with a friend using a room code." if the candidate has no online layer.
- Add Hindi and Kannada to LANGUAGE only after fluent-speaker review, the shaping text path and a
  reference-phone check; then add them to `supported_languages` in `store-lint-rules.json` and
  raise `max_counts.language`.
- "Fair by design" lines must match `StoreCatalog` descriptions and the in-game shop text.
