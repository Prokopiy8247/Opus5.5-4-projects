using UnityEngine;

namespace PG
{
    /// <summary>
    /// The ship's explosion: a fireball, a spray of sparks and a light flash,
    /// built from code on first use (no assets) and replayed for every later
    /// loss. It hangs on the ship root in local space, so it stays on the wreck
    /// through origin shifts, and borrows the exhaust's additive material.
    /// </summary>
    public class ShipExplosion : MonoBehaviour
    {
        ParticleSystem fire, sparks;
        Light flash;
        float age;

        public static void Play(ShipController ship)
        {
            var fx = ship.GetComponentInChildren<ShipExplosion>(true);
            if (fx == null)
            {
                var go = new GameObject("Explosion");
                go.transform.SetParent(ship.transform, false);
                fx = go.AddComponent<ShipExplosion>();
                fx.Build(ExhaustMaterial(ship));
            }
            fx.Fire();
        }

        static Material ExhaustMaterial(ShipController ship)
        {
            if (ship.thrusterParticles != null)
                foreach (var ps in ship.thrusterParticles)
                    if (ps != null) return ps.GetComponent<ParticleSystemRenderer>().sharedMaterial;
            return null;
        }

        void Build(Material mat)
        {
            fire = MakeBurst("Fireball", mat, 70, 0.7f, 1.9f, 6f, 34f, 5f, 13f, 2.5f,
                             new Color(1f, 0.95f, 0.75f), new Color(1f, 0.45f, 0.12f), new Color(0.45f, 0.10f, 0.03f));
            sparks = MakeBurst("Sparks", mat, 120, 0.6f, 1.7f, 40f, 130f, 0.4f, 1.1f, 0.5f,
                               new Color(1f, 1f, 0.9f), new Color(1f, 0.75f, 0.35f), new Color(1f, 0.35f, 0.10f));

            flash = gameObject.AddComponent<Light>();
            flash.type = LightType.Point;
            flash.color = new Color(1f, 0.72f, 0.40f);
            flash.range = 420f;
            flash.shadows = LightShadows.None;
            flash.enabled = false;
        }

        ParticleSystem MakeBurst(string name, Material mat, int count, float lifeMin, float lifeMax,
                                 float speedMin, float speedMax, float sizeMin, float sizeMax, float radius,
                                 Color hot, Color mid, Color cold)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 1f;
            main.loop = false;
            main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
            main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
            main.startColor = Color.white;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = count;
            var em = ps.emission;
            em.rateOverTime = 0f;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = radius;
            // The blast slows down quickly instead of flying apart at a constant rate.
            var lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.limit = speedMin * 0.5f;
            lim.dampen = 0.08f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(hot, 0f), new GradientColorKey(mid, 0.3f), new GradientColorKey(cold, 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.7f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var sz = ps.sizeOverLifetime;
            sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.5f, 1f, 1.6f));
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        void Fire()
        {
            age = 0f;
            fire.Clear(); sparks.Clear();
            fire.Play(); sparks.Play();
            flash.intensity = 9f;
            flash.enabled = true;
        }

        void Update()
        {
            if (!flash.enabled) return;
            age += Time.deltaTime;
            flash.intensity = 9f * Mathf.Exp(-age * 4f);
            if (age > 1.5f) flash.enabled = false;
        }
    }
}
