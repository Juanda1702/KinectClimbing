using UnityEngine;
using System.Linq;
using System.Reflection;

[RequireComponent(typeof(CharacterController))]
public class KinectClimber : MonoBehaviour
{
    // ---- Joints Kinect v1 ----
    readonly int handL = (int)KinectWrapper.NuiSkeletonPositionIndex.HandLeft;
    readonly int handR = (int)KinectWrapper.NuiSkeletonPositionIndex.HandRight;
    readonly int footL = (int)KinectWrapper.NuiSkeletonPositionIndex.FootLeft;
    readonly int footR = (int)KinectWrapper.NuiSkeletonPositionIndex.FootRight;
    readonly int hipC = (int)KinectWrapper.NuiSkeletonPositionIndex.HipCenter;

    enum EffId { HL = 0, HR = 1, FL = 2, FR = 3, None = -1 }

    [Header("Detección de presas")]
    [Tooltip("Layer de presas. Si está vacío, se usa el Tag.")]
    public LayerMask scalableMask;
    [Tooltip("Usado sólo si el LayerMask está vacío.")]
    public string scalableTag = "Scalable";

    [Header("Parámetros de agarre (ajusta a tu escala)")]
    public bool useHands = true;
    public bool useFeet = true;
    public float handProbeRadius = 0.04f;
    public float footProbeRadius = 0.04f;
    [Tooltip("Tiempo tocando una presa antes de considerarse 'agarrado'.")]
    public float requiredHoldTime = 0.20f;
    [Tooltip("Si la extremidad se aleja más que esto del ancla, suelta (no aplica a la primaria bloqueada).")]
    public float releaseDistance = 0.04f;
    [Tooltip("Seguridad: distancia máx. extremidad-cadera.")]
    public float maxReachFromHip = 0.40f;

    [Header("Movimiento trepando")]
    [Tooltip("Intensidad de pegado a las anclas por frame.")]
    [Range(0.05f, 2f)] public float pullGain = 1.0f;
    [Tooltip("Sesgo vertical para favorecer subida.")]
    public float climbUpBonus = 0.9f;
    [Tooltip("Límite de corrección por frame (unidades escena).")]
    public float maxSnapPerFrame = 0.05f;
    [Tooltip("Empujón leve hacia el muro (pon 0 para desactivar).")]
    public float wallSnap = 0.0f;
    [Tooltip("Gravedad cuando NO hay agarre.")]
    public float gravityWhenFree = 9.81f;

    [Header("Subida por gesto (extremidades libres)")]
    [Tooltip("Ganancia de subida por delta Y de extremidades libres.")]
    public float stepUpGain = 0.5f;
    [Tooltip("Umbral mínimo de delta Y (anti-ruido).")]
    public float minUpDelta = 0.002f;

    [Header("Movimiento lateral")]
    [Tooltip("Ganancia por gesto lateral de extremidades libres.")]
    public float lateralGain = 0.5f;
    [Tooltip("Umbral lateral anti-ruido.")]
    public float minSideDelta = 0.002f;
    [Tooltip("Aporte de la cadera al lateral.")]
    public float hipLateralGain = 0.6f;
    [Tooltip("Límite de movimiento lateral por frame.")]
    public float lateralMaxPerFrame = 0.05f;

    [Header("Soltar agarre por no contacto")]
    [Tooltip("Si deja de tocar durante este tiempo, suelta (no aplica a la primaria bloqueada).")]
    public float notouchReleaseTime = 0.25f;

    [Header("Extremidad primaria (última que se agarra)")]
    [Tooltip("La última extremidad que se agarra queda BLOQUEADA: no se suelta por nada hasta que otra extremidad se agarre.")]
    public bool latchPrimary = true;
    [Tooltip("Peso extra de la primaria en el pegado a anclas.")]
    public float primaryAnchorWeight = 1.5f;
    [Tooltip("Peso extra de la primaria en la subida por gesto.")]
    public float primaryStepUpMul = 1.5f;
    [Tooltip("Peso extra de la primaria en el lateral por gesto.")]
    public float primaryLateralMul = 1.5f;

