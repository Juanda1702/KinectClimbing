using UnityEngine;

[RequireComponent(typeof(Animator))]
public class KinectHighKneeImpulseMover : MonoBehaviour
{
    [Header("Raíz de movimiento")]
    [Tooltip("Si se deja vacío, usará el padre (offset-node creado por AvatarController).")]
    public Transform moveRoot;                 // moveremos ESTE transform (idealmente el padre)
    public bool addCharacterControllerIfMissing = false;

    [Header("Dirección/rotación")]
    public bool useShoulderHeading = true;
    public bool invertForward = true;         // <- cámbialo si sigue invertido
    public float turnSmooth = 10f;

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

        // 1) Rotación/dirección (en moveRoot)
        if (useShoulderHeading && km.IsJointTracked(userId, shL) && km.IsJointTracked(userId, shR))
        {
            Vector3 sL = km.GetJointPosition(userId, shL);
            Vector3 sR = km.GetJointPosition(userId, shR);
            Vector3 across = sR - sL; across.y = 0f;
            Vector3 forwardK = Vector3.Cross(Vector3.up, across).normalized;

            if (forwardK.sqrMagnitude > 1e-4f)
            {
                Vector3 fwd = new Vector3(forwardK.x, 0f, -forwardK.z); // invierte Z de Kinect v1
                if (invertForward) fwd = -fwd;
                Quaternion target = Quaternion.LookRotation(fwd);
                moveRoot.rotation = Quaternion.Slerp(moveRoot.rotation, target, turnSmooth * Time.deltaTime);
            }
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

        // 3) Aplicar impulso en moveRoot
        Vector3 move = Vector3.zero;
        if (remainingImpulse > 0f && impulseDuration > 0f)
        {
            float stepDist = Mathf.Min(remainingImpulse, (impulseDistance / impulseDuration) * Time.deltaTime);
            remainingImpulse -= stepDist;
            move += moveRoot.forward * stepDist;
        }

        move.y -= gravity * Time.deltaTime;
        cc.Move(move);

        // Actualiza la posición objetivo tras nuestro movimiento de este frame
        _desiredPos = moveRoot.position;
        _haveDesired = true;

    }

    // --- Anti-recentrado: mantener la última posición deseada del moveRoot ---
    private Vector3 _desiredPos;
    private bool _haveDesired;

    void LateUpdate()
    {
        if (!cc || !moveRoot) return;

        // Si aún no tenemos objetivo, tomamos el actual
        if (!_haveDesired) { _desiredPos = moveRoot.position; _haveDesired = true; return; }

        // Si algún otro sistema "reseteó" el moveRoot (salto brusco hacia atrás),
        // reponemos la posición deseada para que NO regrese al origen.
        float snapBackThreshold = 0.20f; // 20 cm: ajusta si hace falta
        float dist = Vector3.Distance(moveRoot.position, _desiredPos);
        if (dist > snapBackThreshold)
        {
            moveRoot.position = _desiredPos; // vuelve a donde lo dejamos
        }
    }

    void ResetState()
    {
        calibrated = false; calibT = 0f;
        leftUp = rightUp = false;
        lastStepTime = 0f;
        lastStepSide = Side.None;
        remainingImpulse = 0f;
    }
}
