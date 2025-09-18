using UnityEngine;
using System.Linq;

[RequireComponent(typeof(CharacterController))]
public class KinectClimber : MonoBehaviour
{
    // ---- Kinect joints (v1) ----
    readonly int handL = (int)KinectWrapper.NuiSkeletonPositionIndex.HandLeft;
    readonly int handR = (int)KinectWrapper.NuiSkeletonPositionIndex.HandRight;
    readonly int footL = (int)KinectWrapper.NuiSkeletonPositionIndex.FootLeft;
    readonly int footR = (int)KinectWrapper.NuiSkeletonPositionIndex.FootRight;
    readonly int hipC = (int)KinectWrapper.NuiSkeletonPositionIndex.HipCenter;

    [Header("Detección de presas")]
    public LayerMask scalableMask;                // si está vacío, se usa Tag
    public string scalableTag = "Scalable";

    [Header("Parámetros de agarre (ajusta a tu escala)")]
    public bool useHands = true;
    public bool useFeet = true;
    public float handProbeRadius = 0.04f;
    public float footProbeRadius = 0.04f;
    public float requiredHoldTime = 0.20f;
    public float releaseDistance = 0.04f;
    public float maxReachFromHip = 0.40f;

    [Header("Movimiento trepando")]
    [Tooltip("Intensidad de pegado a las anclas por frame")]
    [Range(0.05f, 2f)] public float pullGain = 1.0f;
    [Tooltip("Sesgo vertical para favorecer la subida")]
    public float climbUpBonus = 0.9f;
    [Tooltip("Límite de corrección por frame (en unidades de tu escena)")]
    public float maxSnapPerFrame = 0.05f;
    [Tooltip("Empujón hacia el muro (pequeño avance)")]
    public float wallSnap = 0.02f;
    [Tooltip("Gravedad cuando NO hay agarre")]
    public float gravityWhenFree = 9.81f;

    [Header("Reglas de escalada")]
    [Tooltip("Min. agarres requeridos para mover (vertical/lateral)")]
    public int minGripsToClimb = 2;
    [Tooltip("Teletransportar el cuerpo al confirmar agarre")]
    public bool hardSnapOnGrip = true;

    [Header("Subida por movimiento de extremidades")]
    public float stepUpGain = 0.5f;
    public float minUpDelta = 0.002f;

    [Header("Movimiento lateral mientras se trepa")]
    [Tooltip("Ganancia de movimiento lateral según gesto")]
    public float lateralGain = 0.5f;
    [Tooltip("Delta mínimo lateral del efector para contar (ruido)")]
    public float minSideDelta = 0.002f;

    [Header("Integración con locomoción")]
    public KinectHighKneeImpulseMover locomotionToDisable;

    [Header("Origen de efectores (opcional)")]
    public bool useAvatarBones = false;
    public Transform hipRef;
    public Transform handLRef, handRRef, footLRef, footRRef;

    // ---- Internos ----
    CharacterController cc;
    KinectManager km;
    uint userId;

    struct Eff
    {
        public bool enabled;
        public bool touching;
        public bool gripped;
        public bool justGripped;      // para snap duro
        public float dwell;
        public Vector3 anchor;
        public Collider col;
        public Vector3 lastPos;       // para deltas vertical/lateral
        public float probeRadius;
    }

    Eff eHL, eHR, eFL, eFR;
    float verticalVel; // solo para gravedad cuando NO hay agarre

    void Reset()
    {
        if (!hipRef) hipRef = FindByContains(transform, "hip", "Hip");
        if (!handLRef) handLRef = FindByContains(transform, "HandLT", "LeftHand", "Hand_L");
        if (!handRRef) handRRef = FindByContains(transform, "HandRT", "RightHand", "Hand_R");
        if (!footLRef) footLRef = FindByContains(transform, "FootLT", "LeftFoot", "Foot_L");
        if (!footRRef) footRRef = FindByContains(transform, "FootRT", "RightFoot", "Foot_R");
    }

    static Transform FindByContains(Transform root, params string[] keys)
    {
        if (!root) return null;
        var all = root.GetComponentsInChildren<Transform>(true);
        foreach (var t in all)
        {
            string n = t.name.ToLower();
            if (keys.Any(k => n.Contains(k.ToLower()))) return t;
        }
        return null;
    }

