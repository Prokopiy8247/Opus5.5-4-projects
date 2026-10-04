using System;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// Double-precision 3-vector used for *logical* (system) coordinates.
    /// Distances in this project are tens of kilometres and the floating-origin
    /// rebase must be exact, so logical positions are kept in double precision.
    /// </summary>
    [Serializable]
    public struct DVec3
    {
        public double x, y, z;

        public DVec3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }

        public static readonly DVec3 zero = new DVec3(0, 0, 0);

        public static DVec3 operator +(DVec3 a, DVec3 b) => new DVec3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static DVec3 operator -(DVec3 a, DVec3 b) => new DVec3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static DVec3 operator -(DVec3 a) => new DVec3(-a.x, -a.y, -a.z);
        public static DVec3 operator *(DVec3 a, double s) => new DVec3(a.x * s, a.y * s, a.z * s);
        public static DVec3 operator *(double s, DVec3 a) => new DVec3(a.x * s, a.y * s, a.z * s);
        public static DVec3 operator /(DVec3 a, double s) => new DVec3(a.x / s, a.y / s, a.z / s);

        public double sqrMagnitude => x * x + y * y + z * z;
        public double magnitude => Math.Sqrt(x * x + y * y + z * z);

        public DVec3 normalized
        {
            get
            {
                double m = magnitude;
                return m > 1e-12 ? new DVec3(x / m, y / m, z / m) : zero;
            }
        }

        public static double Dot(DVec3 a, DVec3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        public static DVec3 Cross(DVec3 a, DVec3 b) => new DVec3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);

        public static double Distance(DVec3 a, DVec3 b) => (a - b).magnitude;

        public static DVec3 Lerp(DVec3 a, DVec3 b, double t) => a + (b - a) * t;

        /// <summary>Render-space position: logical minus the current origin offset.</summary>
        public Vector3 ToRender(DVec3 origin) => new Vector3((float)(x - origin.x), (float)(y - origin.y), (float)(z - origin.z));

        public static DVec3 FromRender(Vector3 v, DVec3 origin) => new DVec3(origin.x + v.x, origin.y + v.y, origin.z + v.z);

        public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);

        public static DVec3 From(Vector3 v) => new DVec3(v.x, v.y, v.z);

        public override string ToString() => $"({x:F1}, {y:F1}, {z:F1})";
    }

    /// <summary>Deterministic hashing + Perlin noise used by every planet's terrain function.</summary>
    public static class PGNoise
    {
        public static uint Hash(int a, int seed)
        {
            unchecked
            {
                uint h = (uint)(a * 374761393) + (uint)(seed * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return h ^ (h >> 16);
            }
        }

        /// <summary>Deterministic 0..255 permutation table for a planet seed.</summary>
        public static int[] BuildPerm(int seed)
        {
            int[] p = new int[512];
            int[] src = new int[256];
            for (int i = 0; i < 256; i++) src[i] = i;
            // Fisher-Yates using the seed hash chain - no System.Random, fully reproducible.
            uint state = (uint)(seed * 2654435761u) ^ 0x9E3779B9u;
            for (int i = 255; i > 0; i--)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                int j = (int)(state % (uint)(i + 1));
                int tmp = src[i]; src[i] = src[j]; src[j] = tmp;
            }
            for (int i = 0; i < 256; i++) { p[i] = src[i]; p[i + 256] = src[i]; }
            return p;
        }

        static double Fade(double t) => t * t * t * (t * (t * 6.0 - 15.0) + 10.0);

        static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        /// <summary>Classic 3D Perlin gradient noise, deterministic, range roughly [-1, 1].</summary>
        public static double Perlin3(int[] perm, double x, double y, double z)
        {
            int X = (int)Math.Floor(x) & 255;
            int Y = (int)Math.Floor(y) & 255;
            int Z = (int)Math.Floor(z) & 255;
            x -= Math.Floor(x); y -= Math.Floor(y); z -= Math.Floor(z);

            double u = Fade(x), v = Fade(y), w = Fade(z);

            int A = perm[X] + Y, AA = perm[A] + Z, AB = perm[A + 1] + Z;
            int B = perm[X + 1] + Y, BA = perm[B] + Z, BB = perm[B + 1] + Z;

            double lerp(double a, double b, double t) => a + t * (b - a);

            return lerp(
                lerp(lerp(Grad(perm[AA], x, y, z), Grad(perm[BA], x - 1, y, z), u),
                     lerp(Grad(perm[AB], x, y - 1, z), Grad(perm[BB], x - 1, y - 1, z), u), v),
                lerp(lerp(Grad(perm[AA + 1], x, y, z - 1), Grad(perm[BA + 1], x - 1, y, z - 1), u),
                     lerp(Grad(perm[AB + 1], x, y - 1, z - 1), Grad(perm[BB + 1], x - 1, y - 1, z - 1), u), v),
                w);
        }

        /// <summary>Ridged variant - sharp mountain crests instead of rolling hills.</summary>
        public static double Ridged(double n) => 1.0 - Math.Abs(n) * 2.0;

        public static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        public static double SmoothStep(double a, double b, double t)
        {
            t = Clamp01((t - a) / (b - a + 1e-12));
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
