using System.Linq;
using UnityEngine;

/// Colócalo en el MISMO GameObject que tiene el SphereCollider de la presa.
/// - glowSphere: objeto visual que se enciende mientras la presa está siendo TOCADA
/// - SetTouched(true/false): lo llama el KinectClimber cuando una mano/pie toca/deja de tocar
/// - OnGripStart/OnGripEnd: eventos de agarre confirmado (dwell cumplido), opcionalmente con sfx/log
public class ClimbableGrip : MonoBehaviour
{
    [Header("Feedback visual")]
    [Tooltip("Se activa mientras haya al menos una extremidad TOCANDO esta presa.")]
    public GameObject glowSphere;

    [Header("Sonido (opcional)")]
    public AudioSource sfx;              // opcional, puede ser el mismo GO o un hijo
    public AudioClip touchClip;          // al empezar a tocar
    public AudioClip untouchClip;        // al dejar de tocar
    public AudioClip gripClip;           // al confirmar agarre
    public AudioClip ungripClip;         // al soltar agarre

    [Header("Debug")]
    public bool logEvents = false;

    // Contadores para manejar múltiples efectores sobre la misma presa
    int touchCount = 0;   // # de manos/pies tocando (para glow)
    int gripCount = 0;   // # de manos/pies agarrados (sólo para logs/sfx)

    /// Llamado por el trepador cuando una extremidad empieza/deja de TOCAR esta presa.
    public void SetTouched(bool touching)
    {
        if (touching) touchCount++;
        else touchCount = Mathf.Max(0, touchCount - 1);

        if (glowSphere) glowSphere.SetActive(touchCount > 0);

        if (logEvents)
            Debug.Log($"[{name}] Touch {(touching ? "ON" : "OFF")}  (touchCount={touchCount})", this);

        // Sfx (opcional)
        if (sfx)
        {
            var clip = touching ? touchClip : untouchClip;
            if (clip) sfx.PlayOneShot(clip);
        }
    }

    /// Llamado por el trepador cuando se CONFIRMA un agarre (tras cumplir dwell).
    public void OnGripStart()
    {
        gripCount++;
        if (logEvents)
            Debug.Log($"[{name}] Grip START  (gripCount={gripCount})", this);

        if (sfx && gripClip) sfx.PlayOneShot(gripClip);
    }

    /// Llamado por el trepador cuando se SUELTA un agarre (por distancia/no contacto, etc.).
    public void OnGripEnd()
    {
        gripCount = Mathf.Max(0, gripCount - 1);
        if (logEvents)
            Debug.Log($"[{name}] Grip END    (gripCount={gripCount})", this);

        if (sfx && ungripClip) sfx.PlayOneShot(ungripClip);
    }

    void OnEnable()
    {
        // Asegura estado inicial del glow (apagado si no hay toques).
        if (glowSphere) glowSphere.SetActive(touchCount > 0);
    }

    void OnDisable()
    {
        // Al desactivar el componente, apaga el glow para evitar que quede encendido.
        if (glowSphere) glowSphere.SetActive(false);
        touchCount = 0;
        gripCount = 0;
    }

#if UNITY_EDITOR
    // Quality of life: si no arrastras el glowSphere, intenta auto-localizar un hijo razonable.
    void OnValidate()
    {
        if (!glowSphere)
        {
            // Busca un hijo llamado algo como "Glow" o "Sphere"
            var t = GetComponentsInChildren<Transform>(true)
                    .FirstOrDefault(x =>
                        x != transform &&
                        (x.name.ToLower().Contains("glow") || x.name.ToLower().Contains("sphere")));
            if (t) glowSphere = t.gameObject;
        }
    }
#endif
}
