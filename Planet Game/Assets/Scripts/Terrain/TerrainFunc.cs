using System;
using UnityEngine;

namespace PG
{
    /// <summary>
    /// The single terrain function for a planet.
    ///
    /// Everything that needs ground - the streaming mesh, the analytic ship
    /// contact, the landing-site probe, the prop scatter, the orbit-view
    /// silhouette - calls into here. There is deliberately no second source of
    /// truth: the continent you see from orbit is the continent you land on.
    ///
    /// Pure math, no UnityEngine object access, so it is safe to call from
    /// background threads (Job/ThreadPool) while meshes are being built.
    /// </summary>
    public static class TerrainFunc
    {
        // ---------------------------------------------------------------- noise

        static double Fbm(int[] p, double x, double y, double z, int oct, double lac, double gain)
        {
            double sum = 0.0, amp = 1.0, f = 1.0, norm = 0.0;
            for (int i = 0; i < oct; i++)
            {
                sum  += amp * PGNoise.Perlin3(p, x * f, y * f, z * f);
                norm += amp;
                amp  *= gain;
                f    *= lac;
            }
            return norm > 0.0 ? sum / norm : 0.0;
        }

        static double RidgedFbm(int[] p, double x, double y, double z, int oct, double lac, double gain)
        {
            double sum = 0.0, amp = 1.0, f = 1.0, norm = 0.0, prev = 1.0;
            for (int i = 0; i < oct; i++)
            {
                double n = 1.0 - Math.Abs(PGNoise.Perlin3(p, x * f, y * f, z * f));
                n *= n;
                sum  += amp * n * prev;
                prev  = n;
                norm += amp;
                amp  *= gain;
                f    *= lac;
            }
            return norm > 0.0 ? (sum / norm) * 2.0 - 1.0 : 0.0;
        }

        // ------------------------------------------------------------ elevation

        /// <summary>Height in metres above the planet datum, for a unit direction.</summary>
        public static double Elevation(BodyDef b, int[] perm, DVec3 dir)
        {
            double fC = b.radius / Math.Max(b.continentSize, 1.0);
            double fM = b.radius / Math.Max(b.mountainSize, 1.0);
            double fD = b.radius / Math.Max(b.detailSize, 1.0);

            // Domain warp: three low-frequency noise fields push the sample
            // direction around so ridges are not axis-aligned with the cube grid.
            double wf = fC * 0.55;
            double w1 = PGNoise.Perlin3(perm, dir.x * wf + 11.3, dir.y * wf - 7.1,  dir.z * wf + 3.9);
            double w2 = PGNoise.Perlin3(perm, dir.x * wf - 4.7,  dir.y * wf + 19.5, dir.z * wf - 12.4);
            double w3 = PGNoise.Perlin3(perm, dir.x * wf + 26.9, dir.y * wf + 2.2,  dir.z * wf + 8.8);

            double wamp = b.warpAmount * 1.35 / Math.Max(fC, 1e-6);

            DVec3 wd = new DVec3(
                dir.x + w1 * wamp,
                dir.y + w2 * wamp,
                dir.z + w3 * wamp).normalized;

            double cont = Fbm(perm, wd.x * fC, wd.y * fC, wd.z * fC, b.octaves, 2.05, 0.5);

            // Mountains only grow out of the raised parts of the landmass field.
            double mask = PGNoise.SmoothStep(-0.05, 0.45, cont);

            double mount;
            if (b.ridgedMountains)
                mount = RidgedFbm(perm, wd.x * fM, wd.y * fM, wd.z * fM, 4, 2.11, 0.5) * mask;
            else
                mount = Fbm(perm, wd.x * fM, wd.y * fM, wd.z * fM, 4, 2.07, 0.5) * mask * 1.6;

            double det = Fbm(perm, dir.x * fD, dir.y * fD, dir.z * fD, 3, 2.31, 0.55);

            double h = cont * b.continentAmp
                     + mount * b.mountainAmp * (b.ridgedMountains ? 0.55 : 0.42)
                     + det * b.detailAmp;

            // ---- signature geology. Every term is continuous in dir, so the
            //      features are identical from orbit and at the landing site. ---
            if (b.terraceStep > 0.0 && h > 0.0)
            {
                // Mesa terracing: flat treads, steep risers, blended with the
                // raw relief so it stays natural rather than staircase-like.
                double t = h / b.terraceStep;
                double fl = Math.Floor(t);
                double f = PGNoise.SmoothStep(0.28, 0.72, t - fl);
                double terr = (fl + f) * b.terraceStep;
                h += (terr - h) * 0.6 * PGNoise.SmoothStep(20.0, 120.0, h);
            }
            if (b.canyonDepth > 0.0)
            {
                // Narrow meandering channels: where a domain-warped noise field
                // crosses zero, carve a canyon with steep walls.
                double fc = b.radius / 1100.0;
                double n = PGNoise.Perlin3(perm, wd.x * fc + 41.0, wd.y * fc - 13.0, wd.z * fc + 7.0);
                double ch = 1.0 - Math.Abs(n);
                double canyon = PGNoise.SmoothStep(0.90, 0.985, ch);
                h -= b.canyonDepth * canyon * PGNoise.SmoothStep(-60.0, 40.0, h);
            }
            if (b.crackDensity > 0.0)
            {
                double fk = b.radius / 300.0;
                double n = PGNoise.Perlin3(perm, wd.x * fk - 5.0, wd.y * fk + 17.0, wd.z * fk - 29.0);
                // Wide enough (~10-15 m) for the mid-LOD grid to sample the walls
                // smoothly instead of as a sawtooth.
                double crack = PGNoise.SmoothStep(0.915, 0.99, 1.0 - Math.Abs(n));
                h -= 13.0 * crack * b.crackDensity;
            }
            return h;
        }

