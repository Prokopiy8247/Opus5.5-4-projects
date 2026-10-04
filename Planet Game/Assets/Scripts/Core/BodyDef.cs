using System;
using UnityEngine;

namespace PG
{
    public enum BodyKind { Star, Rocky, Ocean, Ice, GasGiant }

    /// <summary>
    /// Authoring definition of one celestial body: where it is in the logical
    /// (double precision) system, how big it is, and the parameters that drive
    /// its single terrain function.
    ///
    /// The terrain, the collider ground and the visual mesh all read the same
    /// numbers out of this class - that is what keeps the silhouette you see
    /// from orbit identical to the ground you land on.
    /// </summary>
    [Serializable]
    public class BodyDef
    {
        public string id;
        public string displayName;
        public BodyKind kind;

        [Tooltip("Logical position in system space, metres, star at the origin.")]
        public Vector3 logicalPosF;                 // authored as float, promoted to double
        public double radius = 3000.0;              // sea-level / datum radius, metres
        public int    seed = 1;

        // ---- terrain function ----
        // Feature sizes are authored in METRES on the surface; the noise input
        // frequency is derived as radius / size, which keeps the same art
        // direction readable for planets of different radii.
        public double continentAmp = 260.0;   // low frequency landmass relief
        public double mountainAmp  = 190.0;   // ridged mountain relief
        public double detailAmp    = 26.0;    // high frequency roughness
        public double continentSize = 2600.0; // metres, landmass wavelength
        public double mountainSize  = 420.0;
        public double detailSize    = 70.0;
        public int    octaves       = 5;
        public double warpAmount   = 0.35;    // domain warp, breaks up the grid look
        public bool   ridgedMountains = true;

        // ---- signature geology (0 disables) ----
        public double canyonDepth = 0.0;      // metres: meandering canyon network carved into the land
        public double terraceStep = 0.0;      // metres: mesa-style terracing of the uplands
        public double crackDensity = 0.0;     // 0..1: crevasse lines across ice sheets
        public double volcanicFraction = 0.0; // 0..1: share of the surface in dark volcanic fields

        // ---- surface appearance ----
        public Color  lowColor  = new Color(0.42f, 0.30f, 0.20f);
        public Color  midColor  = new Color(0.55f, 0.44f, 0.31f);
        public Color  highColor = new Color(0.80f, 0.79f, 0.76f);
        public Color  waterColor = new Color(0.06f, 0.20f, 0.36f);
        public double seaLevel = -1e9;        // -1e9 == no water at all
        public double snowLine = 1e9;         // absolute height above datum where snow starts
        public double vegetation = 0.0;       // 0..1 blend of the "vegetation" tint band

        // ---- atmosphere ----
        public double atmosphereHeight = 0.0; // 0 == airless
        public double skyOpticalDepth = 0.6;  // vertical optical depth of the visible air (sky opacity at zenith ~ 1-e^-d)
        public Color  atmosphereColor = new Color(0.35f, 0.55f, 1.0f);
        public Color  sunsetColor = new Color(1.0f, 0.48f, 0.22f);   // terminator / rim tint
        public Color  cloudColor = new Color(1f, 1f, 1f);
        public Color  fogColor = new Color(0.55f, 0.68f, 0.95f);
        public double fogDensity = 0.0;
        public double cloudCover = 0.0;
        public double cloudHeight = 900.0;
        public double ambient = 0.10;
        public double sunIntensity = 1.15;
        public double dayNightTilt = 0.0;

        // ---- gameplay ----
        public bool landable = true;
        public double gravity = 9.81;         // m/s^2 at the datum surface
        public string poiName = "";
        public string poiDesc = "";
        [Tooltip("Unit direction of the landmark, filled in at runtime by PropScatter.")]
        public double poiLocalDirX, poiLocalDirY, poiLocalDirZ;

        // ---- runtime ----
        [NonSerialized] public GameObject go;  // GameObject instance for this body

        public DVec3 LogicalPos
        {
            get => new DVec3(logicalPosF.x, logicalPosF.y, logicalPosF.z);
            set
            {
                logicalPosF.x = (float)value.x;
                logicalPosF.y = (float)value.y;
                logicalPosF.z = (float)value.z;
            }
        }

        public float SurfaceGravityAt(double r)
        {
            // Simple inverse square falloff from the datum radius.
            double k = radius / Math.Max(r, 1.0);
            return (float)(gravity * k * k);
        }
    }

    /// <summary>All tuning numbers for the compressed gameplay scale of this system.</summary>
    public static class PGConst
    {
        // Floating origin ------------------------------------------------------
        public const double OriginShiftThreshold = 900.0;   // metres from origin before rebase
        public const double OriginGrid            = 128.0;  // rebase snapping, keeps deltas exact

        // Ship - the parked height of the hull origin above the ground and the
        // gear footprint are measured from the imported model at start-up
        // (ShipController.MeasureGear); these are only the fallbacks.
        public const float  ShipLength            = 15.0f;
        public const float  ShipGroundClearance   = 2.0f;   // hull origin above ground when parked
        public const float  ShipGearSpan          = 7.0f;   // fore/aft gear separation

        public const float  SpeedAtmoMax     = 220f;    // m/s, atmospheric cruise
        public const float  SpeedAtmoBoost   = 380f;
        public const float  SpeedOrbitMax    = 650f;    // m/s, space cruise (boost x1.7)
        public const float  SpeedPulse       = 1250f;   // m/s, pulse drive (interplanetary): ~35-45 s per hop
        public const float  SpeedApproachCap = 900f;    // m/s, cap a few km above a surface
        public const float  SpeedEntryCap    = 420f;    // m/s, cap once the atmosphere is entered
        public const float  SpeedLandingMax  = 14f;     // m/s, below which touchdown is a landing

        public const float  AccelAtmo   = 60f;
        public const float  AccelOrbit  = 110f;
        public const float  AccelPulse  = 420f;
        public const float  BrakeAccel  = 160f;
        public const float  PulseChargeTime = 1.2f;     // seconds to spin up the pulse drive
        public const float  PulseAlignCone  = 20f;      // degrees: nose must be this close to the target
        public const float  PulseDropCone   = 55f;      // degrees: drive drops out beyond this

        // Terrain streaming ----------------------------------------------------
        public const int    PatchRes        = 20;       // quads per patch edge
        public const int    MaxLevel        = 7;        // level 7 patch ~18 m, quad ~0.9 m
        public const int    RootSplit       = 2;        // 2x2 root patches per cube face
        public const float  SplitFactor     = 2.6f;     // split when dist < size * this
        public const float  MergeFactor     = 3.6f;     // merge hysteresis
        public const int    MaxActivePatches = 1100;    // live nodes incl. hidden parents
        public const int    MaxSplitsPerFrame = 24;
        public const int    MeshBuildsPerFrame = 14;
        public const int    ColliderBuildsPerFrame = 4;

        public const float  CrashSpeed      = 26f;      // m/s vertical impact that damages
        public const float  MaxLandingSlope = 24f;      // degrees

        public const string SaveFileName = "pg_save.json";
    }
}
