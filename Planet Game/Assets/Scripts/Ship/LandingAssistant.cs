using UnityEngine;

namespace PG
{
    /// <summary>
    /// The landing assistant.
    ///
    /// It does not move the ship to a different scene and it does not snap it.
    /// It probes the *actual* terrain function under the hull - slope, ground
    /// clearance, obstacles, and water depth if the body has water - reports a
    /// clear reason when the spot is unsuitable, and otherwise flies a visible,
    /// continuous descent in the real world.
    /// </summary>
    public static class LandingAssistant
    {
        public struct ProbeResult
        {
            public bool ok;
            public string reason;
            public double groundHeight;      // local terrain height under the ship
            public float slopeDegrees;
            public double waterDepth;
            public bool hasObstacle;
            public float clearance;          // metres between gear and ground
        }

        /// <summary>
        /// Sample a ring of points under the landing gear footprint and decide
        /// whether the ship could rest there.
        /// </summary>
        public static ProbeResult Probe(WorldStreamer ws, BodyDef body, DVec3 shipPos, DVec3 up, float airDensity)
        {
            var r = new ProbeResult { ok = false, reason = "" };
            if (body == null || ws == null) { r.reason = "no body"; return r; }

            var pr = ws.RuntimeOf(body);
            if (pr == null) { r.reason = "terrain not loaded"; return r; }

            DVec3 local = shipPos - body.LogicalPos;
            double radius = local.magnitude;
            DVec3 dir = local / radius;

            r.groundHeight = TerrainFunc.Elevation(body, pr.Perm, dir);
            r.waterDepth = body.seaLevel > -1e8 ? body.seaLevel - r.groundHeight : -1.0;

            // --- slope over the gear footprint --------------------------------
            // Three contact points: the two rear gear and the nose gear.
            DVec3 t1 = DVec3.Cross(up, new DVec3(0, 1, 0)).normalized;
            if (t1.sqrMagnitude < 1e-6) t1 = DVec3.Cross(up, new DVec3(1, 0, 0)).normalized;
            DVec3 t2 = DVec3.Cross(up, t1).normalized;

            double span = PGConst.ShipGearSpan * 0.5;
            double ang = span / body.radius;

            double hC = r.groundHeight;
            double hA = TerrainFunc.Elevation(body, pr.Perm, (dir + t1 * ang).normalized);
            double hB = TerrainFunc.Elevation(body, pr.Perm, (dir - t1 * ang).normalized);
            double hD = TerrainFunc.Elevation(body, pr.Perm, (dir + t2 * ang).normalized);
            double hE = TerrainFunc.Elevation(body, pr.Perm, (dir - t2 * ang).normalized);

            double riseA = System.Math.Abs(hA - hC);
            double riseB = System.Math.Abs(hB - hC);
            double riseD = System.Math.Abs(hD - hC);
            double riseE = System.Math.Abs(hE - hC);
            double maxRise = System.Math.Max(System.Math.Max(riseA, riseB), System.Math.Max(riseD, riseE));
            r.slopeDegrees = (float)(System.Math.Atan2(maxRise, span) * 180.0 / System.Math.PI);

            // --- obstacles: any gear point sticking up into the hull -----------
            double maxH = System.Math.Max(System.Math.Max(hA, hB), System.Math.Max(hD, hE));
            r.hasObstacle = (maxH - hC) > 2.6;
            r.clearance = (float)((radius - body.radius) - hC);

            // --- rejection rules -----------------------------------------------
            // Note the deliberate absence of "you must land on a pad": every
            // flat-enough spot on every planet is a legal landing site.
            if (r.waterDepth > 1.2)
            {
                r.reason = "water, " + r.waterDepth.ToString("F1") + " m deep";
                return r;
            }
            if (body.seaLevel > -1e8 && r.groundHeight < body.seaLevel + 0.5)
            {
                r.reason = "waterline - the shore is too soft to set down on";
                return r;
            }
            if (r.slopeDegrees > PGConst.MaxLandingSlope)
            {
                r.reason = "slope " + r.slopeDegrees.ToString("F0") + " deg is over the "
                         + PGConst.MaxLandingSlope.ToString("F0") + " deg limit";
                return r;
            }
            if (r.hasObstacle)
            {
                r.reason = "obstacle in the gear footprint";
                return r;
            }

            r.ok = true;
            r.reason = "clear (" + r.slopeDegrees.ToString("F0") + " deg)";
            return r;
        }

