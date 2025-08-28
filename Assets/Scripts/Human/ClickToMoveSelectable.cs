using UnityEngine;
using UnityEngine.AI;

public class ClickMove_SimpleTurn : MonoBehaviour
{
    [Header("Refs")]
    public NavMeshAgent agent;           // Reference to the NavMeshAgent on this Capsule
    public GameObject selectionRing;     // A flat ring to show when selected

    [Header("Tuning")]
    public float maxClickSampleDist = 3f; // Max distance from click to find a NavMesh point
    public float rotateSpeed = 360f;      // Degrees per second for manual turning
    public float facingTolerance = 5f;    // Degrees difference considered "aligned"

    // Internal state
    bool selected, turning;
    Vector3 target;
    Collider selfCol;

    void Reset() => agent = GetComponent<NavMeshAgent>();

    void Awake()
    {
        // Cache references
        if (!agent) agent = GetComponent<NavMeshAgent>();
        selfCol = GetComponent<Collider>();
        if (selectionRing) selectionRing.SetActive(false);

        // We rotate manually instead of letting NavMeshAgent do it
        agent.updateRotation = false;
        agent.autoBraking = true;
    }

    void Update()
    {
        // --- Handle selection input ---
        if (Input.GetMouseButtonDown(0)) // Left click
            SetSelected(RaycastHitsSelf());

        if (!selected) return;

        // --- Handle movement input ---
        if (Input.GetMouseButtonDown(1) && TryGetNavmeshPointFromClick(out target))
        {
            // New target clicked: start rotation phase before moving
            turning = true;
            agent.isStopped = true;
        }

        // --- State machine: turning or moving ---
        if (turning) RotateTowardsTarget();
        else CheckArrivalStop();
    }

    /// <summary>
    /// Rotates the capsule until it faces the target point. 
    /// Once aligned (within facingTolerance), movement starts.
    /// </summary>
    void RotateTowardsTarget()
    {
        Vector3 dir = target - transform.position;
        dir.y = 0f; // keep flat on ground

        if (dir.sqrMagnitude < 0.0001f)
        {
            // Target too close, skip movement
            turning = false;
            return;
        }

        // Desired rotation to face the target
        var desired = Quaternion.LookRotation(dir);

        // Rotate smoothly towards target
        transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, rotateSpeed * Time.deltaTime);

        // Check if facing within tolerance
        if (Quaternion.Angle(transform.rotation, desired) <= facingTolerance)
        {
            // Done turning → start moving
            turning = false;
            agent.isStopped = false;
            agent.SetDestination(target);
        }
    }

    /// <summary>
    /// Checks if the agent has arrived at the destination and stops it.
    /// </summary>
    void CheckArrivalStop()
    {
        if (!agent.hasPath || agent.pathPending) return;

        if (agent.remainingDistance <= Mathf.Max(agent.stoppingDistance, 0.05f) &&
            agent.velocity.sqrMagnitude < 0.01f)
        {
            agent.isStopped = true;
        }
    }

    /// <summary>
    /// Shoots a ray from the mouse to the world and tries to find the nearest NavMesh point.
    /// Returns true and the position if valid.
    /// </summary>
    bool TryGetNavmeshPointFromClick(out Vector3 navPoint)
    {
        navPoint = default;
        if (!Camera.main) return false;

        // Screen click → world ray
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

        // Hit something in the world
        if (!Physics.Raycast(ray, out var hit, 1000f, ~0, QueryTriggerInteraction.Ignore))
            return false;

        // Snap to nearest NavMesh point
        return NavMesh.SamplePosition(hit.point, out var navHit, maxClickSampleDist, NavMesh.AllAreas)
            ? (navPoint = navHit.position, true).Item2
            : false;
    }

    /// <summary>
    /// Raycasts from the mouse and returns true if this capsule was hit.
    /// Used to select/deselect with left click.
    /// </summary>
    bool RaycastHitsSelf()
    {
        if (!Camera.main) return false;
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);

        return Physics.Raycast(ray, out var hit, 1000f, ~0, QueryTriggerInteraction.Ignore) 
               && hit.collider == selfCol;
    }

    /// <summary>
    /// Sets selection state and shows/hides the ring.
    /// </summary>
    void SetSelected(bool v)
    {
        selected = v;
        if (selectionRing) selectionRing.SetActive(v);
    }
}
