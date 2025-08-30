using UnityEngine;
using UnityEngine.AI;

//
// VehicleSeatInteractor
// ---------------------
// Purpose:
// - Let the player enter/exit the vehicle with a single key (E) *only* when standing inside
//   a designated door zone (a BoxCollider placed next to the door).
// - When seated, the player's NavMesh + click-move are disabled; the player is parented to
//   the DriverSeat transform and visually locked in place.
// - When exiting, the player is moved to ExitPoint and their movement is safely re-enabled.
// - The vehicle can only drive while a player is seated. We enforce this by enabling/disabling
//   your SimpleCarAI_NavMesh component.
// - We also auto-"select" the vehicle by toggling the AI's selection ring (this is how your
//   SimpleCarAI_NavMesh determines isSelected).
//
// Key design choices explained inline below.
//
[DisallowMultipleComponent]
public class VehicleSeatInteractor : MonoBehaviour
{
    [Header("Assign on Vehicle")]
    [Tooltip("Where the player should be parented while seated (use an empty GameObject at the driver seat location).")]
    public Transform driverSeat;          // DriverPositionEmpty

    [Tooltip("Where the player should appear when exiting the vehicle (place on walkable ground).")]
    public Transform exitPoint;           // ExitPositionEmpty

    [Tooltip("Door use zone: a BoxCollider placed near the door. Rotation and scale are respected.")]
    public BoxCollider enterTrigger;      // Door zone (Trigger flag optional)

    [Tooltip("Press this key to enter/exit when inside the door zone.")]
    public KeyCode useKey = KeyCode.E;

    [Tooltip("Only colliders with this tag count as 'player' inside the zone.")]
    public string playerTag = "Player";

    [Header("Driving/Selection")]
    [Tooltip("Your vehicle AI driver. Driving is allowed only when a player is seated.")]
    public SimpleCarAI_NavMesh carAI;     // Vehicle AI (same GameObject as this script is typical)

    // The seated player (we store the GameObject to easily fetch components as needed).
    GameObject driver;

    // Reusable buffer to avoid per-frame allocations when checking the zone.
    // 16 is plenty for typical door areas; increase if you expect many overlapping colliders.
    static readonly Collider[] hitsBuf = new Collider[16];

    void Awake()
    {
        // If the AI lives on the same GameObject, auto-grab it so wiring is simpler.
        if (!carAI) TryGetComponent(out carAI);
    }

    void Start()
    {
        // Start safe: vehicle cannot drive and is not selected until someone is seated.
        SetDriving(false);
        SetSelected(false);
    }

    void Update()
    {
        // We react only on the press edge to avoid multiple toggles in one key hold.
        if (!Input.GetKeyDown(useKey)) return;

        if (driver == null)
        {
            // Not seated yet -> try to find a player within the door zone and seat them.
            var p = FindPlayerInZone();
            if (p) Seat(p);
        }
        else
        {
            // Already seated -> unseat the current driver.
            Unseat();
        }
    }

    // ======================
    // Core Seat / Unseat API
    // ======================

    /// <summary>
    /// Seats the given player: disables their movement systems, parents them to the seat,
    /// and enables driving + selection for this vehicle.
    /// </summary>
    void Seat(GameObject player)
    {
        driver = player;

        // Disable player movement systems (collider, click-move, NavMeshAgent).
        // Rationale:
        //  - Collider off prevents unwanted physics interactions while inside the car.
        //  - ClickMove off prevents new movement requests.
        //  - NavMeshAgent disabled to avoid the agent trying to correct position while parented.
        TogglePlayerMovement(player, enable: false);

        // Snap the player to the seat and make them follow the vehicle's transform.
        // We use worldPositionStays = false to take the exact local transform (0/0/0)
        // relative to driverSeat. We also force localScale to 1 to avoid any inherited scale quirks.
        var t = player.transform;
        t.SetParent(driverSeat, worldPositionStays: false);
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale    = Vector3.one;

        // Allow the vehicle to drive (enable AI) and mark it selected (ring on).
        SetDriving(true);
        SetSelected(true);
    }

    /// <summary>
    /// Unseats the current driver: detaches from the seat, places them at ExitPoint,
    /// safely re-enables movement, and disables driving + selection.
    /// </summary>
    void Unseat()
    {
        if (!driver) return;

        var t = driver.transform;

        // Detach from vehicle. We keep world space to preserve current visual pose until we place them.
        t.SetParent(null, worldPositionStays: true);

        // Place at the exit point if provided; localScale forced back to 1 to avoid cumulative errors.
        if (exitPoint) t.SetPositionAndRotation(exitPoint.position, exitPoint.rotation);
        t.localScale = Vector3.one;

        // Safely restore the NavMeshAgent:
        //  - It must be enabled *before* we use Warp/ResetPath.
        //  - SamplePosition ensures there is walkable mesh at/near the ExitPoint to avoid errors like
        //    "ResetPath can only be called on an active agent placed on a NavMesh".
        if (driver.TryGetComponent(out NavMeshAgent agent))
        {
            if (!agent.enabled) agent.enabled = true;

            if (NavMesh.SamplePosition(t.position, out var hit, 1.5f, NavMesh.AllAreas))
            {
                // Warp sets the agent's internal state and avoids sudden accelerations
                // or internal path invalidation issues.
                agent.Warp(hit.position);
                agent.isStopped = true;
                agent.ResetPath();
            }
            else
            {
                // If ExitPoint is off-mesh, we prefer a clean fallback (agent disabled)
                // over spamming errors or letting the agent run invalid logic.
                agent.enabled = false;
                Debug.LogWarning("[VehicleSeatInteractor] ExitPoint is not on the NavMesh. Agent has been disabled.");
            }
        }

        // Re-enable player movement systems after agent recovery.
        TogglePlayerMovement(driver, enable: true);

        // Clear driver and stop driving/selection for this vehicle.
        driver = null;
        SetSelected(false);
        SetDriving(false);
    }