        /// <summary>
        /// Search outward from the point below the ship for a spot that passes
        /// the probe, and return it. This is the "choose an acceptable area"
        /// half of landing: a pilot does not accept the first patch of ground
        /// under the nose, they pick a clearing.
        ///
        /// Returns false only when there is genuinely nowhere suitable within
        /// <paramref name="maxRadiusMetres"/> — which is the honest reason to
        /// refuse, rather than "you are not on the designated pad".
        /// </summary>
        public static bool FindSuitableSite(WorldStreamer ws, BodyDef body, DVec3 shipPos,
                                            DVec3 up, float airDensity,
                                            double maxRadiusMetres,
                                            out DVec3 site, out ProbeResult best)
        {
            site = up;
            best = Probe(ws, body, shipPos, up, airDensity);
            if (best.ok) return true;

            var pr = ws.RuntimeOf(body);
            if (pr == null) return false;

            // Concentric rings, finest first, so the chosen site is as close to
            // straight-down as the terrain allows.
            double[] ringsMetres = { maxRadiusMetres * 0.12, maxRadiusMetres * 0.28,
                                     maxRadiusMetres * 0.5,  maxRadiusMetres * 0.75,
                                     maxRadiusMetres };

            double bestSlope = double.MaxValue;
            ProbeResult bestProbe = best;
            DVec3 bestDir = up;

            foreach (double r in ringsMetres)
            {
                double ang = r / body.radius;
                DVec3 t1 = DVec3.Cross(up, new DVec3(0, 1, 0)).normalized;
                if (t1.sqrMagnitude < 1e-6) t1 = DVec3.Cross(up, new DVec3(1, 0, 0)).normalized;
                DVec3 t2 = DVec3.Cross(up, t1).normalized;

                const int spokes = 12;
                for (int i = 0; i < spokes; i++)
                {
                    double a = 2.0 * System.Math.PI * i / spokes;
                    DVec3 dir = (up + t1 * (System.Math.Cos(a) * ang) + t2 * (System.Math.Sin(a) * ang)).normalized;

                    // Sample the probe at the candidate's own ground level.
                    double h = TerrainFunc.Elevation(body, pr.Perm, dir);
                    DVec3 pos = body.LogicalPos + dir * (body.radius + h + PGConst.ShipGroundClearance);
                    var p = Probe(ws, body, pos, dir, airDensity);

                    if (p.ok && p.slopeDegrees < bestSlope)
                    {
                        bestSlope = p.slopeDegrees;
                        bestProbe = p;
                        bestDir = dir;
                        // First hit in the closest ring is good enough.
                        site = bestDir;
                        best = bestProbe;
                        return true;
                    }
                }
            }

            best = bestProbe;
            return false;
        }