        /// <summary>0..1 strength of the volcanic-field mask at a direction (0 outside fields).</summary>
        public static double VolcanicMask(BodyDef b, int[] perm, DVec3 dir)
        {
            if (b.volcanicFraction <= 0.0) return 0.0;
            double fv = b.radius / 1700.0;
            double n = Fbm(perm, dir.x * fv + 71.0, dir.y * fv - 3.0, dir.z * fv + 19.0, 3, 2.1, 0.5);
            double thr = 0.42 - b.volcanicFraction * 0.9;
            return PGNoise.SmoothStep(thr, thr + 0.12, n);
        }

        /// <summary>Absolute surface point in planet-local metres (planet centre at origin).</summary>
        public static DVec3 SurfacePoint(BodyDef b, int[] perm, DVec3 dir)
        {
            double h = Elevation(b, perm, dir);
            double r = b.radius + h;
            return new DVec3(dir.x * r, dir.y * r, dir.z * r);
        }

        /// <summary>
        /// Analytic surface normal, from finite differences on the sphere. Using
        /// the function (not the mesh) means normals agree across LOD levels and
        /// across patch borders, so no lighting seams appear when detail pops in.
        /// </summary>
        public static DVec3 SurfaceNormal(BodyDef b, int[] perm, DVec3 dir, double stepMetres)
        {
            return SurfaceNormal(b, perm, dir, Elevation(b, perm, dir), stepMetres);
        }

        /// <summary>
        /// Same as <see cref="SurfaceNormal(BodyDef,int[],DVec3,double)"/> but reuses an
        /// already-sampled elevation at <paramref name="dir"/>. Central differences
        /// would be more symmetric; one-sided differences on both tangents are
        /// enough at the step sizes used and cost two samples instead of four.
        /// </summary>
        public static DVec3 SurfaceNormal(BodyDef b, int[] perm, DVec3 dir, double h0, double stepMetres)
        {
            DVec3 up = Math.Abs(dir.y) < 0.95 ? new DVec3(0, 1, 0) : new DVec3(1, 0, 0);
            DVec3 t1 = DVec3.Cross(up, dir).normalized;
            DVec3 t2 = DVec3.Cross(dir, t1).normalized;

            double eps = stepMetres / b.radius;

            DVec3 p0 = dir * (b.radius + h0);
            DVec3 pu = SurfacePoint(b, perm, (dir + t1 * eps).normalized);
            DVec3 pv = SurfacePoint(b, perm, (dir + t2 * eps).normalized);

            DVec3 n = DVec3.Cross(pu - p0, pv - p0);
            if (DVec3.Dot(n, dir) < 0) n = -n;
            return n.normalized;
        }

        // ---------------------------------------------------------------- colour

        static Color LerpC(Color a, Color c, float t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return new Color(a.r + (c.r - a.r) * t, a.g + (c.g - a.g) * t, a.b + (c.b - a.b) * t, 1f);
        }

        /// <summary>Surface albedo from height, slope, water and snow (no noise-driven features).</summary>
        public static Color Albedo(BodyDef b, double height, DVec3 normal, DVec3 dir)
        {
            return Albedo(b, null, height, normal, dir);
        }

