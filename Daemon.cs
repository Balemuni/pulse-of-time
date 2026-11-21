using System.Diagnostics;

namespace PulseOfTime;

/// Reads OoT's state and the controller ~60 times a second and drives everything the DualSense has, so
/// that triggers, voice coils and lights all tell the same story.
///
/// Fighting   your sword cuts the air across your hands (Square); a hit lands as a crisp impact from the
///            enemy's side, R2 ticks under your finger, the lightbar flashes; each kind of enemy dies its
///            own way (pop, crunch, bones, armour, squish, ghost, heavy, mid-boss, boss); hold Square and
///            the spin attack charges in your hands, both triggers tremble harder, the player LEDs fill;
///            let go and the spin goes round. Raise the shield (L2) near enemies and a blow on it rings
///            in your left hand and kicks L2 back. Lock-on clicks.
/// Moving     jump push-off, landing by fall speed, rolling tumbles round the hands, climbing grips
///            alternate hands, Epona gallops in both triggers and kicks off when she spurs.
/// Weapons    bow / slingshot as the controller's bow effect, the string creaks with your real R2 pull,
///            the player LEDs show the draw; a shot twangs, an empty
///            quiver clicks dry.
/// Life       hits jolt, healing sparkles, low life beats in L2 + coils + red lightbar, the mute LED
///            pulses at one heart; a fairy brings you back with sparkles; death by fire / water /
///            lightning feels different.
/// Treasure   chests swell and burst, songs rise, keys clink, medallions and
///            stones rumble deep, heart containers beat three times; rupees tick by amount, magic shimmers.
/// Places     the lightbar takes the place's mood (torchlight in fire, waves in water, Jabu-Jabu's pulse,
///            fireflies in the forest, shadow violet, desert gold, ice sparkle, Ganon's red), with rare quiet
///            touches of its ambience; it turns amber near enemies and crimson near a boss.
/// Triggers   firm: L2 carries the shield's weight, R2 has a hard click point; iron boots make both
///            heavier, hover boots lighter. Pause, loading and death set them free.
static class Daemon
{
    const float AimFov = 40f;
    const int AimHoldMs = 120;

    static readonly uint HPause = GameData.Hash("Sequence_IsPause"), HPauseReq = GameData.Hash("Sequence_GamePauseRequestCount"),
        HPrePause = GameData.Hash("Sequence_IsPreGamePause"), HChild = GameData.Hash("Sequence_IsChildhood"),
        HLoading = GameData.Hash("Sequence_IsLoadingBanc"), HMap = GameData.Hash("Sequence_CurrentBanc"),
        HArrows = GameData.Hash("PouchData.Item_Arrow_Normal.StockNum"), HSeeds = GameData.Hash("PouchData.Item_DekuSeed.StockNum"),
        HRupee = GameData.Hash("PlayerStatus.CurrentRupee"), HMp = GameData.Hash("PlayerStatus.MP"), HMaxLife = GameData.Hash("PlayerStatus.MaxLife"),
        HShield = GameData.Hash("PlayerStatus.EquipShield"), HBoot = GameData.Hash("PlayerStatus.EquipBoot"),
        HArmor = GameData.Hash("PlayerStatus.EquipArmor"), HWeapon = GameData.Hash("PlayerStatus.EquipWeapon"), HDeath = GameData.Hash("PlayerDeadReason"),
        HNuts = GameData.Hash("PouchData.Item_DekuNut.StockNum"), HBombs = GameData.Hash("PouchData.Item_Bomb.StockNum"),
        HChus = GameData.Hash("PouchData.Item_Bombchu.StockNum"), HSticks = GameData.Hash("PouchData.Item_DekuStick.StockNum"),
        HWalletM = GameData.Hash("PouchData.Item_Wallet_Medium.StockNum"), HWalletL = GameData.Hash("PouchData.Item_Wallet_Large.StockNum"),
        HMpMax = GameData.Hash("PlayerStatus.MPMax");
    static readonly (string name, uint hash)[] ItemFlags = GameNames.Items.Select(n => (n, GameData.Hash("IsGet." + n))).ToArray();
    static readonly (string name, uint hash)[] KillCounts = GameNames.Enemies.Select(n => (n, GameData.Hash("DefeatedEnemyNum." + n))).ToArray();

    static readonly byte[] Off = Effects.Off();

    // controller buttons (DualSense.Buttons): Switch Y = Square (attack), A = Circle, B = Cross, X = Triangle (jump), L/R = L1/R1
    const int BSquare = 1, BCross = 2, BCircle = 4, BL1 = 16, BR1 = 32;

    /// Left stick as one of 8 directions (0 up, clockwise), or -1 near the centre.
    static int StickDir(int x, int y)
    {
        float dx = (x - 128) / 128f, dy = (y - 128) / 128f;
        if (dx * dx + dy * dy < 0.5f * 0.5f) return -1;
        double a = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0 = up, 90 = right
        return (int)Math.Round((a + 360) % 360 / 45) % 8;
    }

    public static int Run(string[] args)
    {
        // one copy only: two would fight over the triggers and double every vibration
        using var single = new Mutex(true, "PulseOfTime.Daemon", out bool firstCopy);
        if (!firstCopy) { Console.WriteLine("Already running (another window has it). Close that one first."); Thread.Sleep(5000); return 1; }
        bool debug = args.Contains("--debug"), haptics = !args.Contains("--no-haptics"), lightsOn = !args.Contains("--no-lights");
        float Opt(string name, float def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture) : def; }
        float gain = Opt("--gain", 1f), sound = Opt("--sound", 1f), music = Opt("--music", 1f), ambient = Opt("--ambient", 0.6f);

