namespace PulseOfTime;

/// The place Link is in (GameData Sequence_CurrentBanc, e.g. "Market_Child", "FireTemple"): a lightbar
/// mood that moves like the place, and rare, quiet touches of its ambience in the hands.
sealed class Scene
{
    public enum Theme { None, Fire, Water, Organic, Forest, Shadow, Desert, Ice, Ganon, Holy, Dream }

    public Theme Current { get; private set; }
    public string Map { get; private set; } = "";
    readonly Random _rng = new(7);
    long _nextAmbient, _sparkleUntil;
    float _flicker = 1;

    public void SetMap(string map, long now)
    {
        if (map == Map) return;
        Map = map;
        Current = Classify(map);
        _nextAmbient = now + 3000;
    }

    static Theme Classify(string m)
    {
        bool Has(params string[] k) => k.Any(x => m.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (Has("Dream")) return Theme.Dream;
        if (Has("Ganon", "FinalBattle")) return Theme.Ganon;
        if (Has("FireTemple", "DeathMountainCrater", "Crater", "DodongoCavern", "Volvagia")) return Theme.Fire;
        if (Has("JabuJabu")) return Theme.Organic;
        if (Has("IceCavern", "Ice")) return Theme.Ice;
        if (Has("WaterTemple", "Zora", "LakeHylia", "Lake")) return Theme.Water;
        if (Has("ShadowTemple", "BottomOfTheWell", "Well", "Graveyard")) return Theme.Shadow;
        if (Has("SpiritTemple", "Colossus", "Wasteland", "Gerudo", "Desert")) return Theme.Desert;
        if (Has("ForestTemple", "LostWood", "Kokiri", "SacredForest", "DekuTree", "Forest")) return Theme.Forest;
        if (Has("TempleOfTime", "ShrineOfTime", "DoorOfTime")) return Theme.Holy;
        return Theme.None;
    }

    /// The place's colour right now (animated), or null for "use the tunic colour".
    public (float R, float G, float B)? Light(long now)
    {
        double t = now / 1000.0;
        switch (Current)
        {
            case Theme.Fire: // torchlight: a random walk of brightness
                _flicker = Math.Clamp(_flicker + (float)(_rng.NextDouble() - 0.5) * 0.25f, 0.55f, 1f);
                return (255 * _flicker, 85 * _flicker, 8 * _flicker);
            case Theme.Water:
            {
                float w = 0.65f + 0.35f * (float)Math.Sin(2 * Math.PI * 0.4 * t);
                return (10 * w, (80 + 60 * w) * w, 255 * w);
            }
            case Theme.Organic:
            {
                float p = 0.45f + 0.55f * (float)Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * t / 1.6)), 4);
                return (210 * p, 40 * p, 70 * p);
            }
            case Theme.Forest:
            {
                if (now > _sparkleUntil && _rng.NextDouble() < 0.003) _sparkleUntil = now + 90; // a firefly
                if (now < _sparkleUntil) return (200, 255, 80);
                float b = 0.7f + 0.3f * (float)Math.Sin(2 * Math.PI * 0.25 * t);
                return (20 * b, 200 * b, 60 * b);
            }
            case Theme.Shadow:
            {
                float b = 0.35f + 0.15f * (float)Math.Sin(2 * Math.PI * 0.17 * t) + 0.05f * (float)_rng.NextDouble();
                return (90 * b, 0, 150 * b);
            }
            case Theme.Desert:
            {
                float b = 0.8f + 0.2f * (float)Math.Sin(2 * Math.PI * 1.3 * t) * (float)Math.Sin(2 * Math.PI * 0.21 * t);
                return (235 * b, 160 * b, 40 * b);
            }
            case Theme.Ice:
            {
                if (now > _sparkleUntil && _rng.NextDouble() < 0.006) _sparkleUntil = now + 60;
                return now < _sparkleUntil ? (255, 255, 255) : (110, 210, 255);
            }
            case Theme.Ganon:
            {
                float p = 0.4f + 0.6f * (float)Math.Pow(0.5 + 0.5 * Math.Sin(2 * Math.PI * 0.3 * t), 3);
                return (160 * p, 0, 12 * p);
            }
            case Theme.Holy: return (255, 225, 160);
            case Theme.Dream:
            {
                double h = t * 0.05 % 1;
                return (150 + 80 * (float)Math.Sin(2 * Math.PI * h), 50, 255);
            }
            default: return null;
        }
    }

    /// Picks the next ambient touch for the place, if one is due: (effect, pan) or null.
    public (Fx fx, float pan)? Ambient(long now)
    {
        if (now < _nextAmbient || Current == Theme.None) return null;
        (Fx fx, int min, int max)? a = Current switch
        {
            Theme.Fire => (Fx.AmbLava, 3000, 7000),
            Theme.Water => (Fx.AmbDrip, 2000, 5000),
            Theme.Organic => (Fx.AmbOrganic, 1600, 1600),
            Theme.Shadow => (Fx.AmbEerie, 8000, 14000),
            Theme.Desert => (Fx.AmbSand, Map.Contains("Wasteland") ? 2000 : 4000, Map.Contains("Wasteland") ? 4000 : 9000),
            Theme.Ice => (Fx.AmbCrystal, 3000, 6000),
            Theme.Ganon => (Fx.AmbThunder, 12000, 25000),
            _ => null,
        };
        if (a is not { } x) return null;
        _nextAmbient = now + _rng.Next(x.min, x.max + 1);
        return (x.fx, (float)(_rng.NextDouble() * 1.6 - 0.8));
    }
}
