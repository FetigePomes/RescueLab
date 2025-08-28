using UnityEngine;
using UnityEngine.AI;

public class ClickMove_SimpleTurn_Refined : MonoBehaviour
{
    [Header("Refs")]
    public NavMeshAgent agent;           // Assign your own agent
    public GameObject selectionRing;     // Child ring (inactive by default)

    [Header("Tuning")]
    public float maxClickSampleDist = 3f; // Snap click to nearest NavMesh
    public float rotateSpeed = 360f;      // deg/sec
    public float facingTolerance = 5f;    // deg to consider "aligned"

    // State
    bool turning;
    Vector3 target;

    // Cache
    Collider selfCol;

    void Reset() => agent = GetComponent<NavMeshAgent>();

    void Awake()
    {
        if (!agent) agent = GetComponent<NavMeshAgent>();
        selfCol = GetComponent<Collider>();
        if (selectionRing) selectionRing.SetActive(false);

        // We handle rotation ourselves so we can "turn first, then move"
        agent.updateRotation = false;
        agent.autoBraking = true;
    }

    void Update()
    {
        // LMB: select/deselect by clicking the capsule
        if (Input.GetMouseButtonDown(0))
            SetSelected(RaycastHitsSelf());

        // If not selected, do nothing else this frame
        if (selectionRing == null || !selectionRing.activeSelf)
            return;

        // RMB: set a new goal → rotate first
        if (Input.GetMouseButtonDown(1) && TryGetNavmeshPointFromClick(out target))
        {
            turning = true;
            agent.isStopped = true;
        }

        if (turning) RotateThenGo();
        else if (Arrived()) agent.isStopped = true;
    }

    /// Rotates toward target; when aligned, starts NavMeshAgent movement.
    void RotateThenGo()
    {
        Vector3 dir = target - transform.position; dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) { turning = false; return; }

        Quaternion desired = Quaternion.LookRotation(dir);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, desired, rotateSpeed * Time.deltaTime);

        if (Quaternion.Angle(transform.rotation, desired) <= facingTolerance)
        {
            turning = false;
            agent.isStopped = false;
            agent.SetDestination(target);
        }
    }

    /// True when the agent has effectively reached its destination.
    bool Arrived()
    {
        if (!agent.hasPath || agent.pathPending) return false;
        if (agent.remainingDistance == Mathf.Infinity) return false;
        return agent.remainingDistance <= Mathf.Max(agent.stoppingDistance, 0.05f)
               && agent.velocity.sqrMagnitude < 0.01f;
    }

    /// Click → world ray → nearest NavMesh point (no layer masks needed).
    bool TryGetNavmeshPointFromClick(out Vector3 navPoint)
    {
        navPoint = default;
        if (!Camera.main) return false;

        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out var hit, 1000f, ~0, QueryTriggerInteraction.Ignore))
            return false;

        if (NavMesh.SamplePosition(hit.point, out var navHit, maxClickSampleDist, NavMesh.AllAreas))
        {
            navPoint = navHit.position;
            return true;
        }
        return false;
    }

    /// Left-click hit test to toggle selection.
    bool RaycastHitsSelf()
    {
        if (!Camera.main) return false;
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        return Physics.Raycast(ray, out var hit, 1000f, ~0, QueryTriggerInteraction.Ignore)
               && hit.collider == selfCol;
    }

    /// Selection is just the ring’s active state—no extra bool needed.
    void SetSelected(bool v)
    {
        if (selectionRing) selectionRing.SetActive(v);
    }
}
