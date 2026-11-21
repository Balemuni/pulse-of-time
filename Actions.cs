namespace PulseOfTime;

/// Everything driven by Link's action verbs (the game's own state machine, read from the BSA layers):
/// what he is doing decides the textures in the coils, how the triggers resist and how the light looks.
sealed class Actions
{
    public readonly record struct Out(byte[]? R, byte[]? L, (float R, float G, float B)? Light, float LightMix, bool FreeTriggers, bool Water);

    readonly HashSet<string> _now = new(), _before = new();
    readonly Dictionary<string, long> _seen = new();
    readonly Dictionary<string, long> _since = new();
    readonly Random _rng = new(3);
    long _burnUntil, _freezeUntil, _shockUntil, _squeezeUntil, _windUntil, _nayruUntil, _fadeAbyss, _rainUntil;
    long _gripPulseUntil, _noteFlashUntil;
    int _gripSide = 1; float _climbTravel; int _noteBtn;
    (float, float, float) _noteColor;
    public bool Raining => _rainUntil > 0;
    public void StartRain(long now, int ms) => _rainUntil = now + ms;

    static readonly string[] Dive = { "Dive", "QuickDive", "WaterDive", "AirDive", "GuideDive" };
    static readonly (int btn, float hz, (float, float, float) col)[] Notes =
    {   // Switch A, L, R, Y, X = PS Circle, L1, R1, Square, Triangle; pitches D F A B D' moved into the coils' range
        (4, 150, (40, 90, 255)), (16, 178, (255, 210, 0)), (32, 224, (255, 190, 20)), (1, 252, (255, 170, 40)), (8, 300, (255, 230, 60)),
    };

    bool Has(string v) => _now.Contains(v);
    bool Entered(string v) => _now.Contains(v) && !_before.Contains(v);
    bool EnteredAny(params string[] v) => v.Any(Entered);
    bool HasAny(params string[] v) => v.Any(Has);
    long Held(string v, long now) => _since.TryGetValue(v, out var t) ? now - t : 0;

