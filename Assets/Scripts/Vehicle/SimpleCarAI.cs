// SimpleCarAI_NavMesh_Select.cs
// Drop-in replacement for SimpleCarAI_NavMesh with "click-to-select" behavior.
// LMB toggles selection (via ring). RMB sets destination ONLY if selected.

using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(Rigidbody))]
[DisallowMultipleComponent]
public class SimpleCarAI_NavMesh : MonoBehaviour
{
    [Header("Wheel Colliders (assign all four)")]
    public WheelCollider wheelFL, wheelFR, wheelRL, wheelRR;

    [Header("Wheel Meshes (assign all four)")]
    public Transform meshFL, meshFR, meshRL, meshRR;
    [Tooltip("Use this if your wheel models are rotated in DCC (e.g. need +90° around X).")]
    public Vector3 meshRotationOffsetEuler = Vector3.zero;

    [Header("Selection")]
    [Tooltip("Optional: child ring or highlight object. It will be toggled by LMB.")]
    public GameObject selectionRing;

    [Header("Player Control")]
    public bool playerControlled = false;

    [Header("Driving")]
    public float motorForce = 1500f;
    public float brakeForce = 3000f;
    public float maxSteerAngle = 30f;      // deg
    public float maxSpeed = 12f;           // m/s forward/reverse limit
    public float steerSpeed = 120f;        // deg/sec

    [Header("Targeting / Arrival")]
    public float stopDistance = 2f;
    public float waypointReachDist = 1.25f;
    public float forwardAngleThreshold = 45f;

    [Header("Approach Deceleration (prevents last-second throttle)")]
    public float approachDecel = 3.0f;      // m/s^2
    public float approachDeadband = 0.4f;   // m/s

    [Header("Handbrake on Arrival")]
    public bool handbrakeOnArrival = true;
    public float handbrakeBrakeTorque = 20000f;
    public bool freezeRigidBodyOnHandbrake = true;

    [Header("NavMesh")]
    public bool useNavMesh = true;
    public bool allowPartialPaths = true;
    public float sampleMaxDistance = 3f;
    public int areaMask = NavMesh.AllAreas;
    public bool continuousReplan = true;
    public float replanInterval = 0.5f;

    [Header("Ground Picking (right-click)")]
    public LayerMask groundMask = ~0;

    [Header("Switching & Stability")]
    public float stopSpeedEpsilon = 0.25f;
    public float startKickTime = 0.2f;
    public float startKickFactor = 0.6f;

    [Header("Center of Mass (optional)")]
    public Transform centerOfMass;

    private enum Mode { Forward, Reverse, StopToSwitch, ArriveHold }
    private Mode mode = Mode.Forward;

    private Rigidbody rb;
    private float modeTimer;
    private float currentSteerAngle;

    private NavMeshPath path;
    private Vector3[] corners = new Vector3[0];
    private int cornerIndex = -1;
    private Vector3 finalDestination;
    private bool hasDestination;
    private float lastReplanTime = -999f;

    private bool handbrakeActive = false;
    private RigidbodyConstraints originalConstraints;

    // Selection state/cache
    private Collider selfCol;

    private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    private Quaternion MeshRotOffset => Quaternion.Euler(meshRotationOffsetEuler);

    void Awake()
    {
        path = new NavMeshPath();
        rb = GetComponent<Rigidbody>();
        originalConstraints = rb.constraints;
        if (centerOfMass) rb.centerOfMass = centerOfMass.localPosition;

        selfCol = GetComponent<Collider>(); // Needed for LMB selection
        if (selectionRing) selectionRing.SetActive(false);
    }

