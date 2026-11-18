# Pulse of Time

Full DualSense support for *The Legend of Zelda: Ocarina of Time* (Switch 2 remake) running in the [Lichii](https://github.com/zortos293/lichii) emulator or its fork Fritchii.

Pulse of Time runs next to the emulator. It reads what the game is doing and plays it on your DualSense: on the voice-coil motors in both grips, on L2 and R2, on the lightbar and on the player LEDs. Nothing in the game or the emulator gets patched.

**[Download the latest release](../../releases/latest)**: unzip it, start the game, double-click `PulseOfTime.exe`.

## How much is in there

- **160 hand-made haptic effects and 27 continuous textures.** None of them is a recorded rumble file. Each one is synthesized live for the two motors.
- **170 of the game's 282 action states** have their own reaction: swimming, climbing, parrying, wall jumps, getting grabbed, falling, riding and many more.
- **Every ocarina song, boss intro and warp** is recognised from the game's event system.
- **Over 30 story, minigame and dungeon moments** come from the save data: water levels, time travel, minigame timers and scores, the castle escape, bottles, masks, horse races.
- **The game's own sound** is turned into touch on top: explosions, stomps and footsteps come through from the side they happen on.
- **Four points of contact.** L2 and R2 are the front, the two grips are the back. A hit from behind lands in your palms, a blow from the front right shakes R2.

## Moments worth playing for

- **Pulling the Master Sword.** Everything goes quiet. A heartbeat slows down and fades away, and the lightbar turns white. When you wake up seven years later, every step and landing feels heavier. As a child, everything is lighter and quicker.
- **The bow.** R2 tightens as you draw and clicks at full draw. You feel the string creak under your finger, the arrow leaves with a twang, and the player LEDs show the draw. Shoot your last arrow and R2 jams.
- **Sword fights.** Swings sweep across the controller from one hand to the other. Every hit lands from the enemy's side, and the third hit of a combo is a heavy finisher. Charging a spin attack makes both triggers tremble harder and harder. Each enemy type dies differently: Stalfos rattle apart, Iron Knuckles clang to the floor, Poes fade out across your hands.
- **Enemies telegraph.** When something winds up an attack, you feel the air being pulled in from its side and your shield hand braces on L2. Arrows and fireballs whine in from where they were shot.
- **ReDeads.** Their scream locks both triggers, turns the light grey and puts a cold tremble in your hands until you can move again. When a Like Like grabs you, every button you mash jerks against its grip.
- **Swimming and diving.** Every stroke washes from one hand to the other. Underwater the pressure builds, and without the Zora Tunic your breath runs down on the player LEDs and the last bit blinks.
- **Climbing.** One grip per hand movement, left and right in turn, matched to Link's actual climbing speed.
- **Epona.** The hind hooves land in your palms and the fore hooves in L2 and R2, in a proper canter rhythm. In turns, her weight shifts to the outside.
- **The Water Temple.** When the water rises, it climbs from your palms up into your fingers. When it drains, it runs back down.
- **Rain.** Single drops land anywhere on both hands and tap the triggers now and then. Under a roof they turn into a dull drumming. Running through puddles splashes on alternating feet.
- **Ganon's castle collapsing.** The ground keeps shaking, rubble hits from random directions and the mute LED stays on as an alarm.
- **Gerudo Fortress.** You feel the nearest guard as a heartbeat from her direction, and it gets faster the closer she gets.
- **Minigames.** A starting shot opens the game and the last ten seconds tick down harder. The last three are heartbeats. Target hits pop, misses go dull, a new fishing record lands with a fanfare. Angry Cuccos peck at you from every side.
- **Ocarina.** Every note sits in its own place on the controller: low D in the palms, F in L2, A in R2, high D in both triggers. Every song has its own colour and feel. The Song of Storms brings real rain to your hands.
- **The lightbar** shows the place you're in, the time of day, fire, ice and lightning, danger nearby and the colour of every boss. The **player LEDs** show your hearts, the bow draw, spin charge, the locked-on enemy's life, the boss's life and the minigame clock. The **mute LED** flashes when you are down to one heart.

## What you need

- Windows 10 or 11
- A DualSense **connected by USB cable**. The haptic motors only get a signal over USB. Over Bluetooth, the triggers and lights still work.
- Lichii (or its fork Fritchii) running *Ocarina of Time* for Switch 2, **version 1.1.1** (build `E0F3CCFA`). Other versions use different memory addresses and won't be detected.

That's all. The release is a single exe, no .NET or anything else to install.

## Setup

You only need to do this once:

1. Plug the DualSense in with a USB cable.
2. Press `Win + R`, type `mmsys.cpl` and press Enter.
3. On the **Playback** tab, select **Speakers (DualSense Wireless Controller)** and click **Configure**.
4. Pick **Quadraphonic** and click **Next**. The haptic motors are channels 3 and 4 of that device, and Windows only sends them when it's set to four channels.
5. On the **full-range speakers** page, tick **Front left and right** first. Then **Surround speakers** can be ticked too: tick it. The motors run on low frequencies, and some drivers filter those out of channels that aren't marked as full-range, which makes the haptics weaker.
6. Click **Next** and **Finish**.

## Playing

1. Start Lichii or Fritchii and load your save.
2. Start `PulseOfTime.exe`. If Windows shows "Windows protected your PC", click **More info**, then **Run anyway**.
3. After about three seconds a self-test is printed. Every line should say `[ok]`.
4. Play. Closing the window puts the controller back to normal.

The touchpad works while you play:

- **Click** to switch between gentle, normal and strong.
- **Click, hold and slide up or down** for finer steps. The player LEDs show the level.
- **Swipe up without clicking** for Navi's compass: a pulse points towards the nearest treasure (green light) or enemy (yellow).

## Tuning

- `Calibrate.bat` plays every effect one by one:
  - D-pad up and down make the effect stronger or weaker. Each change is saved at once.
  - Circle plays the effect again, Cross goes to the next one, Square goes back, Options stops.
- `tuning.json` sits next to the exe and holds every value. Changes apply while you play.
  - `master` is the overall strength.
  - Every effect (`fx.*`) and texture (`loop.*`) has its own factor.
  - `captions: 1` turns on haptic captions: every enemy nearby pings, including those in front of you.
  - `aim.fov` is the camera's field of view while aiming. Change it only if aiming isn't detected (default 40).
- `Demo.bat` plays everything without the game.
- `PulseOfTime.exe replay` plays your last session's effects again, from `pulse-fx.log`.

## Troubleshooting

If a self-test line shows `[!!]`, what to do is printed right next to it:

- `DualSense over USB`: use a cable.
- `haptics device (4 channels)`: do the Quadraphonic setup above.
- `game build 1.1.1`: other versions aren't supported yet.
- `Link found`: load a save.

Button effects assume the emulator's default layout: Square is attack, Circle is A, Cross is B and Triangle is X.

`pulse-events.log` next to the exe lists what the game reported (events, enemies, animations, errors). Attach it if you open an issue.

## How it works

Pulse of Time only ever reads from the game's memory inside the emulator, it never writes to it. The memory it reads:

- Link's action state machine, his animation and the event manager.
- GameData, the game's save fields.
- The actor list, for enemies, objects and bosses.
- The camera, for left/right and front/back.

It also captures the game's audio through Windows process loopback and turns sudden sounds into touch. The motors get their signal as audio on the controller's USB sound device. Triggers and lights are sent as HID output reports.

## Credits

- Offsets for build 1.1.1: [Kabosune/oot-remake-modding](https://github.com/Kabosune/oot-remake-modding)
- GameData layout and field names: [PracticeROM/sloperina](https://github.com/PracticeROM/sloperina)
- Lichii: [zortos293/lichii](https://github.com/zortos293/lichii)
- Audio: [NAudio](https://github.com/naudio/NAudio)
- Trigger effect formats: Nielk1's DualSense trigger research

## License

GPL-3.0-or-later, see [LICENSE](LICENSE).

This is a fan project, not affiliated with Nintendo or Sony. *The Legend of Zelda* and *Ocarina of Time* are trademarks of Nintendo; *DualSense* and *PlayStation* are trademarks of Sony Interactive Entertainment. No game files are included. You need your own copy of the game.
