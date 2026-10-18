using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Lightning somewhere in the clouds every so often: a flash light whose direction changes per
    /// strike flickers through a few pulses, and the sky brightens with it.
    /// </summary>
    public class StormLightning : MonoBehaviour
    {
        [SerializeField] Light flash;
        [Tooltip("Seconds between strikes.")]
        [SerializeField] Vector2 interval = new Vector2(5f, 14f);
        [SerializeField, Min(0f)] float intensity = 6f;
        [Tooltip("Skybox exposure multiplier at the peak of a flash.")]
        [SerializeField, Min(1f)] float skyBoost = 3f;

        Material sky;
        float baseExposure = 1f;
        float nextStrike;
        float strikeTime = -10f;
        float[] pulses = System.Array.Empty<float>();

        public void Configure(Light flashLight) => flash = flashLight;

        void OnEnable()
        {
            sky = RenderSettings.skybox;
            if (sky != null && sky.HasProperty("_Exposure"))
                baseExposure = sky.GetFloat("_Exposure");
            nextStrike = Time.time + Random.Range(2f, interval.x);
            if (flash != null)
                flash.intensity = 0f;
        }

        void OnDisable()
        {
            if (sky != null && sky.HasProperty("_Exposure"))
                sky.SetFloat("_Exposure", baseExposure);
        }

        void Update()
        {
            if (Time.time >= nextStrike)
                Strike();
            float t = Time.time - strikeTime;
            float brightness = 0f;
            // Each pulse rises at once and decays in about a tenth of a second.
            foreach (float start in pulses)
            {
                float since = t - start;
                if (since >= 0f)
                    brightness = Mathf.Max(brightness, Mathf.Exp(-since * 14f));
            }
            if (flash != null)
                flash.intensity = intensity * brightness;
            if (sky != null && sky.HasProperty("_Exposure"))
                sky.SetFloat("_Exposure", baseExposure * (1f + (skyBoost - 1f) * brightness));
        }

        void Strike()
        {
            strikeTime = Time.time;
            nextStrike = Time.time + Random.Range(interval.x, interval.y);
            int count = Random.Range(2, 5);
            pulses = new float[count];
            for (int i = 1; i < count; i++)
                pulses[i] = pulses[i - 1] + Random.Range(0.06f, 0.22f);
            if (flash != null)
                flash.transform.rotation = Quaternion.Euler(Random.Range(35f, 70f), Random.Range(0f, 360f), 0f);
        }
    }
}