    [Header("Anti-vibración")]
    [Tooltip("0..1 cuánto del movimiento nuevo se aplica vs. mantener el anterior.")]
    [Range(0f, 1f)] public float damping = 0.5f;
    [Tooltip("Ignorar correcciones muy pequeñas (unidades de tu escena).")]
    public float deadzone = 0.002f;

    [Header("Integraciones")]
    [Tooltip("Se desactiva mientras haya agarre(s).")]
    public KinectHighKneeImpulseMover locomotionToDisable;
    [Tooltip("Congela el root relativo al sensor del AvatarController mientras trepas para que no te 'devuelva' al origen.")]
    public bool freezeAvatarRootWhenClimbing = true;
    public AvatarController avatarToFreeze;   // opcional

    [Header("Origen de efectores (opcional)")]
    [Tooltip("Usar los Transforms del avatar para manos/pies/cadera.")]
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

        // Contacto (TOUCH) y agarre (GRIP)
        public bool touching;           // este frame está tocando alguna presa
        public Collider touchingCol;    // presa tocada este frame (para glow)
        public bool gripped;            // está agarrado

        public float dwell;             // tiempo tocando la MISMA presa
        public float noTouchTime;       // tiempo sin tocar (para soltar por no contacto)

        public Vector3 anchor;          // punto anclado en la presa
        public Collider col;            // última presa usada para dwell/grip