    // ======================
    // Helpers (zone, player, toggles)
    // ======================

    /// <summary>
    /// Finds the best player candidate inside the door zone.
    /// Uses an oriented OverlapBox that respects the BoxCollider's rotation and scale.
    /// Returns null if no valid player is inside.
    /// </summary>
    GameObject FindPlayerInZone()
    {
        if (!enterTrigger) return null;

        // Convert the BoxCollider (which is defined in local space) into an oriented world-space box.
        // center in world space:
        var tr     = enterTrigger.transform;
        var center = tr.TransformPoint(enterTrigger.center);

        // half extents in world space that respect lossyScale:
        // Vector3.Scale applies each axis scale correctly (important for non-uniform scaling).
        var half   = Vector3.Scale(enterTrigger.size, tr.lossyScale) * 0.5f;

        // rotation in world space (oriented box, not axis-aligned):
        var rot    = tr.rotation;

        // We include triggers to support "IsTrigger" setups; the layer mask is ~0 (everything),
        // which you can tailor in the future if you want to exclude certain layers.
        int count = Physics.OverlapBoxNonAlloc(center, half, hitsBuf, rot, ~0, QueryTriggerInteraction.Collide);

        GameObject best = null;
        float bestSqr = float.MaxValue;
        var refPos = driverSeat ? driverSeat.position : transform.position;

        // Choose the closest tagged "Player" to the driver seat as the best candidate.
        // This avoids ambiguity if multiple players are inside the zone.
        for (int i = 0; i < count; i++)
        {
            var h = hitsBuf[i];
            if (!h || !h.CompareTag(playerTag)) continue;

            float d = (h.transform.position - refPos).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = h.gameObject; }
        }
        return best;
    }

    /// <summary>
    /// Enables/disables the player's collider, click-move script, and NavMeshAgent
    /// in a consistent order to avoid physics or agent state issues.
    /// </summary>
    void TogglePlayerMovement(GameObject player, bool enable)
    {
        if (!player) return;

        // Collider: disable while seated to avoid clipping/forces with the vehicle interior.
        if (player.TryGetComponent(out Collider col))
            col.enabled = enable;

        // Your click-to-move script: disable while seated, and hide its selection ring if present.
        if (player.TryGetComponent(out ClickMove_SimpleTurn_Refined mover))
        {
            mover.enabled = enable;
            if (mover.selectionRing) mover.selectionRing.SetActive(enable);
        }

        // NavMeshAgent:
        // - When enabling, ensure we start from a neutral state (stopped and cleared path).
        // - When disabling, call ResetPath only if the agent is on the NavMesh; then disable.
        if (player.TryGetComponent(out NavMeshAgent agent))
        {
            if (enable)
            {
                if (!agent.enabled) agent.enabled = true;
                agent.isStopped = true;   // neutral start
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

    /// <summary>
    /// Centralized driving control: the vehicle can only drive when a driver is seated.
    /// (We simply toggle the AI component. If you later add manual driving, control it here.)
    /// </summary>
    void SetDriving(bool canDrive)
    {
        if (!carAI) return;
        carAI.enabled = canDrive;
    }

    /// <summary>
    /// Centralized selection control: we toggle the AI's selection ring,
    /// because SimpleCarAI_NavMesh reads selection from this GameObject's active state.
    /// </summary>
    void SetSelected(bool selected)
    {
        if (carAI && carAI.selectionRing)
            carAI.selectionRing.SetActive(selected);
    }

    // ======================
    // Editor Visualization
    // ======================

    /// <summary>
    /// Draw the door zone in the editor to make it easy to position and size correctly.
    /// Uses the BoxCollider's local transform, which matches how we query OverlapBox above.
    /// </summary>
    void OnDrawGizmosSelected()
    {
        if (!enterTrigger) return;

        // Mirror the collider's local transform so the gizmo matches the actual query volume.
        Gizmos.matrix = enterTrigger.transform.localToWorldMatrix;

        // Semi-transparent fill:
        Gizmos.color  = new Color(0, 1, 0, 0.15f);
        Gizmos.DrawCube(enterTrigger.center, enterTrigger.size);

        // Solid wireframe for clarity:
        Gizmos.color  = new Color(0, 1, 0, 0.8f);
        Gizmos.DrawWireCube(enterTrigger.center, enterTrigger.size);
    }
}
