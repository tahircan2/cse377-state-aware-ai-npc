using UnityEngine;
using UnityEngine.InputSystem;

// Lets the user move the Player capsule with WASD / arrow keys, so the guard's state (enemy distance,
// enemies in range, cover) and therefore its decisions change live. Movement is ordinary Unity code;
// the guard only reacts through its normal decision pipeline.
public class PlayerController : MonoBehaviour
{
    public float speed = 6, arenaHalfSize = 19;

    void Update()
    {
        var k = Keyboard.current;
        if (k == null) return;   // no keyboard (e.g. batch mode): nothing to do
        var dir = new Vector3(
            (k.dKey.isPressed || k.rightArrowKey.isPressed ? 1 : 0) - (k.aKey.isPressed || k.leftArrowKey.isPressed ? 1 : 0), 0,
            (k.wKey.isPressed || k.upArrowKey.isPressed ? 1 : 0) - (k.sKey.isPressed || k.downArrowKey.isPressed ? 1 : 0));
        if (dir == Vector3.zero) return;
        var p = transform.position + dir.normalized * speed * Time.deltaTime;
        transform.position = new Vector3(Mathf.Clamp(p.x, -arenaHalfSize, arenaHalfSize), p.y, Mathf.Clamp(p.z, -arenaHalfSize, arenaHalfSize));
    }
}
