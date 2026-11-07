using System.Collections.Generic;

// The game decides which actions exist and which are allowed right now (PDF §4-5).
// These are deterministic rules only; the tactical choice among them is left to the AI.
public static class LegalActionGenerator
{
    public const string Attack = "ATTACK", TakeCover = "TAKE_COVER", Retreat = "RETREAT",
        CallForHelp = "CALL_FOR_HELP", Reload = "RELOAD", HoldPosition = "HOLD_POSITION";

    public static List<string> Generate(NpcState s)
    {
        var legal = new List<string>();
        if (s.ammo > 0 && s.enemyCount > 0) legal.Add(Attack);
        if (s.coverAvailable) legal.Add(TakeCover);
        legal.Add(Retreat);
        if (s.alliesCallable > 0) legal.Add(CallForHelp);
        if (s.ammo < s.maxAmmo) legal.Add(Reload);
        legal.Add(HoldPosition);
        return legal;
    }
}