    void Awake()
    {
        cc = GetComponent<CharacterController>();

        eHL.enabled = useHands; eHL.probeRadius = handProbeRadius;
        eHR.enabled = useHands; eHR.probeRadius = handProbeRadius;
        eFL.enabled = useFeet; eFL.probeRadius = footProbeRadius;
        eFR.enabled = useFeet; eFR.probeRadius = footProbeRadius;

        km = KinectManager.Instance;
        if (!locomotionToDisable)
            locomotionToDisable = GetComponentInParent<KinectHighKneeImpulseMover>();
    }

    void Update()
    {
        // 1) Usuario disponible
        if (km == null || !KinectHelpers.TryGetFirstUserId(km, out userId))
        {
            ReleaseAll();
            ApplyFreeMovement(Vector3.zero);
            return;
        }

        // 2) Cadera y extremidades (en mundo)
        Vector3 hipW = GetHipWorld();

        Vector3 hl = GetJointWorld(handL, ref eHL, handLRef);
        Vector3 hr = GetJointWorld(handR, ref eHR, handRRef);
        Vector3 fl = GetJointWorld(footL, ref eFL, footLRef);
        Vector3 fr = GetJointWorld(footR, ref eFR, footRRef);

        // 3) Detección / dwell / anclaje
        if (eHL.enabled) UpdateEffector(ref eHL, hl, hipW);
        if (eHR.enabled) UpdateEffector(ref eHR, hr, hipW);
        if (eFL.enabled) UpdateEffector(ref eFL, fl, hipW);
        if (eFR.enabled) UpdateEffector(ref eFR, fr, hipW);

        int gripCount = (eHL.gripped ? 1 : 0) + (eHR.gripped ? 1 : 0) + (eFL.gripped ? 1 : 0) + (eFR.gripped ? 1 : 0);
        bool anyGrip = gripCount > 0;

        if (locomotionToDisable) locomotionToDisable.enabled = !anyGrip;

        if (anyGrip)
        {
            // 3.1) Teletransportación al confirmar agarre (snap duro)
            if (hardSnapOnGrip)
            {
                Vector3 snapDelta = Vector3.zero; int n = 0;
                if (eHL.justGripped) { snapDelta += (eHL.anchor - hl); n++; eHL.justGripped = false; }
                if (eHR.justGripped) { snapDelta += (eHR.anchor - hr); n++; eHR.justGripped = false; }
                if (eFL.justGripped) { snapDelta += (eFL.anchor - fl); n++; eFL.justGripped = false; }
                if (eFR.justGripped) { snapDelta += (eFR.anchor - fr); n++; eFR.justGripped = false; }
                if (n > 0) cc.Move(snapDelta / n);
            }

            // 4) Corrección de pegado a anclas (promedio)
            Vector3 delta = Vector3.zero; int m = 0;

            if (eHL.gripped) { delta += (eHL.anchor - hl); m++; }
            if (eHR.gripped) { delta += (eHR.anchor - hr); m++; }
            if (eFL.gripped) { delta += (eFL.anchor - fl); m++; }
            if (eFR.gripped) { delta += (eFR.anchor - fr); m++; }

            if (m > 0)
            {
                delta /= m;
                delta.y *= (1f + climbUpBonus);
                delta *= Mathf.Clamp(pullGain, 0.05f, 2f);
                delta = Vector3.ClampMagnitude(delta, maxSnapPerFrame);
            }

            // 5) Movimiento vertical por gesto (step-up) — requiere mínimo de agarres
            if (gripCount >= Mathf.Max(1, minGripsToClimb))
            {
                float up = 0f;
                if (!eHL.gripped && eHL.enabled) up += Mathf.Max(0f, (hl.y - eHL.lastPos.y) - minUpDelta);
                if (!eHR.gripped && eHR.enabled) up += Mathf.Max(0f, (hr.y - eHR.lastPos.y) - minUpDelta);
                if (!eFL.gripped && eFL.enabled) up += Mathf.Max(0f, (fl.y - eFL.lastPos.y) - minUpDelta);
                if (!eFR.gripped && eFR.enabled) up += Mathf.Max(0f, (fr.y - eFR.lastPos.y) - minUpDelta);
                if (up > 0f) delta += Vector3.up * (up * stepUpGain);
            }

            // 6) Movimiento lateral por gesto — requiere mínimo de agarres
            if (gripCount >= Mathf.Max(1, minGripsToClimb))
            {
                float side = 0f;
                Vector3 right = transform.right;   // izquierda/derecha relativas al Player

                if (!eHL.gripped && eHL.enabled) side += Vector3.Dot(hl - eHL.lastPos, right);
                if (!eHR.gripped && eHR.enabled) side += Vector3.Dot(hr - eHR.lastPos, right);
                if (!eFL.gripped && eFL.enabled) side += Vector3.Dot(fl - eFL.lastPos, right);
                if (!eFR.gripped && eFR.enabled) side += Vector3.Dot(fr - eFR.lastPos, right);

                if (Mathf.Abs(side) > minSideDelta)
                    delta += right * (side * lateralGain);
            }

            // 7) Pegado al muro y aplicar
            delta += transform.forward * wallSnap;
            verticalVel = 0f; // sin gravedad mientras hay agarre
            cc.Move(delta);
        }
        else
        {
            // movimiento libre (solo gravedad por ahora)
            ApplyFreeMovement(Vector3.zero);
        }

        // Guardar posiciones para deltas
        if (eHL.enabled) eHL.lastPos = hl;
        if (eHR.enabled) eHR.lastPos = hr;
        if (eFL.enabled) eFL.lastPos = fl;
        if (eFR.enabled) eFR.lastPos = fr;

        // Auto-liberación por distancia al ancla
        if (eHL.gripped && Vector3.Distance(hl, eHL.anchor) > releaseDistance) eHL.gripped = false;
        if (eHR.gripped && Vector3.Distance(hr, eHR.anchor) > releaseDistance) eHR.gripped = false;
        if (eFL.gripped && Vector3.Distance(fl, eFL.anchor) > releaseDistance) eFL.gripped = false;
        if (eFR.gripped && Vector3.Distance(fr, eFR.anchor) > releaseDistance) eFR.gripped = false;
    }

