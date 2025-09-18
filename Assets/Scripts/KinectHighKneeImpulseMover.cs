using UnityEngine;
using System.Linq;
using System.Reflection;

[RequireComponent(typeof(Animator))]
public class KinectHighKneeImpulseMover : MonoBehaviour
{
    [Header("Raíz de movimiento")]
    [Tooltip("Si se deja vacío, usará el padre (offset-node creado por AvatarController).")]
    public Transform moveRoot;                         // moveremos ESTE transform (idealmente el padre)
    public bool addCharacterControllerIfMissing = false;

    [Header("Dirección / Rotación")]
    public enum HeadingMode { Shoulders = 0, Camera = 1 /*(HMD/VR)*/, }
    [Tooltip("Fuente de la dirección de avance")]
    public HeadingMode headingMode = HeadingMode.Shoulders;

    [Tooltip("Cámara para HeadingMode.Camera (p.ej. la del XR Origin). Si está vacío intenta Camera.main.")]
    public Camera headingCamera;

    [Tooltip("Suavizado del heading (cuanto mayor, más suave).")]
    public float headingSmooth = 12f;

    [Tooltip("Hacer que el root también gire hacia el heading.")]
    public bool alignRotationToHeading = true;

    [Tooltip("Suavizado de giro del root si alignRotationToHeading está activo.")]
    public float turnSmooth = 10f;

    [Tooltip("Sólo para heading por Kinect (hombros): invierte el sentido si queda al revés.")]
    public bool invertForward = true;

    [Header("Detección de rodillas (paso)")]
    public float kneeLiftThreshold = 0.12f;
    public float hysteresis = 0.02f;
    public float minStepInterval = 0.30f;
    public float maxStepInterval = 1.4f;
    public bool requireAlternation = true;

    [Header("Impulso por paso")]
    public float impulseDistance = 0.28f;     // avance por paso (m)
    public float impulseDuration = 0.18f;     // tiempo en aplicar el impulso (s)
    public float maxStackedDistance = 0.6f;   // evita acumular demasiado

    [Header("Física")]
    public float gravity = 9.81f;

    // Internos
    CharacterController cc;
    KinectManager km;
    uint userId;

    int hipC, kneeL, kneeR, shL, shR;

    enum Side { None, Left, Right }
    Side lastStepSide = Side.None;

    bool leftUp, rightUp;
    float lastStepTime;

    float remainingImpulse = 0f;

    bool calibrated; float baseDL, baseDR, calibT, needCalibTime = 0.8f;

    // heading suavizado
    Vector3 smoothedHeading = Vector3.forward;
    bool headingInitialized = false;

    // Anti-recentrado
    private Vector3 _desiredPos;
    private bool _haveDesired;

    void Awake()
    {
        // mover el PADRE (offset-node) para no pelear con AvatarController
        if (!moveRoot) moveRoot = transform.parent != null ? transform.parent : transform;

        // CharacterController debe estar en moveRoot (no en el avatar)
        cc = moveRoot.GetComponent<CharacterController>();
        if (!cc && addCharacterControllerIfMissing)
        {
            cc = moveRoot.gameObject.AddComponent<CharacterController>();
            cc.radius = 0.3f; cc.height = 1.7f; cc.center = new Vector3(0, 0.9f, 0);
        }

        // Auto-resolver cámara si falta (útil en VR)
        if (!headingCamera) headingCamera = Camera.main;

        hipC = (int)KinectWrapper.NuiSkeletonPositionIndex.HipCenter;
        kneeL = (int)KinectWrapper.NuiSkeletonPositionIndex.KneeLeft;
        kneeR = (int)KinectWrapper.NuiSkeletonPositionIndex.KneeRight;
        shL = (int)KinectWrapper.NuiSkeletonPositionIndex.ShoulderLeft;
        shR = (int)KinectWrapper.NuiSkeletonPositionIndex.ShoulderRight;
    }

    void OnEnable() { ResetState(); }