    void Update()
    {
        // --- Selection toggle (LMB) ---
        if (Input.GetMouseButtonDown(0))
            SetSelected(RaycastHitsSelf());

        // --- Input: destination (RMB) is only allowed when SELECTED ---
        bool isSelected = selectionRing && selectionRing.activeSelf;

        if (playerControlled && isSelected && Input.GetMouseButtonDown(1) && TryPickGround(out var clickPos))
            SetDestination(clickPos);

        // Keep path refresh going regardless of selection (so non-selected cars still steer properly)
        if (useNavMesh && hasDestination && continuousReplan && Time.time - lastReplanTime >= replanInterval)
            ReplanPath();
    }

    void FixedUpdate()
    {
        if (handbrakeActive && !hasDestination) { UpdateWheelVisuals(); return; }

        if (!hasDestination)
        {
            ApplyMotor(0f);
            ApplyBrakes(0f);
            UpdateWheelVisuals();
            return;
        }

        // pop next corner if close
        if (useNavMesh && corners.Length > 0 && cornerIndex >= 0 && cornerIndex < corners.Length)
        {
            float d = Flat(corners[cornerIndex] - transform.position).magnitude;
            if (d <= waypointReachDist)
            {
                cornerIndex++;
                if (cornerIndex >= corners.Length) mode = Mode.ArriveHold;
            }
        }

        Vector3 target = GetCurrentTarget();
        Vector3 toTgtFlat = Flat(target - transform.position);
        float dist = toTgtFlat.magnitude;
        Vector3 dir = (dist > 0.001f) ? toTgtFlat.normalized : transform.forward;

        float rawSteer = Vector3.SignedAngle(transform.forward, dir, Vector3.up);
        float absAngle = Mathf.Abs(rawSteer);
        float fwdSplit = Mathf.Clamp(forwardAngleThreshold, 1f, 179f);

        if (useNavMesh)
        {
            if (cornerIndex >= corners.Length - 1 &&
                Flat(finalDestination - transform.position).magnitude <= stopDistance)
                mode = Mode.ArriveHold;
        }
        else if (dist <= stopDistance) mode = Mode.ArriveHold;

        Mode desired = (absAngle <= fwdSplit) ? Mode.Forward : Mode.Reverse;
        if (mode != desired && mode != Mode.StopToSwitch && mode != Mode.ArriveHold)
        {
            mode = Mode.StopToSwitch;
            modeTimer = 0f;
        }

        float signedSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);

        // kinematic safe speed near FINAL goal
        bool onFinal = !useNavMesh || (cornerIndex >= corners.Length - 1 && cornerIndex >= 0);
        float dGoal = onFinal ? Flat(finalDestination - transform.position).magnitude : 99999f;
        float safeKinematic = onFinal
            ? Mathf.Sqrt(Mathf.Max(0f, 2f * Mathf.Max(0.01f, approachDecel) * Mathf.Max(0f, dGoal - stopDistance)))
            : maxSpeed;
        float allowedSpeed = Mathf.Min(safeKinematic, maxSpeed);

        float clampedTargetSteer = Mathf.Clamp(rawSteer, -maxSteerAngle, maxSteerAngle);
        float absAngleNow = Mathf.Abs(rawSteer);

