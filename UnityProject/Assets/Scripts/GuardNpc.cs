using System.Collections.Generic;
using UnityEngine;
using static LegalActionGenerator;

// Guard NPC: owns the authoritative game state and carries out actions with ordinary
// gameplay code (movement, combat, cooldowns, damage). The AI only picks the action name.
public class GuardNpc : MonoBehaviour
{
    public string npcId = "guard_01";
    public float health = 100;
    public int ammo = 10, maxAmmo = 10;
    public float moveSpeed = 4, detectRange = 20, allyRange = 10, coverRange = 8;
    public float attackRange = 20, attackCooldown = 1, attackDamage = 25, reloadTime = 2;
    public Enemy[] enemies;
    public Transform[] allies, covers;

    // Personalities (PDF §16): only the goal sent to the model changes; state, legal actions and executor stay the same.
    public static readonly NpcObjective[] Roles =
    {
        new() { role = "Guard", goal = "Protect the gate and hold the area while staying alive." },
        new() { role = "Scout", goal = "Observe enemies and avoid unnecessary combat; survival matters more than kills." },
        new() { role = "Assault", goal = "Apply pressure on enemies while remaining combat-effective." },
    };
    public int roleIndex;
    public NpcObjective Objective => Roles[roleIndex];

    public string CurrentAction { get; private set; } = HoldPosition;
    public int StateVersion { get; private set; }
    public bool ActionFinished { get; set; }
    public bool IsDead => health <= 0;

    Vector3? moveTarget;
    float nextShot, reloadDone, lastHit = -99;
    string signature;
    readonly List<Transform> helpers = new();

    // ---------- State projection (PDF §3) ----------
    public NpcState GetState()
    {
        var enemy = NearestEnemy();
        int visible = 0, near = 0;
        foreach (var e in enemies) if (Alive(e) && Dist(e.transform) <= detectRange) visible++;
        foreach (var a in allies) if (Dist(a) <= allyRange) near++;
        return new NpcState
        {
            npcId = npcId, health = Mathf.Round(health), ammo = ammo, maxAmmo = maxAmmo,
            enemyDistance = enemy ? Round1(Dist(enemy.transform)) : -1,
            enemyCount = visible, nearbyAllies = near, alliesCallable = allies.Length - helpers.Count,
            coverAvailable = NearestCover() != null, currentAction = CurrentAction,
        };
    }

    public EnvironmentContext GetEnvironment()
    {
        var cover = NearestCover();
        var enemy = NearestEnemy();
        return new EnvironmentContext
        {
            nearestCoverDistance = cover ? Round1(Dist(cover)) : -1,
            isUnderFire = Time.time - lastHit < 3,
            inCover = enemy && CoverBetween(enemy.transform.position),
            weaponRange = attackRange,
            enemyWeaponRange = enemy ? enemy.fireRange : -1,
        };
    }

    void Update()
    {
        if (IsDead) return;

        // New state version only on significant changes (PDF §14), so small ticks don't make every decision stale.
        var s = GetState();
        var nearest = NearestEnemy();   // entering or leaving the enemies' weapon range also counts as significant
        bool inEnemyRange = nearest && Dist(nearest.transform) <= nearest.fireRange;
        string sig = $"{(int)s.health / 20}|{s.ammo == 0}|{s.enemyCount}|{s.nearbyAllies}|{s.alliesCallable}|{s.coverAvailable}|{inEnemyRange}";
        if (sig != signature) { signature = sig; StateVersion++; }

        if (CurrentAction == Attack) Shoot();
        if (reloadDone > 0 && Time.time >= reloadDone) { ammo = maxAmmo; reloadDone = 0; ActionFinished = true; }
        if (moveTarget is Vector3 t)
        {
            transform.position = Vector3.MoveTowards(transform.position, t, moveSpeed * Time.deltaTime);
            if (transform.position == t) { moveTarget = null; ActionFinished = true; }
        }
        foreach (var a in helpers)
            if (Dist(a) > 2.5f) a.position = Vector3.MoveTowards(a.position, transform.position, moveSpeed * Time.deltaTime);
    }

    // ---------- Action executor (PDF §10) ----------
    public void Execute(string action)
    {
        if (action == CurrentAction && !ActionFinished) return;   // same intent again: keep going, don't restart a reload or a move
        CurrentAction = action;
        moveTarget = null; reloadDone = 0; ActionFinished = false;
        switch (action)
        {
            case Attack: break;   // Shoot() runs every frame, limited by ammo, range and cooldown
            case TakeCover: moveTarget = CoverPoint(); break;
            case Retreat: moveTarget = RetreatPoint(); break;
            case CallForHelp: CallAllies(); break;
            case Reload: reloadDone = Time.time + reloadTime; break;
            case HoldPosition: break;
        }
    }

    public void ResetTo(float hp, int ammoCount)
    {
        health = hp; ammo = ammoCount;
        transform.position = new Vector3(0, 1, 0);
        helpers.Clear();
        moveTarget = null; reloadDone = 0;
        Execute(HoldPosition);
        StateVersion++;   // a new scenario invalidates any decision still in flight
    }

    public void SetRole(int i)
    {
        roleIndex = i;
        StateVersion++;   // a decision made for the old role is stale
    }

    // Right next to a cover object that is between the guard and the given position.
    bool CoverBetween(Vector3 from)
    {
        var c = NearestCover();
        return c && Dist(c) < 1.6f && Vector3.Dot(c.position - transform.position, from - transform.position) > 0;
    }

    public void TakeDamage(float dmg, Vector3 from)
    {
        if (CoverBetween(from)) return;   // cover between the guard and the shooter blocks the shot
        health = Mathf.Max(0, health - dmg);
        lastHit = Time.time;
    }

    void Shoot()
    {
        var e = NearestEnemy();
        if (!e || ammo <= 0 || Time.time < nextShot || Dist(e.transform) > attackRange) return;
        nextShot = Time.time + attackCooldown;
        ammo--;
        e.TakeDamage(attackDamage);
    }

    void CallAllies()
    {
        foreach (var a in allies) if (!helpers.Contains(a)) helpers.Add(a);
        ActionFinished = true;
    }

    Vector3? CoverPoint()   // the side of the nearest cover facing away from the enemy
    {
        var c = NearestCover();
        if (!c) return null;
        var e = NearestEnemy();
        var away = e ? c.position - e.transform.position : Vector3.back;
        away.y = 0;
        var p = c.position + away.normalized * 1.2f;
        p.y = transform.position.y;
        return p;
    }

    Vector3 RetreatPoint()
    {
        var e = NearestEnemy();
        var away = e ? transform.position - e.transform.position : Vector3.back;
        away.y = 0;
        var p = transform.position + away.normalized * 8;
        return new Vector3(Mathf.Clamp(p.x, -18, 18), p.y, Mathf.Clamp(p.z, -18, 18));
    }

    Enemy NearestEnemy()
    {
        Enemy best = null;
        foreach (var e in enemies)
            if (Alive(e) && Dist(e.transform) <= detectRange && (!best || Dist(e.transform) < Dist(best.transform))) best = e;
        return best;
    }

    Transform NearestCover()
    {
        Transform best = null;
        foreach (var c in covers)
            if (c.gameObject.activeInHierarchy && Dist(c) <= coverRange && (!best || Dist(c) < Dist(best))) best = c;
        return best;
    }

    static bool Alive(Enemy e) => e.gameObject.activeInHierarchy;
    float Dist(Transform t) => Vector3.Distance(transform.position, t.position);
    static double Round1(float v) => System.Math.Round(v, 1);   // double, so JSON shows 4.7 and not 4.699999809
}