    void Update()
    {
        if (!cc) return;

        if (km == null) km = KinectManager.Instance;
        if (km == null) return;
        if (!KinectHelpers.TryGetFirstUserId(km, out userId)) { ResetState(); return; }

        if (!km.IsJointTracked(userId, hipC) || !km.IsJointTracked(userId, kneeL) || !km.IsJointTracked(userId, kneeR))
        { ResetState(); return; }

        // 1) Calcular heading (dirección de mirar)
        Vector3 heading = ComputeHeading();
        if (!headingInitialized)
        {
            smoothedHeading = heading;
            headingInitialized = true;
        }
        else
        {
            float t = Mathf.Clamp01(headingSmooth * Time.deltaTime);
            smoothedHeading = Vector3.Slerp(smoothedHeading, heading, t);
        }

        // Rotar el root hacia el heading si se desea
        if (alignRotationToHeading && smoothedHeading.sqrMagnitude > 1e-6f)
        {
            Quaternion target = Quaternion.LookRotation(smoothedHeading);
            moveRoot.rotation = Quaternion.Slerp(moveRoot.rotation, target, turnSmooth * Time.deltaTime);
        }

        // 2) Lectura de rodillas y pasos
        Vector3 hip = km.GetJointPosition(userId, hipC);
        Vector3 kL = km.GetJointPosition(userId, kneeL);
        Vector3 kR = km.GetJointPosition(userId, kneeR);

        float dL = Mathf.Max(0f, hip.y - kL.y);
        float dR = Mathf.Max(0f, hip.y - kR.y);

        if (!calibrated)
        {
            calibT += Time.deltaTime;
            baseDL = Mathf.Lerp(baseDL, dL, 0.2f);
            baseDR = Mathf.Lerp(baseDR, dR, 0.2f);
            if (calibT >= needCalibTime) calibrated = true;
        }
        else
        {
            float thUp = kneeLiftThreshold;
            float thDown = kneeLiftThreshold + hysteresis;

            bool lUpNow = leftUp ? (dL < thDown) : (dL < thUp);
            bool rUpNow = rightUp ? (dR < thDown) : (dR < thUp);

            bool step = false; Side thisSide = Side.None;
            float now = Time.time;

            if (lUpNow && !leftUp && !rUpNow) { step = true; thisSide = Side.Left; }
            if (rUpNow && !rightUp && !lUpNow) { step = true; thisSide = Side.Right; }

            leftUp = lUpNow; rightUp = rUpNow;

            if (step)
            {
                float dt = now - lastStepTime;
                bool okInterval = (lastStepTime > 0 && dt >= minStepInterval && dt <= maxStepInterval);
                bool okAlt = !requireAlternation || (thisSide != lastStepSide);

                if (okInterval && okAlt)
                {
                    remainingImpulse = Mathf.Min(remainingImpulse + impulseDistance, maxStackedDistance);
                    lastStepSide = thisSide;
                }
                lastStepTime = now;
            }
        }

        // 3) Aplicar impulso en la dirección de mirada
        Vector3 move = Vector3.zero;
        if (remainingImpulse > 0f && impulseDuration > 0f && smoothedHeading.sqrMagnitude > 1e-6f)
        {
            float stepDist = Mathf.Min(remainingImpulse, (impulseDistance / impulseDuration) * Time.deltaTime);
            remainingImpulse -= stepDist;
            move += smoothedHeading * stepDist;
        }

        move.y -= gravity * Time.deltaTime;
        cc.Move(move);

        // Anti-recentrado: guardar posición objetivo
        _desiredPos = moveRoot.position;
        _haveDesired = true;
    }

    // --- Anti-recentrado: mantener la última posición deseada del moveRoot ---
    void LateUpdate()
    {
        if (!cc || !moveRoot) return;

        if (!_haveDesired) { _desiredPos = moveRoot.position; _haveDesired = true; return; }

        float snapBackThreshold = 0.20f; // 20 cm
        float dist = Vector3.Distance(moveRoot.position, _desiredPos);
        if (dist > snapBackThreshold)
        {
            moveRoot.position = _desiredPos;
        }
    }

    // ---- Helpers ----
    Vector3 ComputeHeading()
    {
        // 1) Cámara (VR/HMD): usar forward de la cámara, proyectado en el plano XZ
        if (headingMode == HeadingMode.Camera && headingCamera)
        {
            Vector3 f = headingCamera.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude > 1e-6f) return f.normalized;
        }

        // 2) Hombros (Kinect): similar a la versión original
        if (km.IsJointTracked(userId, shL) && km.IsJointTracked(userId, shR))
        {
            Vector3 sL = km.GetJointPosition(userId, shL);
            Vector3 sR = km.GetJointPosition(userId, shR);
            Vector3 across = sR - sL; across.y = 0f;
            Vector3 forwardK = Vector3.Cross(Vector3.up, across).normalized;

            if (forwardK.sqrMagnitude > 1e-6f)
            {
                // Convertir a coords de Unity (Kinect v1 Z invertido)
                Vector3 fwd = new Vector3(forwardK.x, 0f, -forwardK.z);
                if (invertForward) fwd = -fwd;
                return fwd;
            }
        }

        // 3) Fallback: forward actual del root
        Vector3 fallback = moveRoot.forward; fallback.y = 0f;
        return fallback.sqrMagnitude > 1e-6f ? fallback.normalized : Vector3.forward;
    }

    void ResetState()
    {
        calibrated = false; calibT = 0f;
        leftUp = rightUp = false;
        lastStepTime = 0f;
        lastStepSide = Side.None;
        remainingImpulse = 0f;

        headingInitialized = false;
        smoothedHeading = Vector3.forward;
        _haveDesired = false;
    }
}