        switch (mode)
        {
            case Mode.ArriveHold:
                ApplyMotor(0f);
                ApplyBrakes(brakeForce);
                if (Mathf.Abs(signedSpeed) <= stopSpeedEpsilon)
                {
                    if (handbrakeOnArrival) EngageHandbrake();
                    hasDestination = false;
                    ClearPath();
                }
                UpdateWheelVisuals();
                return;

            case Mode.StopToSwitch:
                ApplyMotor(0f);
                ApplyBrakes(brakeForce * Mathf.Clamp01(Mathf.Abs(signedSpeed) / Mathf.Max(0.01f, maxSpeed)));
                if (Mathf.Abs(signedSpeed) <= stopSpeedEpsilon)
                {
                    ApplyBrakes(0f);
                    mode = desired;
                    modeTimer = 0f;
                }
                UpdateWheelVisuals();
                return;

            case Mode.Reverse:
            {
                SmoothSteer(-clampedTargetSteer);
                float projSpeed = -signedSpeed;
                float speedErr  = allowedSpeed - projSpeed;

                if (speedErr > approachDeadband)
                {
                    float kick = (modeTimer < startKickTime && !onFinal) ? startKickFactor : 1f;
                    ApplyBrakes(0f);
                    ApplyMotor(-motorForce * Mathf.Clamp01(speedErr / Mathf.Max(0.01f, maxSpeed)) * kick);
                }
                else if (speedErr < -approachDeadband)
                {
                    ApplyMotor(0f);
                    ApplyBrakes(brakeForce * Mathf.Clamp01((-speedErr) / Mathf.Max(0.01f, maxSpeed)));
                }
                else { ApplyMotor(0f); ApplyBrakes(0f); }

                if (absAngleNow <= fwdSplit) { mode = Mode.StopToSwitch; modeTimer = 0f; }
                break;
            }

            case Mode.Forward:
            default:
            {
                SmoothSteer(clampedTargetSteer);
                float projSpeed = signedSpeed;
                float speedErr  = allowedSpeed - projSpeed;

                if (speedErr > approachDeadband)
                {
                    float kick = (modeTimer < startKickTime && !onFinal) ? startKickFactor : 1f;
                    ApplyBrakes(0f);
                    ApplyMotor(motorForce * Mathf.Clamp01(speedErr / Mathf.Max(0.01f, maxSpeed)) * kick);
                }
                else if (speedErr < -approachDeadband)
                {
                    ApplyMotor(0f);
                    ApplyBrakes(brakeForce * Mathf.Clamp01((-speedErr) / Mathf.Max(0.01f, maxSpeed)));
                }
                else { ApplyMotor(0f); ApplyBrakes(0f); }

                if (absAngleNow > fwdSplit) { mode = Mode.StopToSwitch; modeTimer = 0f; }
                break;
            }
        }

