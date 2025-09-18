using UnityEngine;

public class ClimbableGrip : MonoBehaviour
{
    public float holdTime = 2.0f;  // tiempo para agarrar
    private bool isHeld = false;   // ¿está siendo agarrada la presa?
    private float currentTime = 0f;

    public GameObject glowSphere; // referencia a la esfera que brillará al ser agarrada

    void Start()
    {
        if (glowSphere != null)
        {
            glowSphere.SetActive(false);  // Asegúrate de que la esfera está desactivada al inicio
        }
    }

    void Update()
    {
        if (isHeld)
        {
            currentTime += Time.deltaTime;
            if (currentTime >= holdTime)
            {
                // Después de 2 segundos, hacer brillar la esfera
                if (glowSphere != null)
                {
                    glowSphere.SetActive(true); // Enciende la esfera brillante
                }
            }
        }
        else
        {
            currentTime = 0f; // Resetea el tiempo si se ha soltado la presa
            if (glowSphere != null)
            {
                glowSphere.SetActive(false); // Apaga la esfera
            }
        }
    }

    // Llamado cuando la mano empieza a tocar la presa
    public void OnGripStart()
    {
        isHeld = true; // Comienza a contar el tiempo cuando el avatar toca la presa
        Debug.Log("¡La mano ha tocado la presa!");  // Mensaje en consola
    }

    // Llamado cuando la mano se suelta de la presa
    public void OnGripEnd()
    {
        isHeld = false; // Detén el conteo cuando el avatar suelta la presa
        Debug.Log("¡La mano ha soltado la presa!");  // Mensaje en consola
    }
}