        /// <summary>
        /// One step of the assisted descent. Called every FixedUpdate while the
        /// ship is in ALIGNING or DESCENDING. Moves the real ship through real
        /// world space.
        ///
        /// The target is a chosen site direction rather than "straight down":
        /// the assistant flies the ship laterally onto that site while it
        /// descends, exactly as a pilot would line up a clearing.
        /// </summary>
        public static bool Step(ShipController ship, WorldStreamer ws, ref DVec3 targetDir, float airDensity, float dt)
        {
            var body = ship.SoiBody;
            if (body == null || ws == null) return true;
            var pr = ws.RuntimeOf(body);
            if (pr == null) return true;

            DVec3 toCentre = body.LogicalPos - ship.LogicalPosition;
            double radius = toCentre.magnitude;
            // "Up" is away from the planet centre, so the descent below, which
            // subtracts along it, actually moves the ship toward the ground.
            DVec3 up = -toCentre / radius;

            // ---- lateral alignment onto the chosen site ---------------------
            DVec3 siteDir = targetDir.normalized;
            double siteHeight = TerrainFunc.Elevation(body, pr.Perm, siteDir);
            DVec3 sitePos = siteDir * (body.radius + siteHeight);

            DVec3 here = -toCentre;                 // position relative to centre
            DVec3 want = sitePos - here;            // vector toward the site
            DVec3 wantRadial = up * DVec3.Dot(want, up);
            DVec3 lateral = want - wantRadial;
            double lateralLen = lateral.magnitude;

            double gap = radius - (body.radius + siteHeight + ship.GroundClearance);
            double approachSpeed = System.Math.Min(lateralLen * 0.9, 45.0);
            DVec3 lateralVel = DVec3.zero;
            if (lateralLen > 0.05)
            {
                DVec3 step = (lateral / lateralLen) * (approachSpeed * dt);
                if (step.magnitude > lateralLen) step = lateral;
                ship.LogicalPosition += step;
                lateralVel = step / System.Math.Max(dt, 1e-4);
            }

            // ---- align the hull with the local surface, keeping the heading ---
            DVec3 n = TerrainFunc.SurfaceNormal(body, pr.Perm, up, 5.0);
            Vector3 wantUp = n.ToVector3();
            Vector3 fwd = ship.Orientation * Vector3.forward;
            Vector3 flatFwd = Vector3.ProjectOnPlane(fwd, wantUp);
            if (flatFwd.sqrMagnitude < 0.01f)
                flatFwd = Vector3.ProjectOnPlane(ship.Orientation * Vector3.up, wantUp);
            if (flatFwd.sqrMagnitude < 0.01f)
                flatFwd = Vector3.Cross(wantUp, Vector3.right);
            Quaternion wantRot = Quaternion.LookRotation(flatFwd.normalized, wantUp);
            ship.Orientation = Quaternion.Slerp(ship.Orientation, wantRot, 1f - Mathf.Exp(-3.2f * dt));

            // ---- descent ----------------------------------------------------
            if (gap <= 0.08 && lateralLen < 0.6)
            {
                // Final check at the real position before committing. The ship
                // must not be set down in water or on a slope just because the
                // chosen site drifted out from under it during the approach -
                // if the ground here is no good, pick a new site and keep going
                // rather than declaring a landing that the probe would refuse.
                var here2 = Probe(ws, body, ship.LogicalPosition, up, airDensity);
                if (!here2.ok)
                {
                    if (FindSuitableSite(ws, body, ship.LogicalPosition, up, airDensity,
                                         260.0, out DVec3 newSite, out _))
                    {
                        targetDir = newSite;
                        return false;      // keep flying the approach
                    }
                    // Genuinely nowhere: hold position in the air and report it.
                    ship.LogicalVelocity = DVec3.zero;
                    ship.LandStatus = "CANNOT LAND HERE: " + here2.reason;
                    return true;
                }

                ship.LogicalVelocity = DVec3.zero;
                ship.EnterLanded();
                return true;
            }

            // Descent rate proportional to the remaining height, clamped: fast
            // from altitude, then an exponential flare to ~1.5 m/s at contact.
            // Never a snap - the last metres are the slowest part of the approach.
            double descent = System.Math.Max(1.5, System.Math.Min(90.0, gap * 0.9));
            // Hold height while still far off the site laterally.
            descent *= PGNoise.Clamp01(1.0 - lateralLen / 400.0);
            descent = System.Math.Min(descent, System.Math.Max(gap, 0.0) / System.Math.Max(dt, 1e-4));
            ship.LogicalPosition = ship.LogicalPosition - up * (descent * dt);
            ship.LogicalVelocity = -up * descent + lateralVel;
            ship.LandStatus = lateralLen > 20.0 ? "ALIGNING" : "DESCENDING";

            return false;
        }
    }
}