    public Out Update(long now, Oot.State s, int[] verbs, Oot.WorldState w, int buttons, int prevButtons, Haptics? hx, Quad q, string armor,
                      List<(string name, float frame)> anims, bool stickIdle, string map)
    {
        _before.Clear(); foreach (var v in _now) _before.Add(v);
        _now.Clear();
        // a slot reads empty for a frame now and then while the engine swaps it: a verb counts as active
        // until it has been gone for 150 ms, so nothing re-triggers on that flicker
        foreach (var v in verbs) if (v > 0) _seen[Verbs.Name(v)] = now;
        foreach (var (name, t) in _seen) if (now - t <= 150) _now.Add(name);
        foreach (var v in _now) if (!_before.Contains(v)) _since[v] = now;
        float speed = Math.Max(s.SpeedH, _posVel * 0.9f); // animation-driven moves (push, crawl) show only in the position

        // ---- one-shot reactions on entering a verb ----
        if (hx != null)
        {
            if (Entered("Rolling")) hx.Play(Fx.Roll);
            if (Entered("BackFlip")) hx.Play(Fx.Flip, 1, 1);
            if (Entered("SideStep")) hx.Play(Fx.Flip, 0.8f, _rng.Next(1, 3));
            if (EnteredAny("Jump", "StepJump", "CliffHoldJump", "RideJump")) hx.Play(Fx.Jump);
            if (Entered("WaterJump") || EnteredAny(Dive) && !_before.Contains("Swim") || Entered("InterruptWet")) hx.Play(Fx.Splash);
            if (Entered("Swim") && !HasAny(Dive) && !_before.Any(b => Dive.Contains(b))) hx.Play(Fx.Splash, 0.7f);
            if (Entered("Clamber")) { hx.Play(Fx.Grip, 1, 1, -0.7f); hx.Play(Fx.Lift); }
            if (EnteredAny("JumpAttack", "FallAttack")) hx.Play(Fx.Slam);
            if (Entered("ThrustAttack")) hx.Play(Fx.Thrust);
            if (EnteredAny("DashAttack", "LargeAttack")) hx.Play(Fx.Swing, 1.2f, 1);
            if (EnteredAny("LockOnWait", "LockOnMove") && !_before.Any(b => b.StartsWith("LockOn"))) hx.Play(Fx.LockOn, 1, 1, -0.4f);
            if (Entered("OpenDoor")) hx.Play(Fx.DoorOpen);
            if (Entered("BootsChange")) hx.Play(Fx.BootsClunk);
            if (EnteredAny("Lift", "PreLift")) hx.Play(Fx.Lift);
            if (Entered("Throw")) hx.Play(Fx.Throw);
            if (Entered("Put")) hx.Play(Fx.Put);
            if (Entered("UseEmptyBottle")) hx.Play(Fx.Bottle);
            if (Entered("UseDinFire")) hx.Play(Fx.DinFire);
            if (Entered("UseNayruLove")) { hx.Play(Fx.Nayru); _nayruUntil = now + 8000; }
            if (Entered("UseWarpItem")) hx.Play(Fx.Farore);
            if (EnteredAny("HookshotShoot", "UseHookshot")) hx.Play(Fx.HookshotFire);
            if (Entered("InterruptBoomerang")) hx.Play(Fx.Boomerang);
            if (Entered("BoomerangCatch")) hx.Play(Fx.BoomerangCatch);
            if (EnteredAny("InterruptGuard", "InterruptCrouchGuard", "InterruptNayruLoveGuard")) { hx.Play(Fx.ShieldBlock, 1, 1, -0.6f); Kick = now + 140; }
            if (EnteredAny("InterruptGuardJustRepelled", "InterruptGuardAndRiposte")) { hx.Play(Fx.Perfect, 1, 1, -0.5f); Kick = now + 100; FlashColor = (255, 255, 255); FlashUntil = now + 150; }
            if (EnteredAny("InterruptGuardBreak", "InterruptNayruLoveGuardBreak")) { hx.Play(Fx.GuardBreak); Jolt = now + 250; }
            if (Entered("InterruptRepelled")) { hx.Play(Fx.Repelled, 1, 1, 0.5f); RTick = now + 120; }
            if (EnteredAny("InterruptSmallHit")) HitScale = 0.7f;
            if (EnteredAny("InterruptMediumHit", "InterruptStealthHit")) HitScale = 1.0f;
            if (EnteredAny("InterruptHugeHit", "InterruptSmash", "InterruptCriticalHit")) { HitScale = 1.4f; hx.Play(Fx.Down); }
            if (EnteredAny("InterruptDown", "InterruptDownDamage", "InterruptThrown", "InterruptThrownToOnGround")) hx.Play(Fx.Down);
            if (EnteredAny("InterruptBigFall", "InterruptFallDamage")) { hx.Play(Fx.Land, 1.3f); Jolt = now + 150; }
            if (Entered("InterruptStagger")) hx.Play(Fx.Stagger);
            if (EnteredAny("InterruptStruggle", "Struggle")) hx.Play(Fx.Struggle);
            if (Entered("InterruptAbyssFall")) { hx.Play(Fx.Abyss); _fadeAbyss = now + 1600; }
            if (EnteredAny("InterruptFrozenBreak", "BreakFrozen")) hx.Play(Fx.Shatter);
            if (Entered("InterruptLightArrow")) { FlashColor = (255, 250, 200); FlashUntil = now + 500; hx.Play(Fx.Chime); }
            if (Entered("InterruptOcarinaNotice")) hx.Play(Fx.Chime);
            if (EnteredAny("InterruptSongOfTheStormNoticeOutside", "InterruptSongOfTheStormNoticeInside")) { hx.Play(Fx.Thunder); _rainUntil = now + 90000; FlashColor = (230, 230, 255); FlashUntil = now + 200; q.Pulse(now, 6, 6, 30, 400, 0, true, true); }
            // the sword comes over your shoulder into your hand: hiss in the palms, then R2 closes on the grip
            if (Entered("Drawn")) { hx.Play(Fx.SwordDraw); q.Pulse(now, 0, 5, 60, 70, 200); }
            if (Entered("Sheathed")) { hx.Play(Fx.Sheathe); q.Pulse(now, 0, 3, 80, 40, 120); }
            if ((Has("Talk") || anims.Count > 0 && anims[0].name.StartsWith("EvtTalk")) && (buttons & 4) != 0 && (prevButtons & 4) == 0) hx.Play(Fx.TextTick);
            if (EnteredAny("JumpAttack", "FallAttack")) q.Pulse(now, 8, 8, 35, 150, 200);   // the blow lands through the fingers
            if (Entered("ThrustAttack")) q.Pulse(now, 2, 7, 70, 60);
            if (Entered("Rolling")) { q.Pulse(now, 4, 0, 40, 60, 0); q.Pulse(now, 0, 4, 40, 60, 150); }  // over the front, round the back
            if (EnteredAny("InterruptGuard", "InterruptCrouchGuard")) q.Pulse(now, 8, 3, 35, 140, 0, true, true);
            if (Entered("InterruptRepelled")) q.Pulse(now, 0, 8, 90, 90);
            if (Entered("OpenDoor")) q.Pulse(now, 4, 4, 25, 700, 0, false, true);

            // ---- v2: the remake's moves ----
            if (Entered("Dash")) { hx.Play(Fx.Dash); q.Pulse(now, 3, 3, 45, 90); }
            if (Entered("DashUp")) { hx.Play(Fx.Jump, 1.1f); hx.Play(Fx.Dash, 0.7f); }
            if (Entered("DodgeAttack")) { int d = _rng.Next(1, 3); hx.Play(Fx.Dodge, 1, d); q.Pulse(now, d == 2 ? 4 : 0, d == 1 ? 4 : 0, 80, 60); }
            if (Entered("BackStep")) { hx.Play(Fx.Dodge, 0.9f, _rng.Next(1, 3)); q.Pulse(now, 2, 2, 60, 50, 60); } // away from the front
            if (Entered("WallJump"))
            {   // kicking off one wall, then the other
                _wallSide = -_wallSide; hx.Play(Fx.WallKick, 1, 1, 0.7f * _wallSide);
                q.Pulse(now, _wallSide < 0 ? 6 : 0, _wallSide > 0 ? 6 : 0, 60, 70);
            }
            if (Entered("WallSlip")) hx.Play(Fx.Slip, 0.7f, _rng.Next(1, 3));
            if (EnteredAny("ContinuousJump01", "ContinuousJump02", "TurnJump"))
            {   // a chain of jumps climbs like a scale
                _chain = now - _lastChain < 1500 ? Math.Min(_chain + 1, 6) : 1; _lastChain = now;
                hx.Play(Fx.ChainJump, 1, _chain); q.Pulse(now, 1 + _chain, 1 + _chain, 60 + 20 * _chain, 50);
            }
            if (Entered("SlideLand")) hx.Play(Fx.SlideLand);
            if (Entered("QuickTurn")) hx.Play(Fx.QuickTurn, 1, _rng.Next(1, 3));
            if (Entered("JumpToCeiling")) hx.Play(Fx.Jump, 1.1f);
            if (Entered("JumpOffCeiling")) hx.Play(Fx.Flip, 0.8f);
            if (Entered("RideJumpAttack")) { hx.Play(Fx.Slam, 1.2f); q.Pulse(now, 8, 8, 35, 150, 200); }
            // accidents
            if (Entered("InterruptCrashedByObject")) { hx.Play(Fx.Crash); q.Pulse(now, 8, 8, 35, 140); Jolt = now + 120; FlashColor = (255, 255, 255); FlashUntil = now + 120; }
            if (Entered("InterruptSlip")) hx.Play(Fx.Slip, 1, _rng.Next(1, 3));
            if (EnteredAny("InterruptFallFromRidable", "ForceDismount")) { hx.Play(Fx.Down, 1.2f); q.Pulse(now, 5, 5, 35, 120); }
            if (Entered("InterruptDrown")) hx.Play(Fx.DeathDrown, 0.8f);
            // items and tools
            if (Entered("InterruptHammer")) { hx.Play(Fx.Hammer); q.Pulse(now, 8, 8, 30, 160); RTick = now + 140; Jolt = now + 80; }
            if (Entered("PickUp")) hx.Play(Fx.PickUp);
            if (Entered("LiftDrop")) hx.Play(Fx.Put, 1.2f);
            if (Entered("PutAway")) hx.Play(Fx.Sheathe, 0.7f);
            if (Entered("UseScrubBusinessSeed")) hx.Play(Fx.Put, 0.8f);
            if (EnteredAny("AttachChemical", "InterruptLightChemical")) { hx.Play(Fx.Ignite); FlashColor = (255, 140, 40); FlashUntil = now + 300; }
            if (Entered("Fishing")) hx.Play(Fx.Cast);
            if (Entered("ChangeLure")) hx.Play(Fx.PickUp);
            // the shield
            if (Entered("InterruptGuardFaile")) { hx.Play(Fx.GuardBreak, 0.8f); Jolt = now + 150; }
            if (Entered("InterruptGuardBeam")) { hx.Play(Fx.ShieldBlock, 1.1f, 1, -0.6f); Kick = now + 300; FlashColor = (255, 255, 200); FlashUntil = now + 200; }
            // grabbed (Like Like, ReDead, Wallmaster) and frozen by a scream
            if (HasAny("InterruptRestraintRideOnPlayer", "InterruptRestraintRideOnEnemy", "InterruptRestraintRideOnSword")) GrabbedUntil = Math.Max(GrabbedUntil, now + 300);
            if (Has("InterruptTerror")) TerrorUntil = Math.Max(TerrorUntil, now + 300);
        }
        // being held: a clamp at once, every button you mash jerks against it, letting go snaps free
        bool grabbed = now < GrabbedUntil, terror = now < TerrorUntil;
        if (grabbed && !_wasGrabbed) { hx?.Play(Fx.Grabbed); GrabPresses = 0; q.Pulse(now, 8, 8, 30, 120); }
        if (grabbed && (buttons & ~prevButtons & 0x3F) != 0)
        {
            GrabPresses++; bool left = GrabPresses % 2 == 0;
            hx?.Play(Fx.Struggle, 0.6f); q.Pulse(now, left ? 6 : 0, left ? 0 : 6, 60, 70);
        }
        if (grabbed && EnteredAny("ReleaseRestraint", "Unstick")) { GrabbedUntil = 0; grabbed = false; }
        if (!grabbed && _wasGrabbed) hx?.Play(Fx.Breakfree); // once, however the grip ended
        if (terror && !_wasTerror) { hx?.Play(Fx.Terror); FlashColor = (150, 150, 160); FlashUntil = now + 250; }
        if (!terror && _wasTerror) hx?.Play(Fx.Breakfree, 0.7f);
        if (terror) q.Pulse(now, 2, 2, 220, 30, 0, false, true);
        _wasGrabbed = grabbed; _wasTerror = terror;
        // events (dialogue, cutscenes) play "Evt..." animations and run with no action verb at all
        InEvent = anims.Count > 0 && anims[0].name.StartsWith("Evt");
        DuckSound = HasAny("Talk", "Listen") || InEvent;
        if (_rainUntil > 0 && now > _rainUntil) _rainUntil = 0;

        // elemental states hold a little after the verb ends (they flicker between layers)
        if (Has("InterruptCombust")) _burnUntil = now + 400;
        if (HasAny("InterruptFrozen", "FrozenInIce", "FeelCold")) _freezeUntil = now + 400;
        if (HasAny("InterruptShock", "InterruptParalyze", "InterruptSleepParalysis")) _shockUntil = now + 300;
        if (HasAny("InterruptEaten")) _squeezeUntil = now + 300;
        if (HasAny("InterruptStrongGust", "InterruptWeakGust", "InterruptGust")) _windUntil = now + 300;

        bool swim = Has("Swim"), under = HasAny(Dive), climb = HasAny("Climb", "ClimbLadder"), cliff = Has("CliffHold");
        bool push = Has("GrabPushPull"), lift = Has("Lift"), crawl = Has("Crawl"), slide = HasAny("Slide", "SlippingDown");
        // the ocarina shows as the PlayMusic verb or only as the DemoOcarina animation (seen in play)
        bool ride = Has("Ride"), music = Has("PlayMusic") || anims.Count > 0 && anims[0].name.Contains("Ocarina"), talk = HasAny("Talk", "Listen"), sleep = Has("Sleep"), fishing = Has("Fishing");
        bool hook = HasAny("HookshotShoot", "UseHookshot"), falling = Has("Fall") && s.Vy < -4f;
        bool burn = now < _burnUntil, frozen = now < _freezeUntil, shock = now < _shockUntil, squeeze = now < _squeezeUntil, wind = now < _windUntil;

        // ---- breath: without the Zora tunic a dive gets harder the longer it lasts ----
        bool zora = armor.Contains("Zora");
        if (under) { if (_diveStart < 0) _diveStart = now; }
        else if (_diveStart >= 0) { if (!zora && now - _diveStart > 6000) hx?.Play(Fx.AirGasp); _diveStart = -1; }
        float diveSec = _diveStart >= 0 ? (now - _diveStart) / 1000f : 0;
        float pressure = zora ? 0.4f : Math.Min(1f, diveSec / 20f);
        AirLeft = under && !zora ? Math.Max(0, 1 - diveSec / 20f) : -1; // the player LEDs count the breath down

        // ---- continuous textures ----
        if (hx != null)
        {
            var L = hx.Loops;
            L[(int)Loop.Swim] = swim && !under ? 0.5f + Math.Min(0.5f, speed / 4f) : 0;
            hx.SwimRate = 0.5f + speed * 0.25f;
            L[(int)Loop.Underwater] = under ? 0.6f + 0.7f * pressure : 0;
            hx.Muffle = under;
            // each stroke reaches forward: the trigger on the side the wave starts gets a soft pull of water
            if (swim && !under)
            {
                long stroke = (long)Math.Floor(hx.SwimPhase);
                if (stroke != _lastStroke) { _lastStroke = stroke; bool odd = stroke % 2 == 1; q.Pulse(now, odd ? 0 : 3, odd ? 3 : 0, 25, 260, 0, true, true); }
            }
            L[(int)Loop.FallWind] = falling ? Math.Clamp((-s.Vy - 4f) / 12f, 0.1f, 1f) : 0;
            L[(int)Loop.Burn] = burn ? 1 : 0;
            L[(int)Loop.Frozen] = frozen ? 1 : 0;
            L[(int)Loop.Shock] = shock ? 1 : 0;
            L[(int)Loop.Squeeze] = squeeze ? 1 : 0;
            L[(int)Loop.Wind] = wind ? 0.8f : 0;
            L[(int)Loop.Strain] = cliff ? Math.Min(1f, 0.3f + Held("CliffHold", now) / 4000f) : 0;
            L[(int)Loop.Grind] = push && speed > 0.05f ? 0.7f : push ? 0.15f : 0;
            L[(int)Loop.Slide] = slide ? 0.8f : 0;
            L[(int)Loop.Crawl] = crawl && speed > 0.1f ? 0.7f : 0;
            hx.LoopSpeed = speed;
            L[(int)Loop.Hooves] = ride && speed > 1 ? Math.Min(1f, 0.35f + speed / 14f) : 0;
            hx.HoofRate = Math.Clamp(speed / 4.5f, 1.1f, 4f);
            bool outside = !w.Indoor;
            L[(int)Loop.Rain] = _rainUntil > 0 && outside ? 0.8f : 0;
            hx.RainRoof = w.UnderRoof;

            // ocarina: each note button sounds in the hands while held, and the note has a place: low D in the
            // palms, F in L2, A in R2, B in both triggers softly, high D in both triggers; the triggers hum its pitch
            float note = 0;
            if (music)
                foreach (var (btn, hz, col) in Notes)
                    if ((buttons & btn) != 0)
                    {
                        note = 1; hx.NoteHz = hz;
                        if ((prevButtons & btn) == 0) { hx.Play(Fx.NoteOn); _noteBtn = btn; _noteColor = col; _noteFlashUntil = now + 350; }
                        int f = (int)Math.Min(255, hz);
                        var (al, ar) = btn switch { 16 => (5, 0), 32 => (0, 5), 1 => (3, 3), 8 => (5, 5), _ => (0, 0) };
                        q.Pulse(now, al, ar, f, 40, 0, false);
                    }
            L[(int)Loop.Note] = note;

            // climbing: one grip per hand movement, hand over hand. Link's body velocity is ~0 while climbing
            // (the animation moves him), so this goes by his real position: each pull is a burst of movement,
            // and continuous climbing gives a grip every ~22 cm; the animation looping back is one more cue.
            float dt = Math.Clamp((now - _lastT) / 1000f, 0.001f, 0.1f);
            float dx = s.X - _lastX, dy = s.Y - _lastY, dz = s.Z - _lastZ;
            float moved = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (moved > 2) moved = 0; // a warp, not a step
            _posVel = _posVel * 0.6f + moved / dt * 0.4f;
            if (moved > 0 || dt > 0) _posVy = _posVy * 0.7f + (Math.Abs(dy) < 2 ? dy / dt : 0) * 0.3f;

            // hookshot / longshot flight: the chain pulls you fast toward the target, then you hit it
            if (hook) _hookT = now; // stays armed while it is held: the pull can come after any amount of aiming
            bool flying = now - _hookT < 2500 && _posVel > 6f;
            L[(int)Loop.Wind] = Math.Max(L[(int)Loop.Wind], flying ? 0.9f : 0);
            if (flying && !_flying)
            {   // the hook bites: a snap under R2, then the pull runs from the fingertips back into the palms
                hx.Play(Fx.Bite, 0.8f, 1, 0.4f); hx.Play(Fx.ChainRattle, 1.3f); q.Pulse(now, 0, 8, 120, 60); q.Pulse(now, 5, 5, 40, 200, 80, true, true);
            }
            if (_flying && flying && now >= _nextRatchet) { q.Pulse(now, 4, 5, 160, 50, 0, false, true); _nextRatchet = now + 110; } // chain links ratcheting through the hand
            if (flying) _flying = true;
            else if (_flying && _posVel < 3f) { _flying = false; hx.Play(Fx.Arrive, 1.1f); q.Pulse(now, 6, 6, 45, 110); hx.Duck(250); }
            Flying = _flying;

            // a moving platform / lift: Link stands still (no stick, no action) but the world carries him
            bool idleVerb = verbs.All(v => v <= 0 || Verbs.Name(v) is "Wait" or "Drawn" or "Sheathed" or "None");
            bool carried = stickIdle && idleVerb && _posVel > 0.35f && _posVel < 8f && !swim && !under && !climb;
            _carryT = carried ? (_carryT < 0 ? now : _carryT) : -1;
            bool riding = _carryT >= 0 && now - _carryT > 250;
            Carried = riding;
            L[(int)Loop.Motor] = riding ? Math.Min(1f, 0.35f + _posVel / 3f) : 0;
            hx.MotorUp = Math.Clamp(_posVy / 2f, -1, 1);
            if (riding) _rodeFor = now - _carryT;
            else if (_rodeFor > 1000) { hx.Play(Fx.Arrive, 0.7f); _rodeFor = 0; } else _rodeFor = 0;

            // ice: the ground slides away under you; quicksand: a slow pull down
            bool icy = map.Contains("IceCavern", StringComparison.OrdinalIgnoreCase) || map.Contains("Ice_", StringComparison.OrdinalIgnoreCase);
            Slippery = icy && speed > 1 && !swim && !climb;
            L[(int)Loop.Slide] = Math.Max(L[(int)Loop.Slide], Slippery ? 0.35f : 0);
            bool sandy = map.Contains("Wasteland", StringComparison.OrdinalIgnoreCase) || map.Contains("Hounted", StringComparison.OrdinalIgnoreCase);
            Sinking = sandy && _posVy < -0.05f && _posVy > -1.5f && !HasAny("Fall", "Jump");
            L[(int)Loop.Sink] = Sinking ? 0.9f : 0;
            if (climb)
            {
                bool moving = _posVel > 0.12f;
                _climbTravel += moved;
                float frame = anims.Count > 0 ? anims[0].frame : 0;
                bool wrapped = frame < _lastFrame - 2;
                bool grip = (moving && !_climbMoving) || _climbTravel > 0.22f || (wrapped && moving);
                if (Entered("Climb") || Entered("ClimbLadder")) { grip = true; _gripSide = 1; }
                if (grip && now - _lastGrip > 130)
                {
                    hx.Play(Fx.Grip, 1, 1, 0.8f * _gripSide);
                    q.Pulse(now, _gripSide < 0 ? 4 : 0, _gripSide > 0 ? 4 : 0, 110, 45, 0, true, true);
                    _gripPulseUntil = now + 150; _gripSide = -_gripSide; _climbTravel = 0; _lastGrip = now;
                }
                _climbMoving = moving; _lastFrame = frame;
            }
            else { _climbTravel = 0; _climbMoving = false; }
            _lastX = s.X; _lastY = s.Y; _lastZ = s.Z; _lastT = now;

        }

        // ---- triggers ----
        byte[]? r = null, l = null; bool free = false;
        if (swim && !under) { r = l = speed > 3.5f ? Effects.Vibration(0, 3, 25) : Effects.Feedback(0, 2); }
        if (under) r = l = Effects.Feedback(0, Math.Clamp(3 + (int)(5 * pressure), 3, 8)); // the deeper in breath, the heavier
        if (climb)
        {
            r = l = Effects.Feedback(0, 5);
            // the hand that just gripped (the side already flipped for the next grip) pulls its trigger hard
            if (now < _gripPulseUntil) { if (_gripSide < 0) r = Effects.Feedback(0, 8); else l = Effects.Feedback(0, 8); }
        }
        if (cliff) r = l = Effects.Feedback(0, 7);
        if (push) r = l = Effects.Feedback(0, 8);
        if (lift) r = l = Effects.Feedback(0, 6);
        if (crawl) r = l = Effects.Feedback(0, 3);
        if (slide) r = l = Effects.Vibration(0, 4, 40);
        if (falling) r = l = Effects.Vibration(0, Math.Clamp(1 + (int)((-s.Vy - 4) / 3), 1, 6), 50);
        // holding the hookshot: the chain just sits loaded under R2; the strong chain feel belongs to the pull itself
        if (hook) r = Effects.Feedback(1, 3);
        if (Flying) r = l = Effects.Machine(0, 9, 7, 3, 40, 1);              // the chain reels you in, hard
        if (Slippery) r = l = Effects.Feedback(0, 1);                         // nothing to hold on to
        if (Sinking) { int p = 3 + (int)(4 * Math.Min(1, (now % 4000) / 4000f)); r = l = Effects.Feedback(0, p); }
        if (fishing) r = Effects.Feedback(2, 4); // the line's tension; a real bite comes from Moments (no more random fake nibbles)
        if (burn) r = l = Effects.Vibration(0, 5, 90);
        if (frozen) r = l = Effects.Feedback(0, 8);
        if (shock) r = l = Effects.Vibration(0, 8, 120);
        if (squeeze) { int p = 3 + (int)(5 * (0.5 + 0.5 * Math.Sin(now / 1000.0 * 2 * Math.PI * 1.2))); r = Effects.Feedback(0, p); l = Effects.Feedback(0, 11 - p); }
        Busy = music || talk || sleep || InEvent;
        HandsBusy = climb || cliff || crawl || push || lift || swim && !under || hook || Flying;
        if (Busy) free = true;
        if (grabbed || terror) { r = l = Effects.Feedback(0, 8); free = false; } // held fast / frozen: the triggers lock

        // ---- light ----
        (float, float, float)? light = null; float mix = 0;
        double ts = now / 1000.0;
        if (swim && !under)
        {   // caustics: bright ripples moving through blue
            float c = 0.5f + 0.25f * (float)Math.Sin(ts * 5.1) * (float)Math.Sin(ts * 3.3 + 1) + 0.25f * (float)Math.Sin(ts * 7.7 + 2);
            light = (20 + 60 * c, 120 + 110 * c, 220 + 35 * c); mix = 0.75f;
        }
        if (under)
        {
            float sparkle = _rng.NextDouble() < 0.04 ? 1f : 0f;
            light = (5 + 100 * sparkle, 35 + 120 * sparkle, 150 + 100 * sparkle); mix = 0.9f;
        }
        if (burn) { float f = 0.6f + 0.4f * (float)_rng.NextDouble(); light = (255 * f, 90 * f, 0); mix = 0.95f; }
        if (frozen) { light = (170, 230, 255); mix = 0.9f; }
        if (shock) { bool on = (int)(ts * 15) % 2 == 0; light = on ? (255, 255, 200) : (60, 60, 20); mix = 0.95f; }
        if (squeeze) { float p = 0.5f + 0.5f * (float)Math.Sin(ts * 2 * Math.PI * 1.2); light = (120 * p + 40, 30, 140 * p + 30); mix = 0.85f; }
        if (now < _nayruUntil) { float p = 0.7f + 0.3f * (float)Math.Sin(ts * 9); light = (80 * p, 220 * p, 255 * p); mix = 0.6f; }
        if (HasAny("LockOnWait", "LockOnMove")) { light = (255, 230, 120); mix = 0.25f; }            // Navi's glow on the target
        if (HasAny("ShowItem", "HoldItem")) { float sp = _rng.NextDouble() < 0.15 ? 1f : 0.7f; light = (255 * sp, 210 * sp, 60 * sp); mix = 0.8f; }
        if (under && !zora && diveSec > 10) { float p = (float)(0.5 + 0.5 * Math.Sin(ts * 2 * Math.PI * (0.8 + diveSec / 15))); light = (40 + 120 * p * pressure, 30, 150); mix = 0.9f; }
        if (music) { light = now < _noteFlashUntil ? _noteColor : (180, 140, 40); mix = now < _noteFlashUntil ? 1f : 0.5f; }
        if (talk || InEvent) { light = (110, 90, 70); mix = 0.6f; }
        if (sleep) { float b = 0.15f + 0.15f * (float)Math.Sin(ts * 2 * Math.PI * 0.2); light = (60 * b * 3, 50 * b * 3, 120 * b * 3); mix = 0.9f; }
        if (now < _fadeAbyss) { light = (0, 0, 0); mix = 1 - (_fadeAbyss - now) / 1600f + 0.3f; }
        if (grabbed) { float p = 0.5f + 0.5f * (float)Math.Sin(ts * 2 * Math.PI * 1.5); light = (110 * p + 40, 0, 50 * p + 20); mix = 0.85f; }
        if (terror) { light = (90, 90, 100); mix = 0.95f; }

        return new Out(r, l, light, Math.Clamp(mix, 0, 1), free, swim || under);
    }

