# Ancient Lands POI and traffic coverage

The missing Spirit Essence statues are **`DRAGON_AREA_RUNESTONE`**, defined as `dragonarearunestone` in `cagedobjects.xml`, with visual tile `DRAGON_KPR_FANATICS_RUNESTONE`, a three-second opening channel and `rewarddragonenergy="1"`. They use the caged-object event family, not the reward-building path previously added to the map. That subscription was missing.

`DRAGON_REWARD_SHRINE` is a different object: a reward/ritual building. Twelve positions in the fixed master were already shown, but these do not account for the missing statues. `buffshrines.xml` also contains a `DRAGON_AREA_CURRENCY_GENERATOR` under an explicit prototype comment. It is not evidence that the current statues use `NewShrine`, or that they respawn after the prototype's 1,800 seconds.

## What the map now handles

- **Spirit Essence statues:** `NewCagedObject` (532), ID 0, position 2, visual tile 4, initial cage state 5. The adapter reuses the shared world-position decoder, accepts only the exact runestone definition/tile and only Ancient Lands. A blue lightning symbol and observation ring distinguish it from reward altars. Closed spawn state 1 is labelled as an observation; opened spawn state 2 is retained as depleted evidence and hidden. Missing/unknown state stays unknown. Generic Mists wisp cages are excluded.
- **State changes:** `CagedObjectStateUpdated` (533) correlates its ID 0 with a known statue. The update's state-field schema remains unverified, so receiving one invalidates the previous availability claim without interpreting an arbitrary field as opened/closed. Reordered/foreign packets cannot overwrite newer evidence; pending Join, re-entry memory, Leave and manual-clear rules still apply. A last-known position does not establish that a statue is still usable.
- **Island keys:** `LocalQuestRewardSpawnLocation` positions now appear as possible key rewards. The four Ancient Lands quest types in `localquests.xml` explicitly trigger `SPAWN_DRAGON_AREA_ISLAND_KEY`. The generated union contains 468 alternative key spots, not 468 live keys. The previous exporter omitted these positions.
- **Ancient Ghostlights:** the six T6/T7 solo/veteran/elite `*_MOB_DA_KEEPER_FANATIC_WISP_PURPLE_*TRASH` definitions are now included in Dynamic POI. `dragonarea.xml` explicitly lists their Spirit Essence payout; the catalog previously excluded them because they are categorized as ordinary trash. They use the existing `NewMob`/movement/Leave/death pipeline. Ordinary non-POI trash remains excluded.

Turn on **Dynamic POI** to see observed statues and Ghostlights; **Points of interest** shows possible key locations. The current master/section templates do not place the active runestone tile at fixed positions. No speculative global statue coordinates were added. `_DRAGON_RUNESTONE_01`–`03` compounds exist in the tile catalog, which establishes their contents, not every possible world placement.

## Coverage and remaining gaps

“Diagnostics” means a passive subscription makes the event available to the existing opt-in handled-packet log. It does **not** decode an availability, position or timer for the map. Logging remains off until the user enables diagnostics.