        /// <summary>
        /// Surface albedo. With a permutation table it also paints the
        /// planet-specific features (strata, beaches, forests, crevasses,
        /// volcanic fields) from the same noise the elevation uses.
        /// </summary>
        public static Color Albedo(BodyDef b, int[] perm, double height, DVec3 normal, DVec3 dir)
        {
            float slope = (float)PGNoise.Clamp01(1.0 - DVec3.Dot(normal, dir));   // 0 flat .. 1 vertical
            float volcanic = 0f;   // warm ground: no snow lies on the volcanic fields

            bool hasSea = b.seaLevel > -1e8;
            if (hasSea && height < b.seaLevel)
            {
                // Shallow shelf lightens, the deep stays dark. This is what shows
                // through the translucent water shell near the coast.
                float d = (float)PGNoise.Clamp01((b.seaLevel - height) / 160.0);
                Color shallow = b.kind == BodyKind.Ice ? new Color(0.55f, 0.72f, 0.80f) : new Color(0.20f, 0.55f, 0.58f);
                return LerpC(shallow, b.waterColor * 0.5f, Mathf.Sqrt(d));
            }

            Color c = LerpC(b.lowColor, b.midColor, (float)PGNoise.Clamp01((height + 120.0) / 420.0));
            c = LerpC(c, b.highColor, (float)PGNoise.Clamp01((height - 200.0) / 520.0));

            float n1 = perm != null ? (float)PGNoise.Perlin3(perm, dir.x * b.radius / 140.0, dir.y * b.radius / 140.0, dir.z * b.radius / 140.0) : 0f;

            switch (b.kind)
            {
                case BodyKind.Rocky:
                {
                    // Sedimentary strata on cliffs and canyon walls.
                    float strata = Mathf.Sin((float)(height * 0.19) + n1 * 2.2f) * 0.5f + 0.5f;
                    c = LerpC(c, new Color(c.r * 1.12f, c.g * 0.92f, c.b * 0.82f), strata * Mathf.Clamp01(slope * 3f) * 0.7f);
                    // Wind-blown pale sand on the flats.
                    float sand = Mathf.Clamp01(1f - slope * 6f) * Mathf.Clamp01(n1 * 1.5f + 0.4f);
                    c = LerpC(c, new Color(0.80f, 0.62f, 0.44f), sand * 0.45f);
                    // Dark scree at canyon floors.
                    float low = (float)PGNoise.Clamp01((-height - 40.0) / 120.0);
                    c = LerpC(c, new Color(0.30f, 0.16f, 0.12f), low * 0.6f);
                    break;
                }
                case BodyKind.Ocean:
                {
                    if (hasSea)
                    {
                        // Beach band just above the waterline.
                        float beach = 1f - (float)PGNoise.Clamp01((height - b.seaLevel - 2.0) / 10.0);
                        c = LerpC(c, new Color(0.78f, 0.72f, 0.54f), beach * (1f - Mathf.Clamp01(slope * 4f)));
                    }
                    if (b.vegetation > 0.0)
                    {
                        // Forest / meadow patches in the mid band, avoiding rock.
                        float band = (float)PGNoise.Clamp01(1.0 - Math.Abs(height - 70.0) / 300.0);
                        float flat = 1f - (float)PGNoise.Clamp01((slope - 0.20f) / 0.35f);
                        float beachClear = hasSea ? (float)PGNoise.Clamp01((height - b.seaLevel - 9.0) / 14.0) : 1f;
                        float v = band * flat * beachClear * (float)b.vegetation;
                        Color forest = new Color(0.10f, 0.30f, 0.10f);
                        Color meadow = new Color(0.36f, 0.52f, 0.20f);
                        Color veg = LerpC(meadow, forest, Mathf.Clamp01(n1 * 1.8f + 0.5f));
                        c = LerpC(c, veg, v * 0.9f);
                    }
                    break;
                }
                case BodyKind.Ice:
                {
                    // Blue glacial tint in hollows, crevasses dark blue.
                    c = LerpC(c, new Color(0.62f, 0.78f, 0.92f), Mathf.Clamp01(n1 * 0.8f + 0.3f) * 0.35f);
                    if (perm != null && b.crackDensity > 0.0)
                    {
                        double fk = b.radius / 300.0;
                        // Same lines as the elevation term (domain warp skipped: a
                        // slightly broader painted band reads well around the cut).
                        double n = PGNoise.Perlin3(perm, dir.x * fk - 5.0, dir.y * fk + 17.0, dir.z * fk - 29.0);
                        float crack = (float)PGNoise.SmoothStep(0.90, 0.99, 1.0 - Math.Abs(n));
                        c = LerpC(c, new Color(0.14f, 0.26f, 0.42f), crack * 0.5f * (float)b.crackDensity);
                    }
                    if (perm != null)
                    {
                        float vol = (float)VolcanicMask(b, perm, dir);
                        volcanic = vol;
                        Color basalt = LerpC(new Color(0.10f, 0.09f, 0.10f), new Color(0.32f, 0.12f, 0.07f),
                                             Mathf.Clamp01(n1 * 2f));
                        c = LerpC(c, basalt, vol * 0.92f);
                    }
                    break;
                }
            }

            // Rock shows through on anything steep.
            float rock = (float)PGNoise.Clamp01((slope - 0.20f) / 0.34f);
            Color rockCol = b.kind == BodyKind.Rocky ? new Color(0.44f, 0.27f, 0.19f)
                          : b.kind == BodyKind.Ice ? new Color(0.40f, 0.44f, 0.50f)
                          : new Color(0.36f, 0.33f, 0.30f);
            c = LerpC(c, rockCol, rock * 0.65f);

            // Snow line.
            float snow = (float)PGNoise.Clamp01((height - b.snowLine) / 220.0);
            snow = Math.Max(snow, rock * (float)PGNoise.Clamp01((height - b.snowLine * 0.6) / 300.0) * 0.6f);
            snow *= 1f - volcanic;
            c = LerpC(c, new Color(0.95f, 0.96f, 0.99f), snow * (1f - Mathf.Clamp01(slope * 2.5f) * 0.5f));

            return c;
        }

