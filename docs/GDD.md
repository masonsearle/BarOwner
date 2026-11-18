# BarOwner — Game Design Document

> Working title. Living document: update it when a decision changes, not after.

## Pitch
A first-person, physics-driven bar management game. You (solo or with up to 3 friends) own a run-down
bar and turn it into the best spot in town. You do every job with your own hands: pour the pints,
haul kegs up from the cellar, chuck out troublemakers, paint the walls, and write the rota. It plays
like Schedule I's hands-on business loop with How to Fish's tactile physics and co-op chaos.

## Design pillars
1. **Hands-on everything.** Anything you can do from a menu, you can also do physically. Kegs are heavy
   objects you carry. Glasses can break. A pint is poured by holding and tilting. Menus are reserved for
   things that are menus in real life too: the rota, the supplier order sheet, the till.
2. **Simple first, deep later.** Day 1 is "pour lager, take money". Complexity unlocks with reputation:
   multiple taps, cocktails, staff, theme nights, pricing strategy. Each system has a *simple layer*
   (always on) and a *deep layer* (opt-in or unlocked).
3. **Chaos is better with friends.** Co-op is a first-class feature, not a mode. Physics mishaps (a
   dropped keg rolling down the stairs) should be funny, not punishing.
4. **Your bar, your identity.** Decor, theme nights, prices and staff all express what kind of bar
   you run, and they attract different customers.

## Core loop
```
PREP (morning)  →  SERVICE (open hours)  →  CLOSE (night)  →  INVEST  → next day
order stock        serve, change kegs,      cash up, clean,    hire, decorate,
set prices         handle customers,        read the day's     upgrade, plan
check rota         clean spills             report             theme nights
```
A day of service lasts about 12–15 real minutes and is tunable.

## Systems (simple layer → deep layer)
| System | Simple layer | Deep layer |
|---|---|---|
| **Serving** | Grab glass, hold under tap, serve, take cash | Head quality from tilt, cocktails with recipes, ice/garnish, glass washing |
| **Kegs & cellar** | Keg runs dry; carry a new one down and connect it | Line cleaning, fobbing/waste, cellar temperature, stock rotation |
| **Customers** | Walk in, queue, order, pay, leave; patience bar | Customer types (students, regulars, sports fans, troublemakers), moods, tabs, tips, reviews, regulars who remember you |
| **Economy & pricing** | Set one price per drink | Demand curves per customer type, supplier deals, deliveries, happy hours |
| **Staff & rota** | Hire one bartender who serves automatically | Applicant stats (speed, charm, honesty), weekly rota grid, wages, fatigue, theft |
| **Theme nights** | Pick a night (quiz/karaoke/live music/sports) | Each shifts the customer mix, demand and mess; needs equipment and decor |
| **Decor & build** | Place furniture from a catalogue | Ambience score, capacity, layout affects flow, paint/flooring |
| **Incidents** | Spills to mop, broken glass | Fights, drunk customers, bouncers, licence inspections |
| **Progression** | Reputation stars unlock items | Expanding the building, licences (late hours, live music), second bar |

## Level layout (greybox)
- **Bar room** (public): front door from the street, tables, bar counter with taps, till, glass rack,
  price chalkboard. A STAFF ONLY door behind the bar leads to the back.
- **Back corridor**: brewery deliveries arrive inside the back door.
- **Manager's office** (off the corridor): staff noticeboard + rota, brewery order board, desk, safe.
- **Cellar** (down a ramp from the corridor; kegs can be rolled down): keg couplers feeding the taps,
  spare kegs, empties cage.

## Co-op model
- Host + up to 3 clients (Unity Netcode for GameObjects, client-hosted).
- Shared bar, shared money, the host owns the save file. Any player can do any job.
- **Authority rules** (see `CLAUDE.md`): the server owns the simulation (customers, economy, time,
  stock). A player owns their own movement and whatever object they are holding.
- Later: Steam friends invites via Unity Relay/Lobby or Steamworks transport.

## Feel targets
- First-person movement: weighty but responsive, with a slight head bob. Sprint and a small hop, but no parkour.
- Held objects follow a springy physics hold point and keep their mass. Heavy items (kegs) slow you down,
  can't be thrown far, and later can be carried by two players.
- Every interaction gets feedback: sound, small particle/UI response, and a camera nudge where it fits.

## Art & audio direction
Stylised low-poly with chunky silhouettes and flat colours plus soft gradients (Schedule I / How to Fish).
Warm tungsten bar lighting against a cool blue street outside. Audio: room tone, glass clinks, crowd murmur
that scales with occupancy, and a jukebox you control.

## Tech
- Unity 6.3 (6000.3.x), URP, Input System (project-wide actions), Netcode for GameObjects 2.x.
- Code under `Assets/_Project/`. Greybox first, art pass later.

## Roadmap
Each milestone ends in a playable build, tested in co-op.

| # | Milestone | Done when |
|---|---|---|
| ✅ M1 | Foundation | Two players can join, walk around a greybox bar, pick up / carry / throw objects, and see each other do it |
| ✅ M2 | Pour a pint | Glass + tap: hold to fill, fill level visible, serve to a test customer, cash goes in the till (synced) |
| ✅ M3 | Customers v1 | Customers spawn, queue at the bar, order, wait with patience, pay or leave angry; day timer |
| ✅ M4 | Kegs & cellar | Kegs deplete per pour; carry new keg from cellar and couple it; deliveries |
| ✅ M5 | Day cycle & economy | Prep → service → close; prices; stock ordering; end-of-day report; save/load |
| ✅ M6 | Staff & rota | Hire, rota grid, staff AI serve/clean, wages |
| **M7** | **Decor & theme nights** | Furniture placement mode, ambience, theme night scheduling |
| M8 | Vertical slice | Art + audio pass on one bar, menus, settings, Steam page ready |

## Decisions
- **Setting: a British pub.** Pints, £ (prices stored in pence), lager/bitter/cider on tap, quiz nights, "last orders".

## Open questions
- Do customers physically drink (glass returns as empties to collect)? Probably yes, since it creates a job.
- Is there failure (bankruptcy) or only slower progress?