| POI / information | Relevant event family | Current coverage and limit |
| --- | --- | --- |
| Spirit Essence statue | `NewCagedObject` 532 / `CagedObjectStateUpdated` 533 | Position/type and initial closed/opened observation; later availability stays unknown pending schema verification. |
| Reward / ritual altar | `NewBuilding` 45; building action events | Twelve template positions; existing building-position observation. Action completion alone does not identify a nearby player's reward or the altar's global availability. |
| Drakes, Fire Drake, bosses, Ghostlights | `NewMob` 123, `Move` 3, `Leave` 1, death events | Existing mob identity/position/movement/death pipeline; six Ghostlight definitions added. |
| Chests and coffers | `NewLootChest` 392, `UpdateLootChest` 393, `LootChestOpened` 394; treasure/reward events | Candidate positions and observed chests; known empty state removes them. Locked/eligible/reward details vary by chest family. |
| Island keys on the ground / carried keys | `NewCarriableObject` 486, `CarriedObjectUpdate` 488, `PickupCarriableObjectFinished` 491, `DragonAreaTeleporterKeyUpdate` 698 | Static possible quest-reward spots added. Dynamic key/carrier payloads are subscribed for diagnostics; field layout and ownership semantics still need verification. No guessed key is drawn. |
| Leygates and active links | `NewTeleporterNode` 677; node state 678, full connections 679, changed connection 680 | Existing observed endpoint adapter; state/link events now available to diagnostics. No guessed active lines, destinations or timers. |
| Exits and opening conditions | `NewDragonAreaExit` 695 / `DragonAreaExitStatusUpdate` 696 | Candidate/observed positions; status update logged for diagnosis, not interpreted. |
| Island quests / central encounter | `LocalQuestInfos` 539 through progress 543; Dragon Area quest state 689/691, join info 690 | Quest-reward spots added; listed events available to diagnostics. No inferred completion from killing an arbitrary nearby mob. |
| Character Spirit Essence | `UpdateDragonEnergy` 697 | Available to diagnostics. A changed character counter does not locate its source statue or prove which POI was used. |
| Fishing spots / treasure fishing | `NewFishingZoneObject` 360 | Present in templates, omitted as a map POI layer. Spawn event now available to diagnostics; actual type/charges payload not decoded. Ordinary resource nodes/mobs already have their own layers. |
| Interactive stone doors | `NewTileSwitch` 385 / `NewTileSwitchTrigger` 623 | Fixed door placements exist in the master but are not exported as a dedicated POI. Events now available to diagnostics. A trigger event is not sufficient to label a door open. |
| Lore | `NewLoreSnippetObject` 673 | Six candidate spots and existing observation adapter; collected state is not inferred. |
| Ancient Lands entrance portals outside the instance | `NewDragonAreaEntrance` 694 | Separate open-world feature, not included in this Ancient Lands map change. |

The client can display only objects and state updates actually sent to the observing client. Nearby traffic does not provide a reliable complete live census of every island. Static candidates cover possible template positions; remembered observations can become stale.

## Evidence and validation

Primary game data is pinned to [ao-data/ao-bin-dumps at 0483dd97f1581dfaf02eed42f25f5ea2a3cb75e5](https://github.com/ao-data/ao-bin-dumps/tree/0483dd97f1581dfaf02eed42f25f5ea2a3cb75e5): `cagedobjects.xml`, `buildings.xml`, `buffshrines.xml`, `localquests.xml`, `carriables.xml`, `dragonarea.xml`, `mobs.xml`, `cluster/tiles.xml` and the supported master/section templates. Event numbers come from the pinned shared core's enum.

The caged-object envelope has an existing [public Wisp cage capture fixture](https://github.com/Nouuu/Albion-Online-OpenRadar/blob/2786827386ee31d37d5a6068b965f42d589fe946/web/scripts/__fixtures__/ws/wispcage/spawn.json) with positions at 2, tiles at 4 and initial states 1/2 at 5. This supports the common object envelope, **not** a claim that a live Dragon runestone capture was tested. Only the existing shared value decoder is reused; no new Photon parser or shared-core pin change was made.

To verify the remaining schemas, enable **Settings › About › Diagnostics mode**, enter Ancient Lands, approach a statue, activate it, observe a key reward and a Leygate transition, then disable diagnostics. The new subscriptions make these previously unhandled state packets available in that opt-in log. A runestone spawn/state pair from a current Windows session is still needed to verify the Dragon-specific payload and close the availability gap.

Validation: Debug solution build completes with zero errors; all 117 PoW tests, scoped dotnet formatting and UI-token checks pass. The external native fixture passes 67 checks, including dispatch through the production registered subscribers, correct field-2 remapping, ordinary-cage exclusion, closed/open/unknown states, reordered/foreign/pre-Join updates, clearing, the six Ghostlight roles, possible key counts and diagnostic handler coverage. Both generators reproduce byte-identical output. Updated screenshots use synthetic observations, not a recorded gameplay session.