        // ------------------------------------------------------------- helpers

        /// <summary>
        /// Grab a plausible landing spot on a body, searching near a preferred
        /// direction. Returns the direction and the local height. Used by the
        /// "take me to the demo route" debug action and by spawn placement.
        /// </summary>
        public static DVec3 FindFlatSpot(BodyDef b, int[] perm, DVec3 prefer, double tolMetres, int tries)
        {
            DVec3 best = prefer.normalized;
            double bestSlope = double.MaxValue;
            uint s = 12345u;

            for (int i = 0; i < tries; i++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                double a1 = (s % 10000) / 10000.0 * 2.0 - 1.0;
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                double a2 = (s % 10000) / 10000.0 * 2.0 - 1.0;

                double spread = Math.Min(0.9, tolMetres / b.radius);
                DVec3 cand = (best + new DVec3(a1 * spread, a2 * spread, (a1 + a2) * spread * 0.5)).normalized;

                DVec3 n = SurfaceNormal(b, perm, cand, 6.0);
                double slope = 1.0 - DVec3.Dot(n, cand);
                if (slope < bestSlope)
                {
                    bestSlope = slope;
                    best = cand;
                }
            }
            return best;
        }

        /// <summary>
        /// Flat ground that is actually dry. A planet with oceans has plenty of
        /// flat sea floor, and a landmark, a beacon or a landing site must never
        /// end up on it - the flattest spot is not the right spot.
        ///
        /// Falls back to the flattest spot of any kind if the body has no water
        /// or nowhere dry can be found nearby.
        /// </summary>
        public static DVec3 FindFlatSpotOnLand(BodyDef b, int[] perm, DVec3 prefer, double tolMetres, int tries)
        {
            bool hasSea = b.seaLevel > -1e8;
            if (!hasSea) return FindFlatSpot(b, perm, prefer, tolMetres, tries);

            DVec3 best = prefer.normalized;
            double bestSlope = double.MaxValue;
            bool foundDry = false;
            uint s = 987654u;

            for (int i = 0; i < tries; i++)
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                double a1 = (s % 10000) / 10000.0 * 2.0 - 1.0;
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                double a2 = (s % 10000) / 10000.0 * 2.0 - 1.0;

                double spread = Math.Min(0.9, tolMetres / b.radius);
                DVec3 anchor = foundDry ? best : prefer.normalized;
                DVec3 cand = (anchor + new DVec3(a1 * spread, a2 * spread, (a1 + a2) * spread * 0.5)).normalized;

                double h = Elevation(b, perm, cand);
                if (h < b.seaLevel + 8.0) continue;      // sea floor or shoreline

                DVec3 n = SurfaceNormal(b, perm, cand, 6.0);
                double slope = 1.0 - DVec3.Dot(n, cand);
                if (slope < bestSlope)
                {
                    bestSlope = slope;
                    best = cand;
                    foundDry = true;
                }
            }

            return best;
        }

        /// <summary>Great-circle angular distance in radians between two directions.</summary>
        public static double AngleBetween(DVec3 a, DVec3 b)
        {
            double d = DVec3.Dot(a.normalized, b.normalized);
            if (d > 1.0) d = 1.0; if (d < -1.0) d = -1.0;
            return Math.Acos(d);
        }
    }
}