        modeTimer += Time.fixedDeltaTime;
        UpdateWheelVisuals();
    }

    // ---- Selection helpers (mirrors ClickToMoveSelectable behavior) ----
    bool RaycastHitsSelf()
    {
        if (!Camera.main || !selfCol) return false;
        Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
        return Physics.Raycast(ray, out var hit, 1000f, ~0, QueryTriggerInteraction.Ignore)
               && hit.collider == selfCol;
    }

    void SetSelected(bool v)
    {
        if (selectionRing) selectionRing.SetActive(v);
    }

    // ---- Handbrake ----
    public void EngageHandbrake()
    {
        if (handbrakeActive) return;
        handbrakeActive = true;
        ApplyMotor(0f);
        ApplyBrakes(handbrakeBrakeTorque);
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        if (freezeRigidBodyOnHandbrake)
        {
            originalConstraints = rb.constraints;
            rb.constraints = RigidbodyConstraints.FreezeAll;
        }
    }

    public void ReleaseHandbrake()
    {
        if (!handbrakeActive) return;
        handbrakeActive = false;
        if (freezeRigidBodyOnHandbrake) rb.constraints = originalConstraints;
        ApplyBrakes(0f);
    }

    // ---- Wheel visuals ----
    void UpdateWheelVisuals()
    {
        UpdateOneWheel(wheelFL, meshFL);
        UpdateOneWheel(wheelFR, meshFR);
        UpdateOneWheel(wheelRL, meshRL);
        UpdateOneWheel(wheelRR, meshRR);
    }
    void UpdateOneWheel(WheelCollider col, Transform mesh)
    {
        if (!col || !mesh) return;
        col.GetWorldPose(out Vector3 pos, out Quaternion rot);
        rot = rot * MeshRotOffset;
        mesh.SetPositionAndRotation(pos, rot);
    }

    // ---- Nav helpers ----
    void SetDestination(Vector3 worldPos)
    {
        if (handbrakeActive) ReleaseHandbrake();

        if (!useNavMesh)
        {
            finalDestination = worldPos;
            hasDestination = true;
            ClearPath();

            float a = Vector3.SignedAngle(
                transform.forward,
                Flat(finalDestination - transform.position).normalized,
                Vector3.up
            );
            float split = Mathf.Clamp(forwardAngleThreshold, 1f, 179f);
            mode = (Mathf.Abs(a) <= split) ? Mode.Forward : Mode.Reverse;
            modeTimer = 0f;
            return;
        }

        if (!NavMesh.SamplePosition(worldPos, out var hit, sampleMaxDistance, areaMask))
        {
            useNavMesh = false;
            SetDestination(worldPos);
            return;
        }

        finalDestination = hit.position;
        hasDestination = true;
        ReplanPath();
    }

    void ReplanPath()
    {
        lastReplanTime = Time.time;

        if (!NavMesh.CalculatePath(transform.position, finalDestination, areaMask, path)) return;
        if (path.status == NavMeshPathStatus.PathInvalid) return;
        if (path.status == NavMeshPathStatus.PathPartial && !allowPartialPaths) return;

        corners = path.corners;
        cornerIndex = (corners.Length >= 2) ? 1 : 0;

        Vector3 tgt = GetCurrentTarget();
        float a = Vector3.SignedAngle(
            transform.forward,
            Flat(tgt - transform.position).normalized,
            Vector3.up
        );
        float split = Mathf.Clamp(forwardAngleThreshold, 1f, 179f);
        mode = (Mathf.Abs(a) <= split) ? Mode.Forward : Mode.Reverse;
        modeTimer = 0f;
    }

    Vector3 GetCurrentTarget()
    {
        if (useNavMesh && corners.Length > 0 && cornerIndex >= 0 && cornerIndex < corners.Length)
            return corners[cornerIndex];
        return finalDestination;
    }

    void ClearPath()
    {
        corners = System.Array.Empty<Vector3>();
        cornerIndex = -1;
    }

    // RMB world-pick; unchanged except it’s only called when selected.
    bool TryPickGround(out Vector3 pos)
    {
        Ray ray = Camera.main ? Camera.main.ScreenPointToRay(Input.mousePosition)
                              : new Ray(transform.position + Vector3.up, transform.forward);
        if (Physics.Raycast(ray, out var hit, 5000f, groundMask)) { pos = hit.point; return true; }
        pos = default; return false;
    }

    // ---- Steering + drive ----
    void SmoothSteer(float targetAngle)
    {
        targetAngle = Mathf.Clamp(targetAngle, -maxSteerAngle, maxSteerAngle);
        currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, targetAngle, steerSpeed * Time.fixedDeltaTime);
        if (wheelFL) wheelFL.steerAngle = currentSteerAngle;
        if (wheelFR) wheelFR.steerAngle = currentSteerAngle;
    }
    void ApplyMotor(float force)
    {
        if (wheelFL) wheelFL.motorTorque = force;
        if (wheelFR) wheelFR.motorTorque = force;
        if (wheelRL) wheelRL.motorTorque = force;
        if (wheelRR) wheelRR.motorTorque = force;
    }
    void ApplyBrakes(float force)
    {
        if (wheelFL) wheelFL.brakeTorque = force;
        if (wheelFR) wheelFR.brakeTorque = force;
        if (wheelRL) wheelRL.brakeTorque = force;
        if (wheelRR) wheelRR.brakeTorque = force;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, stopDistance);

        float half = forwardAngleThreshold;
        Vector3 f = transform.forward;
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(transform.position, Quaternion.Euler(0, -half, 0) * f * 3f);
        Gizmos.DrawRay(transform.position, Quaternion.Euler(0,  half, 0) * f * 3f);

        if (useNavMesh && corners != null && corners.Length > 1)
        {
            Gizmos.color = Color.green;
            for (int i = 0; i < corners.Length - 1; i++)
                Gizmos.DrawLine(corners[i], corners[i + 1]);
        }

        if (handbrakeActive)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(transform.position, stopDistance * 0.6f);
        }
    }
}