        Haptics? hx = null; int hxPid = 0;
        DualSense? ds = null; GuestMemory? gm = null; Oot? oot = null; GameData? gd = null; Combat? cb = null;
        var scene = new Scene(); var act = new Actions(); var mom = new Moments();
        // every effect is written down, so a session can be felt again without the game ("PulseOfTime replay")
        var fxLog = TryOpen(Path.Combine(AppContext.BaseDirectory, "pulse-fx.log"));
        var fxClock = Stopwatch.StartNew();
        if (fxLog != null) Haptics.Recorder = (f, st, n, p) => fxLog.WriteLine(FormattableString.Invariant($"{fxClock.ElapsedMilliseconds} {f} {st:F2} {n} {p:F2}"));
        long touchFree = -1, naviUntil = 0; int touchFreeY = 0; float aimAcc = 0; bool fullDraw = false, fullCharge = false, touchFired = false;
        (float R, float G, float B) naviColor = (80, 140, 255);
        (float R, float G, float B) lightNow = (0, 0, 0); int ledSrc = -1; long ledSweepUntil = 0;
        var verbs = new int[5]; var world = new Oot.WorldState(-1, -1, false, false, -1);
        byte[]? sentR = null, sentL = null; DualSense.Lights? sentLights = null;
        var clock = Stopwatch.StartNew();
        long nextRetry = 0, lastSend = 0, nextLevel = 0, nextStrings = 0, nextFlags = 0, nextKills = 0, nextWorld = 0;
        long aimSince = -1, aimEnd = -1, nextBeat = 0, rideSince = -1, airMinVy = 0;
        long flashUntil = 0, deadUntil = 0, deadAt = -1, jolt = 0, rTick = 0, lKick = 0, gallopSpur = 0, celebrateUntil = 0;
        long squareDown = -1, lastSwing = 0;
        float minVy = 0, prevSpeed = 0, charge = 0;
        (byte r, byte g, byte b) flash = default; int flashMs = 1;
        int lastLife = -1, lastAmmo = -1, lastRupee = -1, lastMp = -1, lastMaxLife = -1, swingDir = 1;
        int prevButtons = 0;
        int rideVerb = Verbs.Id("Ride");
        var quad = new Quad(); var events = new Events();
        float quakeLvl = 0; long nextEvent = 0;
        long nextBossFx = 0, lastShieldCharge = 0; int shieldCharge = 0; bool volvagiaUnder = false; (float, float, float)? bossLight = null; float lowImpNow = 0;
        long lastFrame = 0, selfTestAt = 0; float dtF = 0.016f; string lastAnim = "";
        bool wasPaused = false; int prevDir = -1, prevL2b = 0, prevR2b = 0, lastSel = -1, lastHour = -1; long navRepeat = 0;
        string prevShield = "", prevBoot = "", prevArmor = "", prevWeapon = "";
        long nextSonar = 0, heatSince = -1, nextHeat = 0, nextDrop = 0, lastHoofBeat = -1, nextSearchPing = 0; double stepPh = 0;
        long braceUntil = 0, nextTelegraph = 0, lensUntil = 0, lastMpDrop = 0, fuseUntil = 0, ledShowUntil = 0; double ironPh = 0; float ledShow = 0;
        long atkAt = -10000, ledChipUntil = 0, nextAudioTick = 0, nextSparkle = 0, touchStart = -1, ledLevelUntil = 0;
        float atkSide = 0, atkFront = 1; int comboN = 0, touchY0 = 0, touchSteps = 0; bool touchMoved = false;
        int lastNuts = -1, lastBombs = -1, lastChus = -1, lastSticks = -1;
        bool lastChild = false, wasAiming = false, dryClicked = false, flagsReady = false, killsReady = false, wasAimingNock = false;
        var flagState = new Dictionary<uint, bool>(); var killState = new Dictionary<uint, int>();
        var recentKills = new Dictionary<string, long>();
        string shield = "", boot = "", armor = "", weapon = "", lastDbg = "", lastErr = ""; bool saidPad = false, saidGame = false; string touchKey = "master";
        bool quit = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit = true; };
        Console.WriteLine("Pulse of Time is running. Close this window or press Ctrl+C to stop (the controller goes back to normal).");

        while (!quit)
        {
            try
            {
                long now = clock.ElapsedMilliseconds;
                dtF = Math.Clamp((now - lastFrame) / 1000f, 0.001f, 0.1f); lastFrame = now;
                void Flash(byte fr, byte fg, byte fb, int ms) { flash = (fr, fg, fb); flashMs = ms; flashUntil = now + ms; }

                // ---- connections ----
                if (now >= nextRetry)
                {
                    if (ds == null && (ds = DualSense.Open()) != null)
                    {
                        ds.StartInput();
                        Console.WriteLine($"DualSense connected ({(ds.Bluetooth ? "Bluetooth" : "USB")}).");
                        sentR = sentL = null; sentLights = null;
                    }
                    var lichii = GuestMemory.FindEmulator();
                    if (hx != null && hx.Stopped) { Console.WriteLine("Haptics device lost, reconnecting."); hx.Dispose(); hx = null; hxPid = 0; }
                    if (haptics && lichii != null && (hx == null || hxPid != lichii.Id))
                    {
                        hx?.Dispose();
                        hx = Haptics.Start(lichii.Id, out var err);
                        hxPid = hx != null ? lichii.Id : 0;
                        if (hx != null) { hx.Gain = gain; hx.SoundGain = sound; hx.MusicSuppress = music; Console.WriteLine($"Haptics on: {hx.Device}"); }
                        else Console.WriteLine($"Haptics off: {err}");
                    }
                    if (gm == null || Process.GetProcesses().All(p => p.Id != gm.Pid) || gm.Arena == 0 || (oot != null && !oot.StillThere()))
                    {
                        cb?.Dispose(); gm?.Dispose(); gm = GuestMemory.Attach(); oot = null; gd = null; cb = null;
                    }
                    if (gm != null && gm.Arena != 0 && oot == null)
                    {
                        var o = new Oot(gm);
                        if (o.FindMain())
                        {
                            oot = o; gd = new GameData(gm, o.Main); cb = new Combat(gm, o.Main);
                            flagsReady = killsReady = false; selfTestAt = now + 3000;
                            Console.WriteLine($"OoT found (main at 0x{o.Main:X}).");
                        }
                        else { gm.Dispose(); gm = null; }
                    }
                    nextRetry = now + 2000;
                    // say once what is still missing, so a first start never looks like nothing happens
                    if (ds == null && !saidPad) { Console.WriteLine("Waiting for the DualSense (connect it with a USB cable)..."); saidPad = true; }
                    if (oot == null && !saidGame && now > 2500) { Console.WriteLine("Waiting for Lichii or Fritchii with Ocarina of Time 1.1.1 (load a save)..."); saidGame = true; }
                }

                var s = oot?.Read() ?? default;

                // self-test once after the game is found: every source this build relies on, in one line each
                if (selfTestAt > 0 && now >= selfTestAt && oot != null && gd != null)
                {
                    selfTestAt = 0;
                    void Check(string what, bool ok, string fix) => Console.WriteLine($"  [{(ok ? "ok" : "!!")}] {what}{(ok ? "" : "  -> " + fix)}");
                    Console.WriteLine("Self-test:");
                    Check("DualSense over USB", ds != null && !ds.Bluetooth, "connect it with a USB cable (voice-coil haptics need USB)");
                    Check("haptics device (4 channels)", hx != null, "Sound settings: DualSense speaker as Quadraphonic");
                    Check("game build 1.1.1 (E0F3CCFA)", oot.StillThere(), "other game version: offsets need updating");
                    Check("Link found", s.InGame, "load a save");
                    Check("action verbs", Verbs.Name(1) == "Walk" && oot.Verb(0) >= 0, "verb table moved");
                    Check("GameData", gd.Count(GameData.Kind.Int) > 500 && gd.Count(GameData.Kind.Bool) > 5000, "GameData manager moved");
                    Check("camera", oot.CameraEye() != null && s.FovDeg > 0, "camera offsets moved");
                    Check("event manager", oot.EventName() != null, "EventMgr offset moved");
                    Check("trigger strength scaling", Effects.Scale(Effects.Feedback(0, 8), 0.75f).SequenceEqual(Effects.Feedback(0, 6)) && Effects.Scale(Effects.Weapon(2, 4, 8), 0.5f).SequenceEqual(Effects.Weapon(2, 4, 4)), "Effects.Scale is broken");
                    Check("tuning.json", File.Exists(Path.Combine(AppContext.BaseDirectory, "tuning.json")), "will be written on first use");
                    Check("v2 fields (minigames, time travel, water)", gd.Has(GameData.Kind.Int, GameData.Hash("MiniGame_Score")) && gd.Has(GameData.Kind.Bool, GameData.Hash("MiniGame_IsTimerCounting"))
                          && gd.Has(GameData.Kind.Int, GameData.Hash("WaterTemple_Room00_WaterLevel")), "GameData names changed");
                    Check("bottles / sequences (enums)", gd.Enum(GameData.Hash("PlayerStatus.AssignableItem.BottleInfo.BottleA.BottleItem")) != null && gd.Enum(0x4969f026) != null, "enum table moved");
                }
                if (cb != null && oot != null) cb.Link = oot.Link;
                for (int i = 0; i < 5; i++) verbs[i] = oot != null && s.InGame ? oot.Verb(i) : -1;
                if (oot != null && s.InGame && now >= nextWorld) { world = oot.World(); nextWorld = now + 250; }

                byte[] r = Off, l = Off;
                DualSense.Lights? lights = null;
                float pull = ds != null ? ds.R2 / 255f : -1;
                int buttons = ds?.Buttons ?? 0;
                if (hx != null) hx.Pull = pull;
                bool loading = gd?.Bool(HLoading) == true;

                if (s.InGame && gd != null && !loading)
                {
                    bool paused = gd.Bool(HPause) == true || gd.Bool(HPrePause) == true || gd.Int(HPauseReq) > 0;
                    bool child = gd.Bool(HChild) ?? false;
                    int ammo = (child ? gd.Int(HSeeds) : gd.Int(HArrows)) ?? 0;
                    int rupee = gd.Int(HRupee) ?? -1, mp = gd.Int(HMp) ?? -1, maxLife = gd.Int(HMaxLife) ?? -1;
                    if (now >= nextStrings)
                    {
                        shield = gd.Str(HShield) ?? ""; boot = gd.Str(HBoot) ?? ""; armor = gd.Str(HArmor) ?? ""; weapon = gd.Str(HWeapon) ?? "";
                        scene.SetMap(gd.Str(HMap) ?? "", now);
                        nextStrings = now + 500;
                    }
                    bool iron = boot.Contains("HeavyBoot"), hover = boot.Contains("HoverBoot"), sword = weapon.Length > 0;
                    if (child != lastChild) { lastAmmo = -1; lastChild = child; }
                    bool pressed(int b) => (buttons & b) != 0 && (prevButtons & b) == 0;
                    bool released(int b) => (buttons & b) == 0 && (prevButtons & b) != 0;

                    // ---- enemies: our hits, their deaths ----
                    var hits = !paused && cb != null ? cb.Poll() : new List<Combat.Hit>();
                    foreach (var h in hits)
                    {
                        // the blade lands where the enemy is: in front it shakes the fingertips on that side, behind the palms
                        float st = 0.6f + Math.Min(1f, h.Damage / (float)Math.Max(1, h.Max)) * 0.9f;
                        var (palm, pan, tl, tr) = Quad.Place(h.Pan, h.Front, Math.Min(1f, st * 0.8f));
                        hx?.Play(Fx.EnemyHit, Math.Max(palm, 0.5f) * st, h.Boss ? 2 : 1, pan * 0.85f);
                        quad.Pulse(now, Math.Max(tl, 2), Math.Max(tr, 3), h.Boss ? 35 : 55, h.Boss ? 160 : 90);
                        Flash(255, 245, 220, 110); hx?.Duck(220);
                        ledChipUntil = now + 90; // the target's life bar loses a chip
                        if (comboN >= 3) { hx?.Play(Fx.Finisher, 1, 1, h.Pan * 0.6f); quad.Pulse(now, 6, 6, 40, 120); }
                        if (h.Killed) { KillFx(h.Name, h.Pan); recentKills[h.Name] = now; }
                    }
                    // what the enemies are doing (they run on the same action verbs as Link)
                    if (cb != null)
                        foreach (var a in cb.Acts)
                        {
                            var (palm, apan, atl, atr) = Quad.Place(a.Pan, a.Front, 0.8f);
                            string v = a.Verb;
                            bool windup = v.Contains("Attack") || v is "Charge" or "PrepareWeapon" or "SpecialAttackPrepare" or "PreLaunch" or "LandCharge" or "Inhale";
                            if (windup && a.Distance < 8 && now >= nextTelegraph)
                            {   // it winds up: air drawn in from its side, the shield hand tenses
                                hx?.Play(Fx.Telegraph, a.Boss ? 1.2f : 0.9f, 1, apan);
                                atkSide = a.Pan; atkFront = a.Front; atkAt = now; // a blow soon after comes from here
                                quad.Pulse(now, atl * 0.5f, atr * 0.5f, 30, 220, 0, false, true);
                                braceUntil = now + 450; nextTelegraph = now + 350;
                                Flash(255, 170, 40, 160);
                            }
                            else if (v is "Notice" or "Discover" or "Surprise" or "Provoke" or "LookAtTerror" && a.Distance < 12) hx?.Play(Fx.Alert, 0.8f, 1, apan);
                            else if (v is "Appear" or "JumpOut" or "Summon" or "Transform" or "Metamorphose") { hx?.Play(Fx.Appear, 1, 1, apan); quad.Pulse(now, atl * 0.6f, atr * 0.6f, 25, 400, 0, true, true); }
                            else if (v is "InterruptParalyze" or "InterruptShock" or "InterruptSleepParalysis" or "Confuse" or "InterruptFrozen") hx?.Play(Fx.Stun, 1, 1, apan);
                            else if ((v.StartsWith("InterruptGuard") || v == "Parry") && now - lastSwing < 500) { hx?.Play(Fx.Blocked, 1, 1, 0.5f); quad.Pulse(now, 0, 8, 80, 90); }
                            // v2: what else enemies do
                            else if (v is "Search" or "LookAround" or "Observe" && a.Distance < 14 && now >= nextSearchPing) { hx?.Play(Fx.Ping, 0.35f, 1, apan); nextSearchPing = now + 1500; }
                            else if (v is "GetAngry" or "Uproar" or "Shout" && a.Distance < 15) { hx?.Play(Fx.Rage, 0.6f + 0.4f * (1 - a.Distance / 15), 1, apan); quad.Pulse(now, atl * 0.5f, atr * 0.5f, 30, 300, 0, true, true); }
                            else if (v is "Flee" or "Cower" or "Demoralized" && a.Distance < 12) hx?.Play(Fx.Relief, 0.8f, 1, apan);
                            if (v is "RangedAttack" or "RangedLargeAttack" && a.Distance < 20)
                            {   // a shot comes at you: it whines in from its side and reaches the fingertips a moment later
                                hx?.Play(Fx.Projectile, v == "RangedLargeAttack" ? 1.2f : 0.8f, 1, apan); quad.Pulse(now, atl * 0.7f, atr * 0.7f, 90, 120, 200);
                            }
                            if (v == "InterruptBrokenArmor") { hx?.Play(Fx.ArmorBreak, 1.2f, 1, apan); quad.Pulse(now, atl, atr, 120, 80); quad.Pulse(now, 5, 5, 60, 120, 120); Flash(255, 255, 255, 150); }
                            if (v == "InterruptWeakPointHit") { hx?.Play(Fx.WeakSpot, 1.2f, 1, apan); quad.Pulse(now, 6, 6, 120, 80); Flash(255, 255, 160, 150); }
                            if (v == "InterruptAbsorbShoot") hx?.Play(Fx.Bite, 0.7f, 1, apan);
                            if (v == "InterruptRestraintRideOnPlayer" && a.Distance < 3) act.GrabbedUntil = now + 2500;   // it holds on to Link
                            if (v is "ReleaseRestraint" or "Unstick" && a.Distance < 3) act.GrabbedUntil = 0;
                            Log.Write($"ENEMY {a.Name} -> {v} ({a.Distance:F1} m, side {a.Pan:F1}, front {a.Front:F1})", debug);
                        }
                    bool enemyNear = cb != null && cb.Nearby > 0 && cb.NearestDistance < 10;
                    bool bossNear = cb?.BossNear == true;

                    // kills the walk didn't see (out of range, arrows): the game's per-enemy counters
                    if (now >= nextKills)
                    {
                        foreach (var (name, hash) in KillCounts)
                        {
                            int v = gd.Int(hash) ?? -1;
                            if (v < 0) continue;
                            if (killsReady && killState.TryGetValue(hash, out var old) && v > old
                                && !(recentKills.TryGetValue(name, out var at) && now - at < 1500))
                                KillFx(name, 0);
                            killState[hash] = v;
                        }
                        killsReady = true; nextKills = now + 300;
                    }

                    // ---- treasure ----
                    if (now >= nextFlags)
                    {
                        foreach (var (name, hash) in ItemFlags)
                        {
                            bool v = gd.Bool(hash) ?? false;
                            if (flagsReady && v && flagState.TryGetValue(hash, out var was) && !was) Treasure(name);
                            flagState[hash] = v;
                        }
                        flagsReady = true; nextFlags = now + 400;
                    }
                    if (lastMaxLife > 0 && maxLife > lastMaxLife) { hx?.Play(Fx.HeartContainer); Flash(255, 60, 120, 1600); celebrateUntil = now + 1500; }
                    lastMaxLife = maxLife;

                    // ---- aiming / shooting ----
                    bool atAim = Math.Abs(s.FovDeg - Tuning.Get("aim.fov", AimFov)) < 0.6f; // aim.fov in tuning.json if a setup aims at another FOV
                    aimSince = atAim ? (aimSince < 0 ? now : aimSince) : -1;
                    bool aiming = aimSince >= 0 && now - aimSince >= AimHoldMs;
                    if (wasAiming && !aiming) aimEnd = now;
                    wasAiming = aiming;
                    if (hx != null) { hx.Aiming = aiming && ammo > 0; hx.Rubber = child; hx.LowBoost = iron ? 1.45f : 1f; }
                    if (lastAmmo >= 0 && ammo < lastAmmo && (aiming || now - aimEnd < 400))
                    { hx?.Play(child ? Fx.SlingRelease : Fx.BowRelease); mom.Shot(now); }
                    else if (lastAmmo >= 0 && ammo > lastAmmo) hx?.Play(Fx.AmmoGet);
                    lastAmmo = ammo;
                    if (aiming && ammo == 0 && pull > 0.85f && !dryClicked) { hx?.Play(Fx.DryFire); dryClicked = true; }
                    if (pull < 0.5f) dryClicked = false;
                    if (aiming && !wasAimingNock && ammo > 0) hx?.Play(Fx.Nock);                 // an arrow laid on the string
                    wasAimingNock = aiming;
                    // full draw: a click point at the end of R2's travel
                    if (aiming && ammo > 0 && pull >= 0.95f && !fullDraw) { hx?.Play(Fx.Detent); quad.Pulse(now, 0, 3, 250, 20); fullDraw = true; }
                    if (pull < 0.85f) fullDraw = false;
                    // gyro aiming: every few degrees the aim moves a notch; a steady hand stays quiet
                    if (aiming && ds != null)
                    {
                        aimAcc += (Math.Max(0, Math.Abs(ds.GyroX) - 2) + Math.Max(0, Math.Abs(ds.GyroY) - 2)) * dtF; // 2 °/s dead zone: sensor drift
                        if (aimAcc >= 3) { aimAcc = 0; hx?.Play(Fx.AimTick); }
                    }
                    else aimAcc = 0;

                    // ---- sword: swings, spin attack charge ----
                    bool canSwing = sword && !aiming && !paused;
                    if (canSwing && pressed(BSquare))
                    {
                        squareDown = now;
                        if (now - lastSwing > 120)
                        {
                            // a combo: each hit within 650 ms of the last is the next in the chain, the third is the finisher
                            comboN = now - lastSwing < 650 ? Math.Min(comboN + 1, 3) : 1;
                            float cs = comboN == 1 ? 0.8f : comboN == 2 ? 0.95f : 1.2f;
                            if (cb != null && cb.NearestDistance < 3) cs *= 1.15f;
                            bool twoHanded = weapon.Contains("Giant") || weapon.Contains("Biggoron");
                            if (twoHanded)
                            {   // a two-handed blade: a slow, broad arc, both hands on the hilt, both triggers carry it
                                hx?.Play(Fx.SwingHeavy, cs, swingDir);
                                quad.Pulse(now, 5, 5, 45, 160, 0, true, true);
                            }
                            else
                            {   // the arc runs across the front: from one fingertip to the other, the palms follow
                                float light = weapon.Contains("Kokiri") ? 0.85f : 1f;
                                int tf = weapon.Contains("Kokiri") ? 110 : 85;
                                hx?.Play(Fx.Swing, cs * light, swingDir);
                                int a1 = (int)(3 * cs), a2 = (int)(4.5f * cs);
                                if (swingDir == 1) { quad.Pulse(now, a1, 0, tf, 50); quad.Pulse(now, 0, a2, tf, 60, 70); }
                                else { quad.Pulse(now, 0, a1, tf, 50); quad.Pulse(now, a2, 0, tf, 60, 70); }
                                if (weapon.Contains("Master")) hx?.Play(Fx.BladeRing, cs);
                            }
                            swingDir = 3 - swingDir; lastSwing = now;
                        }
                    }
                    if ((buttons & BSquare) != 0 && squareDown >= 0 && canSwing && now - squareDown > 350)
                        charge = Math.Min(1f, (now - squareDown - 350) / 900f);
                    if (released(BSquare) || !canSwing)
                    {
                        if (charge >= 0.35f && canSwing)
                        {   // the spin goes once round the controller: L2, R2, right palm, left palm
                            hx?.Play(Fx.SpinRelease); Flash(140, 200, 255, 300);
                            quad.Pulse(now, 8, 0, 60, 110); quad.Pulse(now, 0, 8, 60, 110, 120);
                        }
                        charge = 0; squareDown = -1;
                    }
                    if (hx != null) hx.Charge = charge;
                    if (charge >= 1 && !fullCharge) { hx?.Play(Fx.Detent); quad.Pulse(now, 3, 3, 250, 25); Flash(140, 200, 255, 120); fullCharge = true; } // the spin is ready
                    if (charge < 1) fullCharge = false;

                    // ---- what Link is doing (action verbs): textures, reactions, trigger and light overrides ----
                    var anims = oot!.Anims(1);
                    if (anims.Count > 0 && anims[0].name != lastAnim) { lastAnim = anims[0].name; Log.Write($"ANIM {lastAnim}", false); }
                    bool stickIdle = ds == null || Math.Abs(ds.LX - 128) < 24 && Math.Abs(ds.LY - 128) < 24;
                    var ao = act.Update(now, s, verbs, world, buttons, prevButtons, paused ? null : hx, quad, armor, anims, stickIdle, scene.Map);
                    HVoice.Material = Moments.MaterialOf(scene.Map, world.Indoor, ao.Water);
                    if (hx != null) hx.Loops[(int)Loop.Quake] = 0; // raised again below by whatever shakes this frame
                    // story, minigames and places (Masterplan v2)
                    mom.Update(now, gd, paused ? null : hx, quad, act, s, scene.Map, cb, pull, weapon, events.Current, paused, buttons, prevButtons);
                    if (hx != null) hx.HoofPan = ds != null ? -(ds.LX - 128) / 128f : 0; // Epona leans out of the turn
                    bool critical = s.Life > 0 && s.Life <= 4;
                    // voices in dialogue would buzz; at one heart the world goes a little muffled
                    if (hx != null)
                    {
                        hx.SoundGain = sound * Tuning.Get("sound.layer") * (act.DuckSound ? 0.3f : 1f) * (critical ? 0.6f : 1f);
                        hx.Gain = gain * Tuning.Get("master");
                    }

                    // ---- the pause screen: a page you hold ----
                    if (paused != wasPaused) { hx?.Play(paused ? Fx.MenuOpen : Fx.MenuClose); quad.Pulse(now, 2, 2, 60, 50); }
                    wasPaused = paused;
                    if (paused && ds != null)
                    {
                        int dir = ds.Dpad < 8 ? ds.Dpad : StickDir(ds.LX, ds.LY);
                        if (dir >= 0 && (dir != prevDir || now >= navRepeat))
                        {
                            float np = dir is 5 or 6 or 7 ? -0.75f : dir is 1 or 2 or 3 ? 0.75f : 0;
                            hx?.Play(Fx.MenuTick, 1, 1, np);
                            quad.Pulse(now, np < 0 ? 2 : 0, np > 0 ? 2 : 0, 120, 25);
                            navRepeat = now + (dir != prevDir ? 380 : 130);
                        }
                        prevDir = dir;
                        if (pressed(BCircle)) { hx?.Play(Fx.MenuConfirm); quad.Pulse(now, 3, 3, 80, 40); }
                        if (pressed(BCross)) hx?.Play(Fx.MenuBack);
                        bool zl = ds.L2 > 200, zr = ds.R2 > 200;
                        if (pressed(BL1) || zl && prevL2b == 0) { hx?.Play(Fx.PageFlip, 1, 1); quad.Pulse(now, 4, 0, 60, 50); }
                        if (pressed(BR1) || zr && prevR2b == 0) { hx?.Play(Fx.PageFlip, 1, 2); quad.Pulse(now, 0, 4, 60, 50); }
                        prevL2b = zl ? 1 : 0; prevR2b = zr ? 1 : 0;
                    }
                    else prevDir = -1;

                    // ---- gear changes: each has its own weight ----
                    if (prevWeapon.Length > 0 && weapon != prevWeapon && weapon.Length > 0) { hx?.Play(Fx.EquipSword); quad.Pulse(now, 0, 4, 90, 60); }
                    if (prevShield.Length > 0 && shield != prevShield && shield.Length > 0) { hx?.Play(shield.Contains("Deku") ? Fx.EquipShieldWood : Fx.EquipShieldMetal); quad.Pulse(now, 4, 0, 70, 60); }
                    if (prevBoot != boot && prevArmor.Length + prevBoot.Length + prevWeapon.Length > 0)
                    {
                        if (boot.Contains("HeavyBoot")) { hx?.Play(Fx.EquipHeavy); quad.Pulse(now, 6, 0, 30, 90); quad.Pulse(now, 0, 6, 30, 90, 180); }
                        else if (boot.Contains("HoverBoot")) hx?.Play(Fx.EquipHover);
                        else hx?.Play(Fx.EquipCloth);
                    }
                    if (prevArmor.Length > 0 && armor != prevArmor) hx?.Play(Fx.EquipCloth);
                    prevShield = shield; prevBoot = boot; prevArmor = armor; prevWeapon = weapon.Length > 0 ? weapon : prevWeapon;
                    if (lastSel >= 0 && world.SelectedItem != lastSel && world.SelectedItem >= 0) hx?.Play(Fx.ItemTick);
                    lastSel = world.SelectedItem;

                    // ---- the day turns ----
                    int hour = world.Seconds >= 0 ? world.Seconds / 3600 : -1;
                    if (lastHour >= 0 && hour >= 0 && hour != lastHour && !world.Indoor)
                    {
                        if (hour == 6) { hx?.Play(Fx.Sunrise); Flash(255, 150, 80, 2500); }
                        if (hour == 18) { hx?.Play(Fx.Nightfall); Flash(120, 60, 160, 2500); }
                    }
                    lastHour = hour;
                    lKick = Math.Max(lKick, act.Kick); jolt = Math.Max(jolt, act.Jolt); rTick = Math.Max(rTick, act.RTick);
                    if (act.FlashUntil > now && act.FlashUntil > flashUntil) { flash = act.FlashColor; flashMs = (int)(act.FlashUntil - now); flashUntil = act.FlashUntil; }

                    // ---- life ----
                    if (lastLife >= 0 && s.Life < lastLife)
                    {
                        jolt = now + 200;
                        if (s.Life <= 0)
                        {
                            uint reason = gd.Enum(HDeath) ?? 0;
                            var fx = reason == GameData.Hash("LifeDepleted_Fire") || reason == GameData.Hash("LifeDepleted_Lava") ? Fx.DeathFire
                                   : reason == GameData.Hash("LifeDepleted_Drown") ? Fx.DeathDrown
                                   : reason == GameData.Hash("LifeDepleted_Lightning") ? Fx.DeathShock : Fx.Death;
                            hx?.Play(fx); deadUntil = now + 2500; deadAt = now;
                        }
                        else
                        {   // the blow comes from the enemy that just wound up, else from the nearest one
                            float st = (0.6f + (lastLife - s.Life) / 8f) * act.HitScale;
                            bool fromAttacker = now - atkAt < 1500;
                            hx?.Duck(350);
                            if (fromAttacker || cb != null && cb.NearestDistance < 6)
                            {
                                float hs = fromAttacker ? atkSide : cb!.NearestSide, hf = fromAttacker ? atkFront : cb!.NearestFront;
                                var (palm, pan, tl, tr) = Quad.Place(hs, hf, Math.Min(1f, st));
                                hx?.Play(Fx.Hit, Math.Max(palm, 0.6f * st), 1, pan * 0.7f);
                                quad.Pulse(now, Math.Max(tl, 3), Math.Max(tr, 3), 40, 220);
                            }
                            else hx?.Play(Fx.Hit, st);
                            Flash(255, 0, 0, 260); act.HitScale = 1;
                        }
                    }
                    if (lastLife == 0 && s.Life > 0 && deadAt >= 0 && now - deadAt < 15000)
                    { hx?.Play(Fx.Revive); Flash(255, 150, 220, 1200); deadUntil = 0; deadAt = -1; }        // a fairy
                    else if (lastLife > 0 && s.Life > lastLife) { hx?.Play(Fx.Heal); Flash(60, 255, 120, 350); }
                    lastLife = s.Life;

                    // ---- rupees / magic ----
                    if (lastRupee >= 0 && rupee > lastRupee)
                    {
                        int d = rupee - lastRupee;
                        hx?.Play(Fx.Coin, 1, d >= 50 ? 4 : d >= 20 ? 3 : d >= 5 ? 2 : 1); Flash(255, 170, 0, 220);
                        // the player LEDs show the wallet filling up for a moment
                        int cap = (gd.Int(HWalletL) ?? 0) > 0 ? 500 : (gd.Int(HWalletM) ?? 0) > 0 ? 200 : 99;
                        ledShow = Math.Clamp(rupee / (float)cap, 0.05f, 1); ledShowUntil = now + 1400;
                    }
                    else if (lastRupee >= 0 && rupee >= 0 && rupee < lastRupee) hx?.Play(Fx.CoinSpend);
                    lastRupee = rupee;
                    if (lastMp >= 0 && mp < lastMp)
                    {   // one drop is a spell; magic draining again and again is the Lens of Truth
                        lensUntil = now - lastMpDrop < 1500 ? now + 1500 : lensUntil;
                        if (now >= lensUntil) { hx?.Play(Fx.MagicUse); Flash(170, 60, 255, 300); }
                        int mpMax = gd.Int(HMpMax) ?? 0;
                        if (mpMax > 0) { ledShow = Math.Clamp(mp / (float)mpMax, 0.05f, 1); ledShowUntil = now + 1200; } // the magic left
                        lastMpDrop = now;
                    }
                    else if (lastMp >= 0 && mp >= lastMp + 4) hx?.Play(Fx.MagicFill);
                    lastMp = mp;

                    // ---- items used up (read from the pouch) ----
                    int nuts = gd.Int(HNuts) ?? -1, bombs = gd.Int(HBombs) ?? -1, chus = gd.Int(HChus) ?? -1, sticks = gd.Int(HSticks) ?? -1;
                    if (lastNuts >= 0 && nuts < lastNuts) { hx?.Play(Fx.NutFlash); Flash(255, 255, 240, 450); quad.Pulse(now, 5, 5, 120, 80); }
                    if (lastBombs >= 0 && bombs < lastBombs) fuseUntil = now + 4500;                       // pulled a bomb: the fuse is lit
                    if (lastChus >= 0 && chus < lastChus) { hx?.Play(Fx.ChuRoll); quad.Pulse(now, 3, 3, 45, 1200, 0, true, true); }
                    if (lastSticks >= 0 && sticks < lastSticks) hx?.Play(Fx.StickSnap);
                    lastNuts = nuts; lastBombs = bombs; lastChus = chus; lastSticks = sticks;
                    if (fuseUntil > now && hx != null && hx.LowImpact > 0.75f) fuseUntil = 0;                // it went off
                    if (hx != null) hx.Loops[(int)Loop.Fuse] = now < fuseUntil ? 1 : 0;

                    // ---- movement: landing by fall speed, riding (jump, roll, climb and the rest come from the verbs) ----
                    if (s.Vy < -1f) { minVy = Math.Min(minVy, s.Vy); airMinVy = now; }
                    else if (minVy < -5f && Math.Abs(s.Vy) < 0.5f)
                    {
                        float st = Math.Clamp((-minVy - 4f) / 12f, 0.2f, 1.2f);
                        hx?.Play(Fx.Land, st * (iron ? 1.4f : 1f));
                        if (st > 0.5f) jolt = Math.Max(jolt, now + 90);
                        minVy = 0;
                    }
                    else if (now - airMinVy > 1500) minVy = 0;
                    bool onHorse = verbs.Contains(rideVerb);
                    bool fast = (onHorse && s.SpeedH > 2.5f) || (s.SpeedH > 7.5f && Math.Abs(s.Vy) < 3f && !ao.Water);
                    rideSince = fast ? (rideSince < 0 ? now : rideSince) : -1;
                    bool riding = rideSince >= 0 && now - rideSince > 300;
                    if (riding && (s.SpeedH - prevSpeed) / dtF > 22f && now > gallopSpur) { hx?.Play(Fx.Spur); gallopSpur = now + 1500; }
                    prevSpeed = s.SpeedH;

                    // ---- places ----
                    if (!paused && scene.Ambient(now) is { } amb) hx?.Play(amb.fx, ambient, 1, amb.pan);

                    // ---- senses ----
                    // sonar: an enemy close by but not in front of you pings from where it is, faster as it nears
                    // haptic captions (tuning "captions" = 1): every enemy nearby pings, in front too, from further away
                    bool captions = Tuning.Get("captions", 0) > 0.5f;
                    if (!paused && !act.DuckSound && cb != null && cb.NearestDistance < (captions ? 14 : 9) && (captions || cb.NearestFront < 0.3f) && now >= nextSonar)
                    {
                        var (palm, pan, tl, tr) = Quad.Place(cb.NearestSide, cb.NearestFront, 0.5f);
                        hx?.Play(Fx.Ping, Math.Max(0.3f, palm), 1, pan);
                        quad.Pulse(now, tl * 0.4f, tr * 0.4f, 100, 40);
                        nextSonar = now + (long)(350 + 1050 * Math.Clamp((cb.NearestDistance - 1.5f) / 7.5f, 0, 1));
                    }
                    // heat: fire places without the Goron tunic press in slow waves that grow with time
                    bool hot = scene.Current == Scene.Theme.Fire && (scene.Map.Contains("Crater") || scene.Map.Contains("FireTemple")) && !armor.Contains("Goron");
                    heatSince = hot ? (heatSince < 0 ? now : heatSince) : -1;
                    float heat = hot ? Math.Min(1f, 0.3f + (now - heatSince) / 60000f) : 0;
                    if (hot && !paused && now >= nextHeat) { hx?.Play(Fx.HeatPulse, heat); quad.Pulse(now, 2 * heat, 2 * heat, 20, 600, 0, true, true); nextHeat = now + 2600 - (long)(1200 * heat); }
                    // rain: drops tap the fingertips too, and running splashes through puddles
                    if (act.Raining && !world.Indoor && !paused)
                    {
                        if (now >= nextDrop) { bool left = Random.Shared.NextDouble() < 0.5; quad.Pulse(now, left ? 1.5f : 0, left ? 0 : 1.5f, 200, 30); nextDrop = now + Random.Shared.Next(200, 700); }
                        if (s.SpeedH > 1 && Math.Abs(s.Vy) < 0.5f && !ao.Water)
                        {
                            double before = stepPh; stepPh += s.SpeedH * 0.55 * dtF;
                            if (Math.Floor(stepPh) != Math.Floor(before)) hx?.Play(Fx.Puddle, 1, 1, (long)Math.Floor(stepPh) % 2 == 0 ? -0.6f : 0.6f);
                        }
                    }
                    // a big boom in the game's sound (explosions, stomps, a boss landing) shakes the fingertips as well,
                    // and sharp bright hits (blades, metal, rock) tick the trigger on their side
                    if (hx != null)
                    {
                        if (hx.LowImpact > 0.75f && !paused) { quad.Pulse(now, Math.Min(8, hx.LowImpact * 6), Math.Min(8, hx.LowImpact * 6), 30, 160); hx.Duck(250); }
                        if (!paused && !act.DuckSound && now >= nextAudioTick && Math.Max(hx.ImpactL, hx.ImpactR) > 1.1f)
                        {
                            quad.Pulse(now, Math.Min(4, hx.ImpactL * 2), Math.Min(4, hx.ImpactR * 2), 150, 35);
                            nextAudioTick = now + 90;
                        }
                        lowImpNow = hx.LowImpact; hx.LowImpact = 0; hx.ImpactL = hx.ImpactR = 0;
                    }

                    // ---- the world around: treasure, heat, water, danger, locks ----
                    if (cb != null && hx != null && !paused)
                    {
                        Combat.Seen? treasure = null, danger = null; float warm = 0, warmPan = 0, mist = 0, mistPan = 0;
                        foreach (var o in cb.Objects)
                        {
                            switch (o.Kind)
                            {
                                case Combat.ObjKind.Treasure when o.Distance < 9 && (treasure == null || o.Distance < treasure.Value.Distance): treasure = o; break;
                                case Combat.ObjKind.Heat when o.Distance < 6:
                                { float k = (1 - o.Distance / 6) * (1 - o.Distance / 6); warm += k; warmPan += o.Pan * k; break; }
                                case Combat.ObjKind.Water when o.Distance < 10:
                                { float k = 1 - o.Distance / 10; mist = Math.Max(mist, k); mistPan = o.Pan; break; }
                                case Combat.ObjKind.Danger when o.Distance < 16 && o.Closing > 1.5f && (danger == null || o.Distance < danger.Value.Distance): danger = o; break;
                            }
                        }
                        // treasure sense: a glint from where it lies, quicker the nearer you are
                        if (treasure is { } tr0 && now >= nextSparkle && !act.InEvent)
                        {
                            var (palm, tpan, tl, ttr) = Quad.Place(tr0.Pan, tr0.Front, 0.6f);
                            hx.Play(Fx.Sparkle, Math.Max(0.4f, palm), 1, tpan);
                            quad.Pulse(now, tl * 0.3f, ttr * 0.3f, 200, 30);
                            nextSparkle = now + (long)(350 + 1300 * Math.Clamp(tr0.Distance / 9, 0, 1));
                        }
                        // fire warms you from its side; a waterfall sprays from its side
                        hx.Loops[(int)Loop.Warmth] = Math.Min(1f, warm);
                        hx.WarmPan = warm > 0 ? Math.Clamp(warmPan / warm, -1, 1) : 0;
                        hx.Loops[(int)Loop.Mist] = mist;
                        hx.MistPan = mistPan;
                        // something heavy rolling at you: a rumble that swells from its side, and the fingers shake as it passes
                        if (danger is { } dg)
                        {
                            hx.Loops[(int)Loop.Danger] = Math.Clamp(1.2f - dg.Distance / 14, 0.2f, 1);
                            hx.DangerPan = dg.Pan;
                            if (dg.Distance < 4 && dg.Front > 0) quad.Pulse(now, 5 * (1 - dg.Pan) / 2 + 2, 5 * (1 + dg.Pan) / 2 + 2, 30, 80, 0, true, true);
                        }
                        else hx.Loops[(int)Loop.Danger] = 0;
                        // a chain lock that just vanished next to you was opened with a key
                        foreach (var g in cb.Gone)
                            if (g.Kind == Combat.ObjKind.Lock) { hx.Play(Fx.Unlock); quad.Pulse(now, 4, 4, 60, 60, 350); }
                    }
                    // iron boots: every step a heavy stamp, left and right foot, the triggers feel the weight land
                    bool grounded = Math.Abs(s.Vy) < 0.5f && !ao.Water && !riding;
                    float walk = Math.Max(s.SpeedH, act.PosSpeed);
                    if (iron && grounded && walk > 0.4f && !paused)
                    {
                        double before = ironPh; ironPh += walk * 0.75 * dtF;
                        if (Math.Floor(ironPh) != Math.Floor(before))
                        {
                            bool leftFoot = (long)Math.Floor(ironPh) % 2 == 0;
                            hx?.Play(Fx.Footstep, 1, 1, leftFoot ? -0.55f : 0.55f);
                            quad.Pulse(now, leftFoot ? 3 : 1, leftFoot ? 1 : 3, 35, 70);
                        }
                    }
                    // dashing: the air rushes past the hands
                    if (hx != null) hx.Loops[(int)Loop.Wind] = Math.Max(hx.Loops[(int)Loop.Wind], grounded && walk > 5.5f && !paused ? Math.Min(0.45f, (walk - 5.5f) / 4f + 0.2f) : 0);
                    // ---- scripted moments: songs, warps, boss intros, sages ... (by the event's name) ----
                    if (now >= nextEvent)
                    {
                        events.Update(now, oot.EventName(), act.InEvent, paused ? null : hx, quad, act, m => Log.Write(m, debug));
                        nextEvent = now + 100;
                    }
                    if (hx != null && now < events.HoovesUntil)
                    {   // Epona's Song: she gallops in from far away, louder and closer until she is there
                        float k = (now - events.HoovesFrom) / (float)Math.Max(1, events.HoovesUntil - events.HoovesFrom);
                        hx.Loops[(int)Loop.Hooves] = Math.Max(hx.Loops[(int)Loop.Hooves], 0.15f + 0.85f * k * k); hx.HoofRate = 2.4f;
                    }

                    // camera shake detection removed on request (camera turns kept triggering it); explosions and
                    // stomps still shake the controller through the game sound (LowImpact above). The quake texture
                    // now only comes from the castle escape (Moments) and Ganon's tower (boss signatures below).

                    // ---- boss signatures: each boss gets its own feel on top of the general combat ----
                    if (cb != null && hx != null && !paused)
                    {
                        Combat.Foe? boss = null;
                        foreach (var f in cb.Live) if (f.Boss && (boss == null || f.Distance < boss.Value.Distance)) boss = f;
                        if (boss is { } bs)
                        {
                            string bn = bs.Name;
                            var (bpalm, bpan, btl, btr) = Quad.Place(bs.Pan, bs.Front, 0.6f);
                            if (bn.Contains("Gohma") && now >= nextBossFx)
                            {   // a giant spider scuttling: bursts of tiny legs from where she is
                                hx.Play(Fx.AmbSand, 0.9f, 1, bpan); nextBossFx = now + 1400;
                            }
                            if (bn.Contains("Barinade")) { hx.Loops[(int)Loop.Shock] = Math.Max(hx.Loops[(int)Loop.Shock], bs.Distance < 7 ? 0.18f : 0.06f); }
                            if (bn.Contains("Morpha")) { hx.Loops[(int)Loop.Mist] = Math.Max(hx.Loops[(int)Loop.Mist], 0.5f); hx.MistPan = bs.Pan; }
                            if (bn.Contains("Ganon") && !bn.Contains("Phantom") && !bn.Contains("Ganondorf")) hx.Loops[(int)Loop.Quake] = Math.Max(hx.Loops[(int)Loop.Quake], 0.15f); // the tower falls apart around you
                            if (bn.Contains("BongoBongo") && lowImpNow > 0.4f) quad.Pulse(now, 8, 8, 30, 120);                   // his drums go through your fingers
                            bossLight = bn.Contains("Volvagia") ? (255, 60, 0) : bn.Contains("Twinrova") || bn.Contains("Kotake") || bn.Contains("Koume") ? (now / 1500 % 2 == 0 ? (255, 60, 0) : (60, 160, 255))
                                      : bn.Contains("Barinade") ? (80, 200, 255) : bn.Contains("BongoBongo") ? (90, 0, 140) : bn.Contains("Morpha") ? (20, 90, 255) : (200, 0, 30);
                        }
                        else bossLight = null;
                        foreach (var a in cb.Acts)
                        {
                            if (!a.Boss) continue;
                            var (_, apan2, atl2, atr2) = Quad.Place(a.Pan, a.Front, 0.9f);
                            if (a.Name.Contains("Volvagia") && a.Verb is "Hide" or "Dive" or "QuickDive" or "Vanish") { volvagiaUnder = true; }
                            if (a.Name.Contains("Volvagia") && a.Verb is "Appear" or "JumpOut")
                            {   // it bursts out of the lava
                                volvagiaUnder = false; hx.Play(Fx.DinFire, 1.2f); quad.Pulse(now, atl2 + 3, atr2 + 3, 30, 300, 0, true, true); hx.Duck(600);
                            }
                        }
                        // Volvagia under the ground: a rumble that wanders under you
                        if (volvagiaUnder && boss is { } vb && vb.Name.Contains("Volvagia")) { hx.Loops[(int)Loop.Danger] = Math.Max(hx.Loops[(int)Loop.Danger], 0.7f); hx.DangerPan = vb.Pan; }
                        else if (boss == null) volvagiaUnder = false;
                        // Twinrova: every block with the Mirror Shield charges it; the third one releases the blast
                        if (boss is { } tw && (tw.Name.Contains("Twinrova") || tw.Name.Contains("Kotake") || tw.Name.Contains("Koume")) && act.HasVerb("InterruptGuard") && now - lastShieldCharge > 600)
                        {
                            shieldCharge++; lastShieldCharge = now; ledShow = Math.Min(1, shieldCharge / 3f); ledShowUntil = now + 2500;
                            if (shieldCharge >= 3) { hx.Play(Fx.Perfect, 1.3f); hx.Play(Fx.DinFire); quad.Pulse(now, 8, 8, 40, 400); shieldCharge = 0; }
                        }
                    }

                    // Epona's fore hooves land in the triggers, in step with the hind hooves in the palms
                    if (riding && hx != null)
                    {
                        double hp = hx.HoofPhase; long beat = (long)Math.Floor(hp); double ph = hp - beat;
                        if (beat != lastHoofBeat && ph > 0.18) { quad.Pulse(now, 7, 0, 40, 60); quad.Pulse(now, 0, 8, 40, 70, (int)(0.18 / hx.HoofRate * 1000)); lastHoofBeat = beat; }
                    }

                    // ---- Navi's compass: swipe up on the touchpad (no click) and a pulse runs toward what matters most
                    //      nearby: treasure (green), else the nearest enemy (yellow), else nothing (blue, two soft ticks) ----
                    if (ds != null && !paused)
                    {
                        if (ds.TouchDown && !ds.TouchClick) { if (touchFree < 0) { touchFree = now; touchFreeY = ds.TouchY; touchFired = false; } }
                        else touchFree = -1;
                        if (touchFree >= 0 && !touchFired && now - touchFree < 450 && touchFreeY - ds.TouchY > 350 && hx != null)
                        {
                            touchFired = true; // once per swipe
                            Combat.Seen? goal = null; Combat.Foe? foe = null;
                            if (cb != null)
                            {
                                foreach (var o in cb.Objects) if (o.Kind == Combat.ObjKind.Treasure && (goal == null || o.Distance < goal.Value.Distance)) goal = o;
                                foreach (var f in cb.Live) if (f.Distance < 25 && (foe == null || f.Distance < foe.Value.Distance)) foe = f;
                            }
                            if (goal != null || foe != null)
                            {
                                float side = goal is { } g1 ? g1.Pan : foe!.Value.Pan, front = goal is { } g2 ? g2.Front : foe!.Value.Front;
                                var (palm, pan, tl, tr) = Quad.Place(side, front, 0.8f);
                                hx.Play(Fx.Compass, Math.Max(0.5f, palm), 1, pan); quad.Pulse(now, tl * 0.6f, tr * 0.6f, 150, 60, 0); quad.Pulse(now, tl * 0.6f, tr * 0.6f, 150, 60, 220);
                                naviColor = goal != null ? (60, 255, 90) : (255, 220, 40);
                            }
                            else { hx.Play(Fx.Compass, 0.35f); naviColor = (80, 140, 255); }
                            naviUntil = now + 900;
                        }
                    }

                    // ---- triggers ----
                    int heavy = iron ? 1 : hover ? -2 : 0;
                    bool low = s.Life > 0 && s.Life * 4 <= s.LifeMax;
                    if (paused && hx != null) Array.Clear(hx.Loops);
                    if (now < deadUntil) { r = Off; l = Off; }
                    else if (paused) { r = l = Effects.Weapon(3, 5, 3); (r, l) = quad.Apply(now, r, l); } // menu: light button detents, ticks on top
                    else
                    {
                        // firm by default: a short, hard click point on R2, the full weight of the shield on L2
                        if (aiming && ammo > 0) r = child ? Effects.Bow(0, 6, Math.Clamp(7 + heavy, 1, 8), 8) : Effects.Bow(0, 7, 8, 8);
                        // empty quiver / no seeds: after the dry click R2 jams (Deathloop style). Only after a dry pull, because
                        // the same aiming view is used by the hookshot and boomerang, which need no arrows.
                        else if (aiming && dryClicked) r = Effects.Feedback(2, 8);
                        else if (aiming) r = Effects.Feedback(1, 2);
                        else r = Effects.Weapon(2, 4, Math.Clamp(8 + heavy, 1, 8));

                        l = shield.Contains("Mirror") || shield.Contains("Hylian") ? Effects.Feedback(0, Math.Clamp(8 + heavy, 1, 8))
                          : shield.Contains("Deku") ? Effects.Slope(0, 9, Math.Clamp(6 + heavy, 1, 8), 8)
                          : Effects.Feedback(1, Math.Clamp(5 + heavy, 1, 8));

                        if (ao.R != null) r = ao.R;
                        if (ao.L != null) l = ao.L;
                        if (ao.FreeTriggers) r = l = Off;
                        if (mom.R != null) r = mom.R;
                        if (mom.L != null) l = mom.L;
                        if (charge > 0) r = l = Effects.Vibration(0, 2 + (int)(6 * charge), 30 + (int)(60 * charge));
                        if (riding) r = l = Effects.Feedback(1, 4); // the reins; the hooves come through Quad in step
                        if (now < braceUntil && !riding) l = Effects.Feedback(0, 8); // an enemy winds up: the shield hand braces
                        if (low)
                        {   // the heartbeat goes through all four points: lub hard in both triggers, dub softer
                            int period = critical ? 650 : 1000;
                            long ph = now % period;
                            if (now >= nextBeat)
                            {
                                hx?.Play(Fx.Heartbeat, critical ? 1.1f : 0.8f);
                                quad.Pulse(now, critical ? 8 : 5, critical ? 8 : 5, 30, 75, 0, false, true);
                                quad.Pulse(now, critical ? 5 : 3, critical ? 5 : 3, 30, 65, 170, false, true);
                                nextBeat = now - ph + period;
                            }
                        }
                        if (now < celebrateUntil) r = l = Effects.Machine(1, 9, 6, 2, 30, 2);
                        // each trigger's resistance can be turned down or up in tuning.json (the shield's weight on L2 tires some hands)
                        r = Effects.Scale(r, Tuning.Get("triggers.R2")); l = Effects.Scale(l, Tuning.Get("triggers.L2"));
                        (r, l) = quad.Apply(now, r, l);
                        if (now < rTick) r = Effects.Vibration(3, 6, 60);
                        if (now < lKick) l = Effects.Vibration(0, 8, 35);
                        if (now < jolt) r = l = Effects.Vibration(0, 8, 45);
                    }

                    // ---- lights ----
                    if (lightsOn)
                    {
                        (float R, float G, float B) c = armor.Contains("Goron") ? (200, 25, 10) : armor.Contains("Zora") ? (15, 70, 230) : (0, 170, 40);
                        if (scene.Light(now) is { } place) c = place;
                        if (!world.Indoor && world.Seconds >= 0) c = TimeOfDay(c, world.Seconds);
                        if (act.Raining && !world.Indoor) c = Mix(c, (90, 100, 130), 0.4f);
                        if (ao.Light is { } own) c = Mix(c, own, ao.LightMix);
                        if (events.LightNow(now) is { } evl) c = Mix(c, evl, 0.85f);
                        if (mom.Light(now) is { } ml) c = Mix(c, ml.c, ml.mix);
                        if (now < naviUntil) c = Mix(c, naviColor, 0.8f * (naviUntil - now) / 900f);
                        if (quakeLvl > 0.3f) c = (c.Item1 * (1 - 0.3f * quakeLvl), c.Item2 * (1 - 0.3f * quakeLvl), c.Item3 * (1 - 0.3f * quakeLvl)); // the light shudders
                        if (now < lensUntil) c = Mix(c, (130, 40, 210), 0.55f + 0.15f * (float)Math.Sin(now / 1000.0 * 6)); // Lens of Truth
                        if (now < fuseUntil) { bool on = now / Math.Max(60, (fuseUntil - now) / 12) % 2 == 0; c = Mix(c, on ? (255, 120, 0) : (60, 20, 0), 0.6f); } // the fuse burns faster
                        if (bossNear) { float p = 0.5f + 0.5f * (float)Math.Sin(now / 1000.0 * 2 * Math.PI * 0.8); var bl = bossLight ?? (220, 0, 30); c = Mix(c, (bl.Item1 * p, bl.Item2 * p, bl.Item3 * p), 0.6f); }
                        else if (enemyNear) { float p = 0.5f + 0.5f * (float)Math.Sin(now / 1000.0 * 2 * Math.PI * (1 + 3 / Math.Max(1, cb!.NearestDistance))); c = Mix(c, (255, 140 * p, 0), 0.35f); }
                        if (low)
                        {
                            int period = s.Life <= 4 ? 700 : 1000;
                            c = Mix(c, ((now % period) < 260 ? 255 : 60, 0, 0), 0.85f);
                        }
                        if (now < flashUntil) c = Mix(c, (flash.r, flash.g, flash.b), (flashUntil - now) / (float)flashMs);
                        if (now < deadUntil) { float a = (deadUntil - now) / 2500f; c = (180 * a, 0, 0); }
                        if (paused) c = (c.R * 0.25f, c.G * 0.25f, c.B * 0.25f);

                        // player LEDs: the bow's draw while aiming, the spin's charge, else the life bar
                        // player LEDs, most urgent first: bow draw, spin charge, strength level, wallet/magic, breath,
                        // the boss's life, the locked-on enemy's life, else Link's own
                        float bossFrac = -1, targetFrac = -1;
                        if (cb != null)
                        {
                            foreach (var f in cb.Live) if (f.Boss && f.Distance < 30) bossFrac = Math.Max(bossFrac, f.Life / (float)f.Max);
                            if (act.HasVerb("LockOnWait") || act.HasVerb("LockOnMove"))
                            {
                                Combat.Foe? best = null;
                                foreach (var f in cb.Live) if (f.Front > 0.5f && f.Distance < 15 && (best == null || f.Distance < best.Value.Distance)) best = f;
                                if (best is { } bt) targetFrac = bt.Life / (float)bt.Max;
                            }
                        }
                        bool grabbedNow = now < act.GrabbedUntil;
                        int src = aiming && ammo > 0 && pull >= 0 ? 1 : charge > 0 ? 2 : now < ledLevelUntil ? 3 : now < ledShowUntil ? 4 : grabbedNow ? 5
                                : mom.Bar >= 0 ? 6 : act.AirLeft >= 0 ? 7 : bossFrac >= 0 ? 8 : targetFrac >= 0 ? 9 : 10;
                        float bar = src switch
                        {
                            1 => pull, 2 => charge, 3 => Math.Clamp(Tuning.Get(touchKey) / (touchKey == "master" ? 2f : 1.5f), 0, 1), 4 => ledShow,
                            5 => Math.Min(1, act.GrabPresses / 8f), 6 => mom.Bar, 7 => act.AirLeft, 8 => bossFrac, 9 => targetFrac,
                            _ => s.LifeMax > 0 ? Math.Max(0, s.Life) / (float)s.LifeMax : 0,
                        };
                        int leds = (int)Math.Ceiling(5 * Math.Clamp(bar, 0, 1));
                        if (now < ledChipUntil && (bossFrac >= 0 || targetFrac >= 0)) leds = Math.Max(0, leds - 1); // a chip breaks off
                        if (act.AirLeft >= 0 && act.AirLeft < 0.25f && now % 400 < 200) leds = 0;                  // last breath blinks
                        if (critical && now % 650 >= 260 && now >= ledShowUntil && charge == 0 && !aiming) leds = 0; // the last heart blinks with the beat
                        int ledMask = (1 << leds) - 1;
                        // a new meter announces itself: one light runs across before it shows its value
                        if (src != ledSrc) { if (ledSrc >= 0 && src != 10) ledSweepUntil = now + 240; ledSrc = src; }
                        if (now < ledSweepUntil) ledMask = 1 << (int)Math.Clamp((now - (ledSweepUntil - 240)) / 48, 0, 4);
                        byte mic = (byte)Math.Max(s.Life > 0 && s.Life <= 4 ? 2 : bossNear ? 1 : 0, mom.Mic);
                        // the light glides between colours (~80 ms); flashes, alarms and blinking cut straight through
                        bool hardCut = now < flashUntil || low || now < deadUntil || paused || ao.LightMix >= 0.95f || mom.Escaping || now < act.TerrorUntil;
                        float k = hardCut ? 1 : 1 - MathF.Exp(-dtF / 0.08f);
                        lightNow = (lightNow.R + (c.R - lightNow.R) * k, lightNow.G + (c.G - lightNow.G) * k, lightNow.B + (c.B - lightNow.B) * k);
                        c = lightNow;
                        lights = new DualSense.Lights((byte)Math.Clamp(c.R, 0, 255), (byte)Math.Clamp(c.G, 0, 255), (byte)Math.Clamp(c.B, 0, 255), (byte)ledMask, mic);
                    }

                    if (debug)
                    {
                        string d = $"{scene.Map}({scene.Current}) life {s.Life}/{s.LifeMax} {(child ? "child" : "adult")} ammo {ammo} rupee {rupee} mp {mp} shield '{shield}' " +
                                   $"boot '{boot}' enemies {cb?.Nearby}@{cb?.NearestDistance:F0}m{(bossNear ? " BOSS" : "")} pause {paused} aim {aiming} ride {riding} charge {charge:F1} " +
                                   $"verbs [{act.Debug}] item {world.UsingItem}/{world.SelectedItem} {(world.Indoor ? "in" : "out")}{(world.UnderRoof ? "+roof" : "")} {world.Seconds / 3600:00}:{world.Seconds / 60 % 60:00}{(act.Raining ? " RAIN" : "")}" +
                                   $"{(act.Flying ? " FLY" : "")}{(act.Sinking ? " SINK" : "")}{(cb != null && cb.Objects.Count > 0 ? $" objs {cb.Objects.Count}" : "")} | {mom.Debug} mat {HVoice.Material}";
                        if (d != lastDbg) Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {d}");
                        lastDbg = d;
                    }

                    void KillFx(string name, float pan)
                    {
                        Fx fx = Family(name);
                        hx?.Play(fx, 1, 1, pan * 0.6f); hx?.Duck(fx is Fx.KillBoss or Fx.KillMidBoss ? 1500 : 300);
                        if (name.Contains("Ganon") && fx == Fx.KillBoss)
                        {   // the last blow on Ganon: the biggest moment of all, then seven years of light
                            hx?.Hush(1500); hx?.Play(Fx.KillBoss, 1.5f); hx?.Play(Fx.TimeShift, 1.2f); quad.Pulse(now, 8, 8, 30, 600, 0, true, true);
                            Flash(255, 255, 255, 4000); celebrateUntil = now + 2500;
                        }
                        if (fx == Fx.KillBoss) { Flash(255, 220, 120, 2500); celebrateUntil = now + 1400; }
                        else if (fx == Fx.KillMidBoss) { Flash(255, 200, 120, 1200); celebrateUntil = now + 800; }
                        else if (fx == Fx.Token) Flash(255, 200, 0, 300);
                        else if (fx == Fx.KillGhost) Flash(160, 90, 255, 600);
                    }

                    void Treasure(string item)
                    {
                        Fx fx = item.Contains("Melody") ? Fx.SongGet
                              : item.Contains("BossKey") ? Fx.BossKeyGet
                              : item.Contains("SmallKey") ? Fx.KeyGet
                              : item.Contains("Map") || item.Contains("Compass") ? Fx.MapGet
                              : item.Contains("Medal") || item.Contains("Stone") ? Fx.MedalGet
                              : Fx.ItemGet;
                        hx?.Play(fx);
                        (byte, byte, byte) col = fx == Fx.SongGet ? ((byte)120, (byte)180, (byte)255) : fx == Fx.MedalGet ? ((byte)255, (byte)255, (byte)255) : ((byte)255, (byte)200, (byte)40);
                        Flash(col.Item1, col.Item2, col.Item3, fx == Fx.MedalGet ? 2500 : 1300);
                        if (fx is Fx.ItemGet or Fx.MedalGet or Fx.BossKeyGet) celebrateUntil = now + 900;
                        Log.Write($"TREASURE {item} -> {fx}", debug);
                    }
                }
                else
                {
                    lastLife = lastAmmo = lastRupee = lastMp = lastMaxLife = -1; aimSince = -1; minVy = 0; charge = 0;
                    lastNuts = lastBombs = lastChus = lastSticks = -1; fuseUntil = 0;
                    if (hx != null) { hx.Aiming = false; hx.Charge = 0; Array.Clear(hx.Loops); hx.Muffle = false; }
                    if (loading && lightsOn && ds != null) lights = new DualSense.Lights(0, 0, 0, 0, 0); // dark while the map loads
                }
                prevButtons = buttons;

                // ---- strength on the touchpad: hold the click and slide up/down to step it; a click alone cycles
                //      gentle 0.6 / normal 1.0 / extreme 1.5. Where the finger starts picks what changes: the left
                //      third = L2's resistance, the right third = R2's, the middle = everything. The player LEDs show the level. ----
                if (ds != null)
                {
                    if (ds.TouchClick && touchStart < 0)
                    {
                        touchStart = now; touchY0 = ds.TouchY; touchSteps = 0; touchMoved = false;
                        touchKey = ds.TouchX < 640 ? "triggers.L2" : ds.TouchX > 1280 ? "triggers.R2" : "master";
                    }
                    if (ds.TouchClick && touchStart >= 0 && ds.TouchDown)
                    {
                        int steps = (touchY0 - ds.TouchY) / 120;
                        if (steps != touchSteps)
                        {
                            bool trig = touchKey != "master";
                            float m = Math.Clamp(MathF.Round((Tuning.Get(touchKey) + 0.1f * (steps - touchSteps)) * 10) / 10, trig ? 0f : 0.2f, trig ? 1.5f : 2f);
                            Tuning.Set(touchKey, m); touchSteps = steps; touchMoved = true;
                            hx?.Play(Fx.ProfileTick, 1, Math.Clamp((int)(m * (trig ? 3.4f : 2.5f)), 1, 5)); ledLevelUntil = now + 1500;
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {touchKey} -> {m}");
                        }
                    }
                    if (!ds.TouchClick && touchStart >= 0)
                    {
                        if (!touchMoved && now - touchStart < 600)
                        {
                            float m = Tuning.Get("master"), next = m < 0.8f ? 1.0f : m < 1.25f ? 1.5f : 0.6f;
                            Tuning.Set("master", next); touchKey = "master";
                            hx?.Play(Fx.ProfileTick, 1, next < 0.8f ? 1 : next < 1.25f ? 3 : 5); ledLevelUntil = now + 1500;
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} strength profile -> {next}");
                        }
                        touchStart = -1;
                    }
                }

                if (ds != null && (!Same(r, sentR) || !Same(l, sentL) || !Equals(lights, sentLights) || now - lastSend > 3000))
                {
                    if (ds.SetOutput(r, l, lights)) { sentR = r; sentL = l; sentLights = lights; lastSend = now; }
                    else { Console.WriteLine("DualSense disconnected."); ds.Dispose(); ds = null; }
                }

                if (debug && hx != null && now >= nextLevel)
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} haptics game-audio peak {hx.PeakIn:F3} -> coils peak {hx.PeakOut:F3} rms {hx.TakeRms():F3}");
                    hx.PeakIn = hx.PeakOut = 0; nextLevel = now + 10000;
                }
                Thread.Sleep(8); // ~120 updates a second: reactions within a few ms of the game
            }
            catch (Exception ex)
            {   // one bad frame (a read during a map change, an odd value) must never stop the controller support
                string err = $"{ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}";
                if (err != lastErr) { Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} error (keeps running): {err}"); Log.Write("ERROR " + err, false); lastErr = err; }
                Thread.Sleep(50);
            }
        }
        ds?.SetOutput(Off, Off, null);
        ds?.Dispose(); cb?.Dispose(); gm?.Dispose(); hx?.Dispose();
        Haptics.Recorder = null; fxLog?.Dispose();
        return 0;
    }

    static StreamWriter? TryOpen(string path) { try { return new StreamWriter(path, false) { AutoFlush = true }; } catch { return null; } }

    /// Outdoors the light follows the sun: rose-orange at dawn, full by day, amber at dusk, dim blue at night.
    static (float, float, float) TimeOfDay((float R, float G, float B) c, int seconds)
    {
        double h = seconds / 3600.0 % 24;
        if (h >= 5 && h < 7) return Mix(c, (255, 120, 70), 0.35f * (float)(1 - Math.Abs(h - 6)));
        if (h >= 7 && h < 17) return c;
        if (h >= 17 && h < 19.5) return Mix(c, (255, 100, 30), 0.4f * (float)Math.Clamp((h - 17) / 1.2, 0, 1));
        var dim = (c.R * 0.45f, c.G * 0.45f, c.B * 0.5f);
        return Mix(dim, (25, 35, 130), 0.4f);
    }

    static (float, float, float) Mix((float R, float G, float B) a, (float R, float G, float B) b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
    }

    /// How an enemy dies, by its actor / GameData name.
    static Fx Family(string n)
    {
        bool Has(params string[] k) => k.Any(x => n.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (Has("DungeonBoss")) return Fx.KillBoss;
        if (Has("MiddleBoss")) return Fx.KillMidBoss;
        if (Has("Skulltula_Gold")) return Fx.Token;
        if (Has("Stalfos", "Stalchild")) return Fx.KillBones;
        if (Has("IronKnuckle", "Lizalfos", "Dinolfos", "Moblin", "Amos", "Anubis")) return Fx.KillArmor;
        if (Has("Poe", "Floormaster", "SkullKid")) return Fx.KillGhost;
        if (Has("LikeLike", "Bari", "Biri", "Octorok", "ShellBlade", "Freezard", "Shabom")) return Fx.KillSquish;
        if (Has("Dekubaba", "Scrub", "Skulltula", "Skullwalltula", "Peahat")) return Fx.KillCrunch;
        if (Has("Dodongo", "Leever", "Beamos", "ReDead", "Gibdo", "DeadHand", "Wolfos")) return Fx.KillHeavy;
        return Fx.KillPop;
    }

    static bool Same(byte[] a, byte[]? b) => b != null && a.AsSpan().SequenceEqual(b);
}
