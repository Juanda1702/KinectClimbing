using UnityEngine;

/// Alinea el XR Origin para que la cámara del casco coincida con la cabeza del avatar.
/// Funciona con cualquier XR Origin (OpenXR).
public class AlignXROriginToAvatarHead : MonoBehaviour
{
    [Tooltip("Hueso de la cabeza del avatar (p.ej. joint_Neck o joint_Head)")]
    public Transform avatarHead;
    [Tooltip("La cámara dentro del XR Origin (se asigna sola si está vacía)")]
    public Camera xrCamera;

    void Awake()
    {
        if (!xrCamera) xrCamera = GetComponentInChildren<Camera>(true);
    }

    void LateUpdate()
    {
        if (!avatarHead || !xrCamera) return;

        // Queremos que la posición mundial de la cámara coincida con la de la cabeza del avatar.
        Vector3 delta = avatarHead.position - xrCamera.transform.position;
        transform.position += delta;

        // Opcional: alinear yaw al del avatar, manteniendo el roll/pitch del HMD.
        Vector3 camFwd = xrCamera.transform.forward; camFwd.y = 0f; camFwd.Normalize();
        Vector3 headFwd = avatarHead.forward; headFwd.y = 0f; headFwd.Normalize();
        if (camFwd.sqrMagnitude > 0.0001f && headFwd.sqrMagnitude > 0.0001f)
        {
            float angle = Vector3.SignedAngle(camFwd, headFwd, Vector3.up);
            transform.Rotate(0f, angle, 0f, Space.World);
        }
    }
}
