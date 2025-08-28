using UnityEngine;

[RequireComponent(typeof(Camera))]
public class SmoothWASD_DollyZoom : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 10f;
    public float moveSmoothTime = 0.12f;   // position smoothing

    [Header("Zoom (moves camera)")]
    public float zoomSpeed = 50f;          // units per scroll “tick”
    public float zoomSmoothTime = 0.12f;   // smoothing for zoom movement
    public float minY = 2f;                // keep above ground
    public float maxY = 200f;              // prevent flying to space

    private Vector3 targetPos;
    private Vector3 moveVel = Vector3.zero;
    private Vector3 zoomVel = Vector3.zero;

    void Awake()
    {
        targetPos = transform.position;
    }

    void Update()
    {
        // --- WASD movement (camera-relative on ground plane) ---
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        Vector3 input = new Vector3(h, 0f, v);
        if (input.sqrMagnitude > 1f) input.Normalize();

        Vector3 world = transform.TransformDirection(input);
        world.y = 0f;

        targetPos += world * moveSpeed * Time.deltaTime;

        // --- Dolly zoom (move along camera forward) ---
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > 0.0001f)
        {
            Vector3 zoomTarget = targetPos + transform.forward * (scroll * zoomSpeed);
            zoomTarget.y = Mathf.Clamp(zoomTarget.y, minY, maxY);
            // Smooth only the zoom delta so WASD stays snappy
            targetPos = Vector3.SmoothDamp(targetPos, zoomTarget, ref zoomVel, zoomSmoothTime);
        }

        // Clamp height and apply final smooth movement
        targetPos.y = Mathf.Clamp(targetPos.y, minY, maxY);
        transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref moveVel, moveSmoothTime);
    }
}