    // ---------- Helpers ----------
    Vector3 GetHipWorld()
    {
        if (useAvatarBones && hipRef) return hipRef.position;
        if (!km.IsJointTracked(userId, hipC)) return Vector3.zero;
        return ToUnity(km.GetJointPosition(userId, hipC));
    }

    Vector3 GetJointWorld(int joint, ref Eff e, Transform overrideRef)
    {
        if (useAvatarBones && overrideRef)
        {
            e.enabled = true;
            return overrideRef.position;
        }

        if (!e.enabled || !km.IsJointTracked(userId, joint))
            return Vector3.zero;

        return ToUnity(km.GetJointPosition(userId, joint));
    }

    void UpdateEffector(ref Eff e, Vector3 posW, Vector3 hipW)
    {
        if (posW == Vector3.zero)
        {
            e.touching = false; e.dwell = 0f; if (!e.gripped) e.col = null; return;
        }

        // seguridad: alcance a cadera
        if (hipW != Vector3.zero && Vector3.Distance(posW, hipW) > maxReachFromHip)
        {
            e.touching = false; e.dwell = 0f; e.gripped = false; e.col = null; return;
        }

        // detección
        Collider found = null;
        if (scalableMask.value != 0)
        {
            var cols = Physics.OverlapSphere(posW, e.probeRadius, scalableMask, QueryTriggerInteraction.Collide);
            if (cols != null && cols.Length > 0) found = cols[0];
        }
        else
        {
            var cols = Physics.OverlapSphere(posW, e.probeRadius, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < cols.Length; i++)
                if (cols[i].CompareTag(scalableTag)) { found = cols[i]; break; }
        }

        if (found != null)
        {
            e.touching = true;
            if (e.col == found) e.dwell += Time.deltaTime;
            else { e.col = found; e.dwell = 0f; }

            if (!e.gripped && e.dwell >= requiredHoldTime)
            {
                e.gripped = true;
                e.justGripped = true;                 // <-- para teletransportación
                e.anchor = found.ClosestPoint(posW);

                var grip = e.col.GetComponent<ClimbableGrip>();
                if (grip) grip.OnGripStart();
            }
        }
        else
        {
            e.touching = false;
            e.dwell = 0f;
            if (!e.gripped) e.col = null;
        }
    }

    void ApplyFreeMovement(Vector3 lateral)
    {
        verticalVel += -Mathf.Abs(gravityWhenFree) * Time.deltaTime;
        Vector3 move = lateral;
        move.y += verticalVel * Time.deltaTime;

        CollisionFlags flags = cc.Move(move);
        if ((flags & CollisionFlags.Below) != 0) verticalVel = 0f;
    }

    void ReleaseAll()
    {
        eHL.gripped = eHR.gripped = eFL.gripped = eFR.gripped = false;
        eHL.justGripped = eHR.justGripped = eFL.justGripped = eFR.justGripped = false;
        eHL.dwell = eHR.dwell = eFL.dwell = eFR.dwell = 0f;
        eHL.col = eHR.col = eFL.col = eFR.col = null;
    }

    static Vector3 ToUnity(Vector3 k) => new Vector3(k.x, k.y, -k.z);
}
