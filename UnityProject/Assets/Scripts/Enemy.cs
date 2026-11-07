using UnityEngine;

// Enemy capsule (the Player is one too). Shoots the guard when in range; disappears at 0 health.
public class Enemy : MonoBehaviour
{
    public GuardNpc guard;
    public float health = 100, fireRange = 10, fireInterval = 2, damage = 1;
    float nextFire;

    void Update()
    {
        if (guard.IsDead || Time.time < nextFire) return;
        if (Vector3.Distance(transform.position, guard.transform.position) > fireRange) return;
        nextFire = Time.time + fireInterval;
        guard.TakeDamage(damage, transform.position);
    }

    public void TakeDamage(float dmg)
    {
        health -= dmg;
        if (health <= 0) gameObject.SetActive(false);
    }
}
