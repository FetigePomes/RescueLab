using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
public class VehicleSeatController : MonoBehaviour
{
    [Header("Assign on Vehicle")]
    [Tooltip("Empty at the driver seat where the player is parented while seated.")]
    public Transform driverSeat;

    [Tooltip("Where the player appears after exiting (place on walkable ground).")]
    public Transform exitPoint;

    [Tooltip("BoxCollider near the door; rotation & scale are respected.")]
    public BoxCollider enterTrigger;

    [Header("Input/Filter")]
    public KeyCode useKey = KeyCode.E;
    [Tooltip("Only colliders with this tag are considered players.")]
    public string playerTag = "Player";
    [Tooltip("Optional layer filter for OverlapBox (leave as Everything to ignore).")]
    public LayerMask playerLayers = ~0;

    [Header("Driving/Selection")]
    [Tooltip("Vehicle AI is enabled only while someone is seated.")]
    public SimpleCarAI_NavMesh carAI;

    GameObject driver;
    static readonly Collider[] hitsBuf = new Collider[16];

    void Awake()
    {
        if (!carAI) TryGetComponent(out carAI);
    }

    void Start()
    {
        SetDriving(false);
        SetSelected(false);
    }

    void Update()
    {
        if (!Input.GetKeyDown(useKey))
            return;

        if (driver == null)
        {
            // Enter only if inside the door zone
            var p = FindPlayerInZone();
            if (p) Seat(p);
        }
        else
        {
            // Exit anytime (keeps flow simple; adjust if you want "exit only in zone")
            Unseat();
        }
    }

    // ---- Seat / Unseat -------------------------------------------------------

    void Seat(GameObject player)
    {
        if (!player || !driverSeat) return;

        driver = player;

        TogglePlayerMovement(player, enable: false);

        // Parent and lock local transform to seat
        var t = player.transform;
        t.SetParent(driverSeat, worldPositionStays: false);
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale    = Vector3.one;

        SetDriving(true);
        SetSelected(true);
    }

    void Unseat()
    {
        if (!driver) return;

        var t = driver.transform;

        // Detach first (keep current pose), then place at exit
        t.SetParent(null, worldPositionStays: true);

        if (exitPoint)
        {
            t.SetPositionAndRotation(exitPoint.position, exitPoint.rotation);
            t.localScale = Vector3.one;
        }

        // Recover NavMeshAgent safely to avoid "not on NavMesh" errors
        if (driver.TryGetComponent(out NavMeshAgent agent))
        {
            if (!agent.enabled) agent.enabled = true;

            // Snap to nearest mesh around exit to keep the agent valid
            if (NavMesh.SamplePosition(t.position, out var hit, 1.5f, NavMesh.AllAreas))
            {
                agent.Warp(hit.position);
                agent.isStopped = true;
                agent.ResetPath();
            }
            else
            {
                agent.enabled = false; // clean fallback if exit is off-mesh
                Debug.LogWarning("[VehicleSeatController] ExitPoint not on NavMesh; agent disabled.");
            }
        }

        TogglePlayerMovement(driver, enable: true);

        driver = null;
        SetSelected(false);
        SetDriving(false);
    }

    // ---- Helpers -------------------------------------------------------------

    GameObject FindPlayerInZone()
    {
        if (!enterTrigger) return null;

        // Oriented box from BoxCollider in world space
        var tr     = enterTrigger.transform;
        var center = tr.TransformPoint(enterTrigger.center);
        var half   = Vector3.Scale(enterTrigger.size, tr.lossyScale) * 0.5f;
        var rot    = tr.rotation;

        int count = Physics.OverlapBoxNonAlloc(center, half, hitsBuf, rot, playerLayers, QueryTriggerInteraction.Collide);

        GameObject best = null;
        float bestSqr = float.MaxValue;
        var refPos = driverSeat ? driverSeat.position : transform.position;

        for (int i = 0; i < count; i++)
        {
            var h = hitsBuf[i];
            if (!h || !h.CompareTag(playerTag)) continue;

            float d = (h.transform.position - refPos).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = h.gameObject; }
        }
        return best;
    }

    // Enables/disables collider, click-move, and NavMeshAgent in a safe order
    void TogglePlayerMovement(GameObject player, bool enable)
    {
        if (!player) return;

        if (player.TryGetComponent(out Collider col))
            col.enabled = enable;

        if (player.TryGetComponent(out ClickMove_SimpleTurn_Refined mover))
        {
            mover.enabled = enable;
            if (mover.selectionRing) mover.selectionRing.SetActive(enable);
        }

        if (player.TryGetComponent(out NavMeshAgent agent))
        {
            if (enable)
            {
                if (!agent.enabled) agent.enabled = true;
                agent.isStopped = true;
                agent.ResetPath();
            }
            else
            {
                if (agent.enabled && agent.isOnNavMesh)
                {
                    agent.isStopped = true;
                    agent.ResetPath();
                }
                agent.enabled = false;
            }
        }
    }

    void SetDriving(bool canDrive)
    {
        if (carAI) carAI.enabled = canDrive;
    }

    void SetSelected(bool selected)
    {
        if (carAI && carAI.selectionRing)
            carAI.selectionRing.SetActive(selected);
    }

    // ---- Editor Gizmos -------------------------------------------------------

    void OnDrawGizmosSelected()
    {
        if (!enterTrigger) return;

        Gizmos.matrix = enterTrigger.transform.localToWorldMatrix;

        Gizmos.color  = new Color(0, 1, 0, 0.15f);
        Gizmos.DrawCube(enterTrigger.center, enterTrigger.size);

        Gizmos.color  = new Color(0, 1, 0, 0.8f);
        Gizmos.DrawWireCube(enterTrigger.center, enterTrigger.size);
    }
}
