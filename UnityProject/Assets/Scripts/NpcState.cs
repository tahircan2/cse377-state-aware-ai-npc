using System;

// Compact projection of the game world that the AI may see (PDF §3) - never the whole scene.
[Serializable]
public class NpcState
{
    public string npcId;
    public float health;
    public int ammo;
    public int maxAmmo;
    public double enemyDistance;   // -1 when no enemy is visible
    public int enemyCount;
    public int nearbyAllies;
    public int alliesCallable;     // allies not yet called for help
    public bool coverAvailable;
    public string currentAction;
}

[Serializable]
public class EnvironmentContext
{
    public double nearestCoverDistance;   // -1 when no cover is in range
    public bool isUnderFire;
    public bool inCover;                  // already behind cover that blocks the nearest enemy's shots
    public double weaponRange;            // how far the NPC can shoot
    public double enemyWeaponRange;       // how far the enemies can shoot (-1 when no enemy is visible)
}

// Role and tactical goal given to the model (PDF §16): same state + different goal = possibly different decision.
[Serializable]
public class NpcObjective
{
    public string role;
    public string goal;
}

// Body of POST /decide. The agent stores it in state.json, which the MCP tools read.
[Serializable]
public class DecisionRequest
{
    public int stateVersion;
    public NpcState npc;
    public string[] legalActions;
    public EnvironmentContext environment;
    public NpcObjective objective;
}

// Structured AI output (PDF §8) plus the state version it was made for (PDF §14).
[Serializable]
public class Decision
{
    public int stateVersion;
    public string action;
    public float confidence;
    public string reason;
    public string[] toolsCalled;
    public bool corrected;   // the agent rejected a reason that contradicted the facts and asked again once
}