    // signals for the daemon's trigger and light logic
    public long Kick, Jolt, RTick, FlashUntil;
    public (byte, byte, byte) FlashColor;
    public float HitScale = 1;
    public bool DuckSound;           // talking: voices would buzz through the sound layer
    public bool InEvent;             // dialogue / cutscene
    public bool Busy;                // ocarina, dialogue, cutscene, sleep: no sword, bow or sonar feel
    public bool HandsBusy;           // climbing, hanging, crawling, pushing, carrying, swimming, hookshot: Square is no sword swing
    long _diveStart = -1, _lastStroke = long.MinValue, _lastGrip, _lastT;
    float _lastX, _lastY, _lastZ, _posVel, _lastFrame; bool _climbMoving;
    public float PosSpeed => _posVel; // real movement speed from position (m/s)
    public float AirLeft = -1;        // breath left underwater without the Zora tunic (0..1), -1 = not diving
    public bool Flying, Slippery, Sinking;
    float _posVy; long _hookT = -100000, _carryT = -1, _rodeFor; bool _flying;
    int _wallSide = 1, _chain; long _lastChain, _nextRatchet;
    public long GrabbedUntil, TerrorUntil; public int GrabPresses; bool _wasGrabbed, _wasTerror;
    public bool Carried;              // standing on something that moves (platform, ship)
    public bool Has(int verb) => _now.Contains(Verbs.Name(verb));
    public bool HasVerb(string v) => _now.Contains(v);

    public string Debug => string.Join(",", _now);
}
