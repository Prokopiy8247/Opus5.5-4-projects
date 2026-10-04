using UnityEngine;

namespace PG
{
    /// <summary>
    /// Ship sound, synthesised at start-up (original audio, no downloaded or
    /// third-party assets): engine hum that follows thrust, a boost layer,
    /// atmospheric wind/entry roar, a pulse-drive tone, and one-shots for
    /// touchdown, lift-off and UI feedback.
    /// </summary>
    public class ShipAudio : MonoBehaviour
    {
        public ShipController ship;
        public AtmosphereSystem atmosphere;

        AudioSource engine, wind, pulse, oneShot;
        AudioClip thump, blip, warn;
        float warnCooldown;
        bool lastWarn;

        const int Rate = 44100;

        void Start()
        {
            if (ship == null) ship = GetComponent<ShipController>();
            engine = MakeLoop("PG_Engine", BuildEngine(), 0.3f);
            wind = MakeLoop("PG_Wind", BuildNoise(3f, 0.035f, 7), 0f);
            pulse = MakeLoop("PG_Pulse", BuildPulse(), 0f);
            oneShot = gameObject.AddComponent<AudioSource>();
            oneShot.playOnAwake = false;
            oneShot.spatialBlend = 0f;
            thump = BuildThump();
            blip = BuildBlip(880f, 0.07f);
            warn = BuildBlip(560f, 0.16f);
            if (ship != null)
            {
                ship.Touchdown += () => oneShot.PlayOneShot(thump, 0.9f);
                ship.LiftOff += () => oneShot.PlayOneShot(blip, 0.5f);
            }
        }

        public void UiBlip() { if (oneShot != null) oneShot.PlayOneShot(blip, 0.35f); }

        AudioSource MakeLoop(string name, AudioClip clip, float vol)
        {
            var a = gameObject.AddComponent<AudioSource>();
            a.clip = clip;
            a.loop = true;
            a.playOnAwake = false;
            a.spatialBlend = 0f;
            a.volume = vol;
            a.Play();
            return a;
        }

        void Update()
        {
            if (ship == null || engine == null) return;
            float load = ship.MotorDisabled ? 0.05f : ship.ThrustLoad;
            bool landed = ship.Landed;
            engine.pitch = Mathf.Lerp(0.70f, 1.45f, load) * (ship.boost ? 1.12f : 1f);
            engine.volume = landed ? 0.10f : Mathf.Lerp(0.16f, 0.55f, load) * (ship.boost ? 1.25f : 1f);

            float air = ship.AirDensity;
            float heat = atmosphere != null ? atmosphere.EntryHeat : 0f;
            wind.volume = Mathf.Clamp01(air * Mathf.Clamp01(ship.Speed / 260f) * 0.45f + heat * 0.55f);
            wind.pitch = Mathf.Lerp(0.7f, 1.4f, Mathf.Clamp01(ship.Speed / 450f));

            float p = ship.PulseEngaged ? 1f : (ship.PulseSpooling ? ship.PulseCharge * 0.7f : 0f);
            pulse.volume = Mathf.Lerp(pulse.volume, p * 0.4f, 1f - Mathf.Exp(-Time.deltaTime * 4f));
            pulse.pitch = 0.8f + p * 0.5f;

            warnCooldown -= Time.deltaTime;
            bool w = ship.CollisionWarning || ship.StarWarning;
            if (w && (warnCooldown <= 0f || !lastWarn)) { oneShot.PlayOneShot(warn, 0.45f); warnCooldown = 0.6f; }
            lastWarn = w;
        }

        // ------------------------------------------------------------ synthesis

        static AudioClip BuildEngine()
        {
            int len = Rate * 2;
            var d = new float[len];
            var rnd = new System.Random(4242);
            double lp = 0.0;
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / Rate;
                double f = 55.0;
                double s = 0.0;
                for (int h = 1; h <= 7; h++) s += System.Math.Sin(t * f * h * System.Math.PI * 2.0) / (h * 1.3);
                double noise = rnd.NextDouble() * 2.0 - 1.0;
                lp += (noise - lp) * 0.08;
                double amp = 0.6 + 0.08 * System.Math.Sin(t * System.Math.PI * 2.0 * 2.5);   // 2 s loop: integer cycles
                d[i] = (float)((s * 0.16 + lp * 0.55) * amp * 0.55);
            }
            d = Crossfade(d, Rate / 20);
            var c = AudioClip.Create("PG_EngineLoop", d.Length, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip BuildNoise(float seconds, float smooth, int seed)
        {
            int len = (int)(Rate * seconds);
            var d = new float[len];
            var rnd = new System.Random(seed);
            double a = 0.0, b = 0.0;
            for (int i = 0; i < len; i++)
            {
                double n = rnd.NextDouble() * 2.0 - 1.0;
                a += (n - a) * smooth;
                b += (a - b) * smooth * 2.0;
                d[i] = (float)(b * 3.0);
            }
            d = Crossfade(d, Rate / 10);
            var c = AudioClip.Create("PG_WindLoop", d.Length, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip BuildPulse()
        {
            int len = Rate * 2;
            var d = new float[len];
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / Rate;
                double wob = 1.0 + 0.4 * System.Math.Sin(t * System.Math.PI * 2.0 * 4.0);
                double s = System.Math.Sin(t * 110.0 * System.Math.PI * 2.0) * 0.5
                         + System.Math.Sin(t * 165.0 * System.Math.PI * 2.0) * 0.3
                         + System.Math.Sin(t * 220.5 * System.Math.PI * 2.0) * 0.15;
                d[i] = (float)(s * wob * 0.25);
            }
            d = Crossfade(d, Rate / 20);
            var c = AudioClip.Create("PG_PulseLoop", d.Length, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip BuildThump()
        {
            int len = (int)(Rate * 0.45);
            var d = new float[len];
            var rnd = new System.Random(77);
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / Rate;
                double env = System.Math.Exp(-t * 11.0);
                double f = 70.0 - 30.0 * t;
                d[i] = (float)((System.Math.Sin(t * f * System.Math.PI * 2.0) * 0.8 + (rnd.NextDouble() * 2 - 1) * 0.25 * System.Math.Exp(-t * 30.0)) * env);
            }
            var c = AudioClip.Create("PG_Touchdown", len, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        static AudioClip BuildBlip(float hz, float seconds)
        {
            int len = (int)(Rate * seconds);
            var d = new float[len];
            for (int i = 0; i < len; i++)
            {
                double t = (double)i / Rate;
                double env = System.Math.Min(1.0, t * 200.0) * System.Math.Exp(-t * 18.0);
                d[i] = (float)(System.Math.Sin(t * hz * System.Math.PI * 2.0) * env * 0.5);
            }
            var c = AudioClip.Create("PG_Blip", len, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        /// <summary>
        /// Blend the last n samples into the first n and drop them, so the
        /// loop's end flows into its start without a click.
        /// </summary>
        static float[] Crossfade(float[] d, int n)
        {
            n = Mathf.Min(n, d.Length / 4);
            var o = new float[d.Length - n];
            System.Array.Copy(d, o, o.Length);
            for (int i = 0; i < n; i++)
            {
                float k = (float)i / n;
                o[i] = d[i] * k + d[d.Length - n + i] * (1f - k);
            }
            return o;
        }
    }
}
