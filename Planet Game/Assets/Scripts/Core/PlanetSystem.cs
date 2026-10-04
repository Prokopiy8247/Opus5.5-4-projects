using System;
using System.Collections.Generic;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// The one star system this game is about: a star plus three fully landable
    /// planets. Distances are compressed gameplay distances, not astronomy.
    ///
    /// Everything here is authored in logical metres. The star sits at the
    /// origin of logical space, which is also the initial floating-origin shift.
    /// </summary>
    public class PlanetSystem : MonoBehaviour
    {
        public static PlanetSystem Instance { get; private set; }

        /// <summary>Clears the static handle. Used only by automated tests.</summary>
        public static void ResetInstance() { Instance = null; }

        public List<BodyDef> bodies = new List<BodyDef>();
        public string systemName = "Sable Reach";

        public BodyDef Star => bodies.Count > 0 ? bodies[0] : null;

        public IEnumerable<BodyDef> Planets
        {
            get
            {
                for (int i = 0; i < bodies.Count; i++)
                    if (bodies[i].kind != BodyKind.Star) yield return bodies[i];
            }
        }

        public BodyDef Get(string id)
        {
            for (int i = 0; i < bodies.Count; i++) if (bodies[i].id == id) return bodies[i];
            return null;
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (bodies.Count == 0) BuildDefaultSystem();
            CreatePlanetGameObjects();
        }

        void CreatePlanetGameObjects()
        {
            foreach (var body in bodies)
            {
                if (body.go == null)
                {
                    var go = new GameObject($"Planet_{body.id}");
                    go.transform.SetParent(transform);
                    body.go = go;
                }
            }
        }

        /// <summary>
        /// Authored system layout (compressed gameplay scale):
        ///   Helion, radius 3.5 km, at the origin;
        ///   three planets (radius 3.2-4.0 km) on a ~30 km ring, 120 degrees
        ///   apart, so every planet-to-planet line is ~52 km long and passes
        ///   ~15 km from the star's centre (11 km clear of its surface). At the
        ///   pulse drive's 1250 m/s a hop takes ~35-45 s including spool-up and
        ///   the slow-down before the target's atmosphere.
        ///   Relief is kept under ~10% of the radius and the visible air is
        ///   concentrated near the ground, so each planet reads as one solid
        ///   sphere with a thin glowing limb from space.
        /// </summary>
        public void BuildDefaultSystem()
        {
            bodies.Clear();

            // ---- Star: Helion. Decorative, never landable. -------------------
            bodies.Add(new BodyDef
            {
                id = "helion",
                displayName = "Helion",
                kind = BodyKind.Star,
                logicalPosF = new Vector3(0f, 0f, 0f),
                radius = 3500.0,
                seed = 7,
                landable = false,
                gravity = 0.0,
                sunIntensity = 1.0,
                atmosphereHeight = 0.0,
                poiName = "Helion",
                poiDesc = "G-type star. No solid surface - do not approach."
            });

            // ---- 1. Tarn-Veth: arid, rocky, rust-red. No water, thin air. ----
            bodies.Add(new BodyDef
            {
                id = "tarnveth",
                displayName = "Tarn-Veth",
                kind = BodyKind.Rocky,
                logicalPosF = new Vector3(-25981f, 3000f, -15000f),
                radius = 3500.0,
                seed = 1841,
                continentAmp = 210.0,
                mountainAmp  = 290.0,
                detailAmp    = 16.0,
                continentSize = 2700.0,
                mountainSize  = 380.0,
                detailSize    = 62.0,
                octaves = 5,
                warpAmount = 0.55,
                ridgedMountains = true,
                lowColor  = new Color(0.42f, 0.20f, 0.13f),
                midColor  = new Color(0.66f, 0.38f, 0.22f),
                highColor = new Color(0.83f, 0.74f, 0.62f),
                waterColor = new Color(0f, 0f, 0f),
                seaLevel = -1e9,
                snowLine = 720.0,
                vegetation = 0.0,
                atmosphereHeight = 1150.0,
                skyOpticalDepth = 0.36,
                atmosphereColor = new Color(0.86f, 0.52f, 0.32f),
                sunsetColor = new Color(1.0f, 0.36f, 0.14f),
                cloudColor = new Color(0.95f, 0.78f, 0.62f),
                fogColor = new Color(0.78f, 0.50f, 0.34f),
                fogDensity = 0.00042,
                cloudCover = 0.14,
                cloudHeight = 480.0,
                canyonDepth = 120.0,
                terraceStep = 38.0,
                ambient = 0.085,
                dayNightTilt = 0.14,
                gravity = 8.20,
                poiName = "The Rustglass Arch",
                poiDesc = "A 210 m natural arch of fused sandstone, cut by wind off the northern basin."
            });

            // ---- 2. Mirvalis: temperate, oceans, vegetation, thick air. -----
            bodies.Add(new BodyDef
            {
                id = "mirvalis",
                displayName = "Mirvalis",
                kind = BodyKind.Ocean,
                logicalPosF = new Vector3(25981f, -2000f, -15000f),
                radius = 4000.0,
                seed = 90210,
                continentAmp = 270.0,
                mountainAmp  = 240.0,
                detailAmp    = 13.0,
                continentSize = 3000.0,
                mountainSize  = 480.0,
                detailSize    = 70.0,
                octaves = 5,
                warpAmount = 0.45,
                ridgedMountains = false,
                lowColor  = new Color(0.24f, 0.40f, 0.20f),
                midColor  = new Color(0.38f, 0.48f, 0.24f),
                highColor = new Color(0.52f, 0.50f, 0.44f),
                waterColor = new Color(0.045f, 0.155f, 0.30f),
                seaLevel = -40.0,
                snowLine = 760.0,
                vegetation = 0.85,
                atmosphereHeight = 1600.0,
                skyOpticalDepth = 0.62,
                atmosphereColor = new Color(0.38f, 0.60f, 0.98f),
                sunsetColor = new Color(1.0f, 0.50f, 0.26f),
                cloudColor = new Color(1f, 1f, 1f),
                fogColor = new Color(0.58f, 0.72f, 0.96f),
                fogDensity = 0.00052,
                cloudCover = 0.58,
                cloudHeight = 560.0,
                ambient = 0.12,
                dayNightTilt = 0.18,
                gravity = 9.81,
                poiName = "Sunken Spire Field",
                poiDesc = "Basalt columns rising from a shallow shelf, each carved with the same helical groove."
            });

            // ---- 3. Kryosyne: glacial, plus an active volcanic rift. --------
            bodies.Add(new BodyDef
            {
                id = "kryosyne",
                displayName = "Kryosyne",
                kind = BodyKind.Ice,
                logicalPosF = new Vector3(0f, 1000f, 30000f),
                radius = 3200.0,
                seed = 553311,
                continentAmp = 220.0,
                mountainAmp  = 340.0,
                detailAmp    = 18.0,
                continentSize = 2500.0,
                mountainSize  = 420.0,
                detailSize    = 58.0,
                octaves = 5,
                warpAmount = 0.62,
                ridgedMountains = true,
                lowColor  = new Color(0.56f, 0.66f, 0.74f),
                midColor  = new Color(0.78f, 0.86f, 0.93f),
                highColor = new Color(0.94f, 0.96f, 0.99f),
                waterColor = new Color(0.10f, 0.26f, 0.36f),
                seaLevel = -150.0,
                snowLine = 120.0,
                vegetation = 0.0,
                atmosphereHeight = 1100.0,
                skyOpticalDepth = 0.52,
                atmosphereColor = new Color(0.55f, 0.72f, 0.95f),
                sunsetColor = new Color(0.78f, 0.52f, 0.95f),
                cloudColor = new Color(0.94f, 0.97f, 1f),
                fogColor = new Color(0.70f, 0.80f, 0.94f),
                crackDensity = 1.0,
                volcanicFraction = 0.16,
                fogDensity = 0.00034,
                cloudCover = 0.40,
                cloudHeight = 600.0,
                ambient = 0.10,
                dayNightTilt = 0.22,
                gravity = 7.40,
                poiName = "Emberglass Rift",
                poiDesc = "A thermal rift where the ice sheet has failed; obsidian shards and steam vents."
            });
        }

        /// <summary>Closest body to a logical point, with the distance to its centre.</summary>
        public BodyDef Nearest(DVec3 p, out double dist)
        {
            BodyDef best = null; dist = double.MaxValue;
            for (int i = 0; i < bodies.Count; i++)
            {
                double d = (bodies[i].LogicalPos - p).magnitude;
                if (d < dist) { dist = d; best = bodies[i]; }
            }
            return best;
        }

        /// <summary>
        /// Nearest body whose sphere of influence contains p. Influence radius
        /// is scaled from the body radius so the three planets do not fight over
        /// the same volume of space; the star owns everything left over.
        /// </summary>
        public BodyDef DominantBody(DVec3 p)
        {
            // The star is decorative and not landable, so it never owns the
            // player: flight always belongs to the nearest landable body, which
            // is what makes the orbital -> atmospheric handover follow the ship
            // continuously instead of flipping at an arbitrary boundary.
            BodyDef best = null;
            double bestClearance = double.MaxValue;

            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (!b.landable) continue;
                double clearance = (b.LogicalPos - p).magnitude - b.radius;
                if (clearance < bestClearance) { bestClearance = clearance; best = b; }
            }

            if (best != null) return best;

            // No landable body at all (only possible if a scene ships a star
            // alone): fall back to whatever is nearest.
            double bd = double.MaxValue;
            for (int i = 0; i < bodies.Count; i++)
            {
                double d = (bodies[i].LogicalPos - p).magnitude;
                if (d < bd) { bd = d; best = bodies[i]; }
            }
            return best ?? Star;
        }
    }
}