        public Vector3 lastPos;         // para deltas (vertical/lateral)
        public float probeRadius;       // radio de búsqueda
    }

    Eff eHL, eHR, eFL, eFR;

    float verticalVel;             // gravedad cuando no hay agarre
    Vector3 lastHipW; bool hipInit;

    // Primaria bloqueada: última que se agarró
    EffId latchedId = EffId.None;

    // Suavizado de corrección (anti vibración)
    Vector3 lastDelta;             // última corrección aplicada

    // AvatarController.offsetRelativeToSensor (por reflexión – opcional)
    FieldInfo fiOffsetRel;
    bool avatarOffsetOriginal;
    bool avatarOffsetCached;

    // --------- Unity ---------
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
        if (!avatarToFreeze)
            avatarToFreeze = GetComponentInParent<AvatarController>();

        if (avatarToFreeze)
        {
            fiOffsetRel = avatarToFreeze.GetType().GetField("offsetRelativeToSensor",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fiOffsetRel != null)
            {
                avatarOffsetOriginal = (bool)fiOffsetRel.GetValue(avatarToFreeze);
                avatarOffsetCached = true;
            }
        }
    }

    void Update()
    {
        // Usuario disponible
        if (km == null || !KinectHelpers.TryGetFirstUserId(km, out userId))
        {
            ForceReleaseAll();     // apaga glows y suelta todo
            ApplyFreeMovement(Vector3.zero);
            UnfreezeAvatarIfNeeded();
            lastDelta = Vector3.zero;
            return;
        }

        // Cadera y extremidades (mundo)
        Vector3 hipW = GetHipWorld();

        Vector3 hl = GetJointWorld(handL, ref eHL, handLRef);
        Vector3 hr = GetJointWorld(handR, ref eHR, handRRef);
        Vector3 fl = GetJointWorld(footL, ref eFL, footLRef);
        Vector3 fr = GetJointWorld(footR, ref eFR, footRRef);

        // Detección / contacto / dwell / anclaje (pasamos el ID de efector)
        if (eHL.enabled) UpdateEffector(EffId.HL, ref eHL, hl, hipW);
        if (eHR.enabled) UpdateEffector(EffId.HR, ref eHR, hr, hipW);
        if (eFL.enabled) UpdateEffector(EffId.FL, ref eFL, fl, hipW);
        if (eFR.enabled) UpdateEffector(EffId.FR, ref eFR, fr, hipW);

        int gripCount = (eHL.gripped ? 1 : 0) + (eHR.gripped ? 1 : 0) + (eFL.gripped ? 1 : 0) + (eFR.gripped ? 1 : 0);
        bool anyGrip = gripCount > 0;

        if (locomotionToDisable) locomotionToDisable.enabled = !anyGrip;
        if (anyGrip && freezeAvatarRootWhenClimbing) FreezeAvatarRoot();
        if (!anyGrip) UnfreezeAvatarIfNeeded();

        if (anyGrip)
        {
            // Pegado a anclas (promedio ponderado; la primaria pesa más)
            Vector3 delta = Vector3.zero; float wSum = 0f;

            AddAnchorDelta(EffId.HL, eHL, hl, ref delta, ref wSum);
            AddAnchorDelta(EffId.HR, eHR, hr, ref delta, ref wSum);
            AddAnchorDelta(EffId.FL, eFL, fl, ref delta, ref wSum);
            AddAnchorDelta(EffId.FR, eFR, fr, ref delta, ref wSum);

            if (wSum > 0f)
            {
                delta /= wSum;
                delta.y *= (1f + climbUpBonus);
                delta *= Mathf.Clamp(pullGain, 0.05f, 2f);
            }

            // Subida por gesto (extremidades libres)
            float up = 0f;
            up += StepUpDelta(EffId.HL, eHL, hl);
            up += StepUpDelta(EffId.HR, eHR, hr);
            up += StepUpDelta(EffId.FL, eFL, fl);
            up += StepUpDelta(EffId.FR, eFR, fr);
            if (up > 0f) delta += Vector3.up * (up * stepUpGain);

            // Lateral por gesto
            Vector3 right = transform.right;
            float side = 0f;
            side += LateralDelta(EffId.HL, eHL, hl, right);
            side += LateralDelta(EffId.HR, eHR, hr, right);
            side += LateralDelta(EffId.FL, eFL, fl, right);
            side += LateralDelta(EffId.FR, eFR, fr, right);

            if (Mathf.Abs(side) > 0f)
                delta += right * Mathf.Clamp(side, -lateralMaxPerFrame, lateralMaxPerFrame);

            // Aporte cadera
            if (hipInit)
            {
                float sideHip = Vector3.Dot(hipW - lastHipW, right);
                if (Mathf.Abs(sideHip) > minSideDelta)
                    delta += right * Mathf.Clamp(sideHip * hipLateralGain, -lateralMaxPerFrame, lateralMaxPerFrame);
            }

            // Anti-vibración: deadzone + damping + límite por frame
            if (delta.sqrMagnitude < (deadzone * deadzone))
            {
                delta = Vector3.zero;
            }
            else
            {
                float a = Mathf.Clamp01(damping);
                delta = Vector3.Lerp(lastDelta, delta, a);
                delta = Vector3.ClampMagnitude(delta, maxSnapPerFrame);
            }

            // Snap al muro (opcional)
            if (wallSnap > 0f)
                delta += transform.forward * wallSnap;

            verticalVel = 0f;          // sin gravedad con agarre
            cc.Move(delta);
            lastDelta = delta;
        }
        else
        {
            // Caída libre (no regresa al origen)
            ApplyFreeMovement(Vector3.zero);
            lastDelta = Vector3.zero;
        }

        // Guardar pos previas
        if (eHL.enabled) eHL.lastPos = hl;
        if (eHR.enabled) eHR.lastPos = hr;
        if (eFL.enabled) eFL.lastPos = fl;
        if (eFR.enabled) eFR.lastPos = fr;
        hipInit = true; lastHipW = hipW;

        // Auto-release por distancia al ancla (NO aplica a primaria bloqueada)
        CheckReleaseByDistance(EffId.HL, ref eHL, hl);
        CheckReleaseByDistance(EffId.HR, ref eHR, hr);
        CheckReleaseByDistance(EffId.FL, ref eFL, fl);
        CheckReleaseByDistance(EffId.FR, ref eFR, fr);
    }

    // ---------- Núcleo contacto/agarre ----------
    void UpdateEffector(EffId id, ref Eff e, Vector3 posW, Vector3 hipW)
    {
        // Si no hay posición válida
        if (posW == Vector3.zero)
        {
            TouchOff(ref e);
            // Si está bloqueada como primaria, NO se suelta.
            if (!(latchPrimary && id == latchedId && e.gripped))
                GripOff(id, ref e);
            return;
        }

        // Seguridad: alcance vs cadera
        if (hipW != Vector3.zero && Vector3.Distance(posW, hipW) > maxReachFromHip)
        {
            TouchOff(ref e);
            if (!(latchPrimary && id == latchedId && e.gripped))
                GripOff(id, ref e);
            return;
        }

        // Buscar presa cercana
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

        // ---- CONTACTO (glow por toque inmediato) ----
        if (found != null)
        {
            if (e.touchingCol != found)
            {
                if (e.touchingCol) CallTouch(e.touchingCol, false); // apagar anterior
                e.touchingCol = found;
                CallTouch(e.touchingCol, true);                     // encender nueva
            }
            e.touching = true;
            e.noTouchTime = 0f;
        }
        else
        {
            if (e.touchingCol) { CallTouch(e.touchingCol, false); e.touchingCol = null; }
            e.touching = false;
            e.noTouchTime += Time.deltaTime;

            // si estaba agarrado y deja de tocar durante X, suelta (NO si es primaria bloqueada)
            if (e.gripped && !(latchPrimary && id == latchedId))
            {
                if (e.noTouchTime >= notouchReleaseTime)
                    GripOff(id, ref e);
            }
            else if (!e.gripped)
            {
                e.col = null;
            }
        }

        // ---- DWELL / AGARRE ----
        if (found != null)
        {
            if (e.col == found) e.dwell += Time.deltaTime;
            else { e.col = found; e.dwell = 0f; }

            if (!e.gripped && e.dwell >= requiredHoldTime)
            {
                e.gripped = true;
                e.anchor = found.ClosestPoint(posW);

                // La última extremidad que se agarra queda BLOQUEADA como primaria
                if (latchPrimary) latchedId = id;

                var grip = e.col.GetComponent<ClimbableGrip>();
                if (grip) grip.OnGripStart();
            }
        }
        else
        {
            e.dwell = 0f;
            if (!e.gripped) e.col = null;
        }
    }

    void CheckReleaseByDistance(EffId id, ref Eff e, Vector3 current)
    {
        if (!e.gripped) return;

        // La primaria bloqueada NO se suelta por distancia
        if (latchPrimary && id == latchedId) return;

        if (Vector3.Distance(current, e.anchor) > releaseDistance)
            GripOff(id, ref e);
    }

    // Suelta (respetando bloqueo de primaria)
    void GripOff(EffId id, ref Eff e)
    {
        if (latchPrimary && id == latchedId) return; // bloqueada: ignora suelta

        if (e.gripped)
        {
            var grip = e.col ? e.col.GetComponent<ClimbableGrip>() : null;
            if (grip) grip.OnGripEnd();
        }
        e.gripped = false;
        e.col = null;
        e.dwell = 0f;
        e.noTouchTime = 0f;

        // Si soltamos la primaria por fuerza mayor (no debería ocurrir aquí), limpiar latch
        if (id == latchedId) latchedId = EffId.None;
    }

    // Suelta forzada (ignora bloqueo) – usada solo en ReleaseAll
    void ForceGripOff(ref Eff e)
    {
        if (e.gripped)
        {
            var grip = e.col ? e.col.GetComponent<ClimbableGrip>() : null;
            if (grip) grip.OnGripEnd();
        }
        e.gripped = false;
        e.col = null;
        e.dwell = 0f;
        e.noTouchTime = 0f;
    }

    void TouchOff(ref Eff e)
    {
        if (e.touchingCol)
        {
            CallTouch(e.touchingCol, false);
            e.touchingCol = null;
        }
        e.touching = false;
        // noTouchTime se maneja en UpdateEffector según caso
    }

    // ---------- Movimiento ----------
    void ApplyFreeMovement(Vector3 lateral)
    {
        verticalVel += -Mathf.Abs(gravityWhenFree) * Time.deltaTime;
        Vector3 move = lateral;
        move.y += verticalVel * Time.deltaTime;

        CollisionFlags flags = cc.Move(move);
        if ((flags & CollisionFlags.Below) != 0) verticalVel = 0f;
    }

    // Anchor delta ponderado (primaria pesa más)
    void AddAnchorDelta(EffId id, Eff e, Vector3 cur, ref Vector3 delta, ref float wSum)
    {
        if (!e.gripped) return;
        Vector3 d = (e.anchor - cur);
        float w = 1f;
        if (latchPrimary && id == latchedId) w *= Mathf.Max(1f, primaryAnchorWeight);

        delta += d * w;
        wSum += w;
    }

    float StepUpDelta(EffId id, Eff e, Vector3 cur)
    {
        if (e.gripped || !e.enabled) return 0f;
        float dy = cur.y - e.lastPos.y;
        if (dy <= minUpDelta) return 0f;

        float k = 1f;
        if (latchPrimary && id == latchedId) k *= Mathf.Max(1f, primaryStepUpMul);
        return dy * k;
    }

    float LateralDelta(EffId id, Eff e, Vector3 cur, Vector3 right)
    {
        if (e.gripped || !e.enabled) return 0f;
        float d = Vector3.Dot(cur - e.lastPos, right);
        if (Mathf.Abs(d) <= minSideDelta) return 0f;

        float k = lateralGain;
        if (latchPrimary && id == latchedId) k *= Mathf.Max(1f, primaryLateralMul);
        return Mathf.Clamp(d * k, -lateralMaxPerFrame, lateralMaxPerFrame);
    }

    // ---------- Orígenes (Kinect / Avatar) ----------
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

    // ---------- Congelar AvatarController root mientras trepo ----------
    void FreezeAvatarRoot()
    {
        if (!freezeAvatarRootWhenClimbing || avatarToFreeze == null || fiOffsetRel == null || !avatarOffsetCached)
            return;

        bool cur = (bool)fiOffsetRel.GetValue(avatarToFreeze);
        if (cur) fiOffsetRel.SetValue(avatarToFreeze, false); // desactiva movimiento relativo al sensor
    }

    void UnfreezeAvatarIfNeeded()
    {
        if (!freezeAvatarRootWhenClimbing || avatarToFreeze == null || fiOffsetRel == null || !avatarOffsetCached)
            return;

        bool cur = (bool)fiOffsetRel.GetValue(avatarToFreeze);
        if (cur != avatarOffsetOriginal)
            fiOffsetRel.SetValue(avatarToFreeze, avatarOffsetOriginal);
    }

    // ---------- Utilidades ----------
    static Vector3 ToUnity(Vector3 k) => new Vector3(k.x, k.y, -k.z);

    static void CallTouch(Collider c, bool touching)
    {
        var grip = c ? c.GetComponent<ClimbableGrip>() : null;
        if (grip) grip.SetTouched(touching);
    }

    void ForceReleaseAll()
    {
        // Apagar glows de contacto
        TouchOff(ref eHL); TouchOff(ref eHR); TouchOff(ref eFL); TouchOff(ref eFR);

        // Terminar grips activos (forzado, ignora bloqueo)
        ForceGripOff(ref eHL); ForceGripOff(ref eHR);
        ForceGripOff(ref eFL); ForceGripOff(ref eFR);

        latchedId = EffId.None;
        lastDelta = Vector3.zero;
    }
}
