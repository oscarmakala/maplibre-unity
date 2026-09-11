using System;
using System.Collections.Generic;

namespace MapLibre.Unity.Source
{
    /// <summary>
    /// Clips polygon rings to an axis-aligned box (Sutherland-Hodgman).
    /// <para>
    /// Used by <see cref="GeoJsonToVectorTile"/>. Before this existed, GenerateTile did a
    /// bounding-box OVERLAP test and then encoded the WHOLE ring into the tile, so a source
    /// polygon larger than one tile was duplicated -- in full -- into every tile it touched.
    /// Measured on the HealthAtlas twin (2026-09-10): four isochrone rings accounted for 118
    /// of 135 meshes and most of 280,311 vertices, each drawn 4+ times with faint seams.
    /// </para>
    /// <para>
    /// Coordinates are plain doubles in whatever space the caller uses. GenerateTile calls it
    /// in Mercator space against the already-buffered tile box, i.e. BEFORE the scale-to-tile
    /// step, so the buffer that the overlap test uses is the same buffer the clip uses and
    /// neighbouring tiles overlap by that margin rather than abutting.
    /// </para>
    /// <para>
    /// KNOWN ARTEFACT -- the pinhole seam, unresolved. Clipping is NOT seam-free. A
    /// controlled Web capture of the HealthAtlas twin (same app build, only this package's pin
    /// differing) counted 248 one-pixel holes inside the isochrone band fill against 207
    /// before clipping -- roughly 41 new ones, in short regularly-spaced near-vertical lines
    /// along tile boundaries, each a partial-coverage pixel with the backdrop showing through.
    /// Because of it, the consuming repo HELD this branch and reverted its pin; do not offer
    /// R7(b) upstream until it is resolved.
    /// </para>
    /// <para>
    /// Why <see cref="Simplify"/> exists, and what it did NOT do. It was written against a
    /// HYPOTHESIS, and that hypothesis was FALSIFIED on 2026-09-11. The hypothesis: the holes
    /// are a crack inside ONE tile's own mesh, because Sutherland-Hodgman repeats a vertex
    /// sitting on a clip edge and a ring crossing an edge at a shallow angle leaves runs of
    /// points collinear to far below the precision EncodePolygons can encode; rounded to
    /// integers those would become zero-length edges and zero-area ears, and where
    /// EarClipTriangulator drops one a pixel goes missing. Simplify removes exactly those
    /// points. It works as designed -- 3,093 fewer vertices reach the renderer, 198,424 down
    /// to 195,331 -- and the artefact did not move: 248 holes before, 248 after, at the SAME
    /// pixel coordinates with the same luminances, while the geometry underneath them changed.
    /// If the holes were dropped ears caused by these vertices, changing these vertices would
    /// have moved them. They did not, so they are not.
    /// </para>
    /// <para>
    /// What that rules out, and where to look next. Ruled out: the vertices of the rings being
    /// clipped (above). Ruled out by construction: a gap BETWEEN tiles -- adjacent tiles clip
    /// against their own buffered boxes and overlap by 2x the buffer (~6 px at that zoom), and
    /// VectorTileMeshBuilder never clamps to the extent. What is left is that the holes are
    /// locked to TILE BOUNDARIES IN WORLD SPACE, independent of ring detail, which points
    /// DOWNSTREAM of the mesh builder: a per-tile shader clip or discard at the tile quad edge,
    /// the tile GameObject's bounds, or two tile meshes rasterising a shared world plane. That
    /// is a renderer question, not a clipper question, which is why nothing further was changed
    /// here and no second fix was guessed. Simplify is kept because it is a real reduction and
    /// because reverting it would only re-add vertices the integer encoder collapses anyway --
    /// not because it fixes anything. Every measurement is in the consuming repo's
    /// docs/increment-1a-results.md under "Clipping (held)".
    /// </para>
    /// </summary>
    public static class TileClipper
    {
        /// <summary>
        /// Clip one polygon ring to the axis-aligned box [minX, maxX] x [minY, maxY].
        /// </summary>
        /// <param name="ring">
        /// Ring points as [x, y] pairs. May be closed (first point repeated last, as GeoJSON
        /// RFC 7946 requires) or open; both are accepted.
        /// </param>
        /// <returns>
        /// An EMPTY list when the ring falls entirely outside the box, or when what survives
        /// the clip has fewer than three distinct points (a corner touch, an edge-grazing
        /// sliver) and so encloses no area.
        /// Otherwise a single CLOSED ring: the first point is repeated as the last, because
        /// <c>GeoJsonToVectorTile.EncodePolygons</c> requires at least four points and treats
        /// the last one as a closing duplicate that it does not emit.
        /// <para>
        /// Vertex ORDER is preserved: Sutherland-Hodgman walks the ring's edges in sequence
        /// and emits surviving vertices and edge intersections in that same sequence, so a
        /// clipped ring keeps the winding of the ring it came from. That matters downstream:
        /// <c>GeometryDecoder.ClassifyPolygonRings</c> decides exterior-vs-hole from the sign
        /// of the shoelace area, so a clip that reversed a ring would turn exteriors into
        /// holes and silently drop geometry (the failure fixed in dd84f0e).
        /// </para>
        /// </returns>
        public static List<double[]> ClipRing(IReadOnlyList<double[]> ring,
            double minX, double minY, double maxX, double maxY)
        {
            var empty = new List<double[]>();
            if (ring == null || ring.Count < 3) return empty;

            double tol = Tolerance(minX, minY, maxX, maxY);

            // Work on the open form. A closed input would otherwise put a duplicate vertex
            // through the clip and come back out as a duplicate that the caller has to strip
            // anyway; dropping it here keeps one representation through the whole routine.
            // EXACT comparison, not the tolerance: a closed ring repeats its first point
            // bit-for-bit, whereas two merely nearby endpoints of a ring smaller than the
            // tolerance are a real edge, and swallowing it would cost that ring a vertex it
            // cannot spare.
            int n = ring.Count;
            if (n >= 2 && Near(ring[0], ring[n - 1], 0.0)) n--;
            if (n < 3) return empty;

            var current = new List<double[]>(n);
            for (int i = 0; i < n; i++) current.Add(new[] { ring[i][0], ring[i][1] });

            current = ClipHalfPlane(current, Edge.MinX, minX);
            if (current.Count == 0) return empty;
            current = ClipHalfPlane(current, Edge.MaxX, maxX);
            if (current.Count == 0) return empty;
            current = ClipHalfPlane(current, Edge.MinY, minY);
            if (current.Count == 0) return empty;
            current = ClipHalfPlane(current, Edge.MaxY, maxY);
            if (current.Count == 0) return empty;

            var simplified = Simplify(current, tol);
            if (simplified.Count < 3)
            {
                // The whole ring is smaller than the precision it is being measured against,
                // so tolerance-based merging erased it. That is the wrong answer: the ring is
                // REAL, just sub-pixel at this zoom, and a tile is more than what will be
                // drawn -- QuerySourceFeatures, selection and every style filter read the
                // features in it. Dropping one here makes a building that exists in the source
                // unqueryable at low zoom, which is a data loss, not a simplification.
                //
                // (Caught by StyleNullHeightTests, which transcodes 0.001-degree buildings into
                // tile 0/0/0: at that zoom the tolerance is ~45x the whole building.)
                //
                // So fall back to EXACT merging -- bit-identical repeats and exactly collinear
                // points, which are redundant at any scale. A ring that still cannot muster
                // three distinct points after that genuinely encloses nothing (a corner touch,
                // an edge-grazing sliver) and is dropped as before.
                simplified = Simplify(current, 0.0);
            }
            if (simplified.Count < 3) return empty;

            simplified.Add(new[] { simplified[0][0], simplified[0][1] }); // close it
            return simplified;
        }

        /// <summary>
        /// Merge tolerance, as a distance in the caller's own coordinate units.
        /// <para>
        /// It is derived from the box rather than fixed, because the box IS the scale that
        /// matters: GenerateTile hands us one tile plus its 64-unit buffer, and then encodes
        /// the result to integers at <c>DefaultExtent</c> = 4096 units per tile. A box side is
        /// therefore about 4096 + 2x64 = 4224 integer steps, and box/8192 is a hair over HALF
        /// one step. Anything closer together than that collapses to the same integer in
        /// EncodePolygons anyway -- so removing it here throws away nothing the encoder could
        /// have represented, while sparing the triangulator the zero-length edges and
        /// zero-area ears that those collapsed points become.
        /// </para>
        /// <para>
        /// At the twin's zoom that half-step is roughly 15 cm on the ground. A caller using
        /// this class with a box that is not one MVT tile gets a proportional tolerance, which
        /// is the sane reading of "negligible at this scale" either way.
        /// </para>
        /// </summary>
        private static double Tolerance(double minX, double minY, double maxX, double maxY)
        {
            double span = Math.Max(maxX - minX, maxY - minY);
            return span > 0 ? span / 8192.0 : 0.0;
        }

        /// <summary>
        /// Drop the vertices that carry no shape: near-duplicates, and points lying on the
        /// straight line between their two neighbours.
        /// <para>
        /// This was WRITTEN as the fix for the R7b pinhole artefact, and it is NOT one --
        /// see the KNOWN ARTEFACT and falsification notes on <see cref="TileClipper"/>. The
        /// hypothesis it encodes: Sutherland-Hodgman emits a repeat whenever a ring vertex
        /// sits on (or within a whisker of) a clip edge -- the intersection and the kept
        /// endpoint coincide -- and a ring crossing an edge at a shallow angle produces runs
        /// of points that are collinear to well below the precision the tile encoder can
        /// represent; EncodePolygons would then round those to identical integers, handing
        /// EarClipTriangulator zero-length edges and zero-area ears, and where it drops one a
        /// one-pixel hole would open in the fill.
        /// </para>
        /// <para>
        /// Measured 2026-09-11: this pass removes 3,093 vertices and the holes do not move --
        /// same count, same pixel coordinates, same luminances. The hypothesis is falsified.
        /// What this method still does is remove vertices the integer encoder would collapse
        /// anyway, which is worth keeping on its own terms; what it does not do is close the
        /// seam. Look downstream of the mesh builder instead.
        /// </para>
        /// <para>
        /// Both passes wrap around the ring, and the collinear pass repeats until nothing more
        /// is removed, because removing one vertex can make a neighbour redundant in turn.
        /// Neither pass reorders anything, so winding is preserved; neither moves a surviving
        /// vertex, so the area changes only by the sub-tolerance slivers it removes.
        /// </para>
        /// </summary>
        private static List<double[]> Simplify(List<double[]> pts, double tol)
        {
            var outp = new List<double[]>(pts.Count);
            foreach (var p in pts)
            {
                if (outp.Count == 0 || !Near(outp[outp.Count - 1], p, tol)) outp.Add(p);
            }
            while (outp.Count > 1 && Near(outp[0], outp[outp.Count - 1], tol))
                outp.RemoveAt(outp.Count - 1);

            if (outp.Count < 3) return outp;

            bool removedAny = true;
            while (removedAny && outp.Count >= 3)
            {
                removedAny = false;
                int i = 0;
                while (i < outp.Count && outp.Count >= 3)
                {
                    var a = outp[(i + outp.Count - 1) % outp.Count];
                    var b = outp[i];
                    var c = outp[(i + 1) % outp.Count];
                    if (IsRedundant(a, b, c, tol))
                    {
                        outp.RemoveAt(i);
                        removedAny = true;
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            return outp;
        }

        /// <summary>
        /// True when b adds nothing to the ring: either its two neighbours have collapsed onto
        /// each other (so a-b-c is a zero-area spike), or b's perpendicular distance from the
        /// line a-c is within tolerance.
        /// </summary>
        private static bool IsRedundant(double[] a, double[] b, double[] c, double tol)
        {
            double dx = c[0] - a[0];
            double dy = c[1] - a[1];
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len <= tol) return true;

            double cross = dx * (b[1] - a[1]) - dy * (b[0] - a[0]);
            return Math.Abs(cross) / len <= tol;
        }

        private enum Edge { MinX, MaxX, MinY, MaxY }

        private static bool Inside(double[] p, Edge edge, double bound)
        {
            switch (edge)
            {
                case Edge.MinX: return p[0] >= bound;
                case Edge.MaxX: return p[0] <= bound;
                case Edge.MinY: return p[1] >= bound;
                case Edge.MaxY: return p[1] <= bound;
                default: throw new ArgumentOutOfRangeException(nameof(edge));
            }
        }

        /// <summary>
        /// Point where segment a-b crosses the clip line. The box is axis-aligned, so one
        /// coordinate is the bound itself and the other is a linear interpolation -- writing
        /// the bound in exactly (rather than through the interpolation) keeps clipped vertices
        /// precisely on the shared edge, so adjacent tiles agree on it to the last bit.
        /// </summary>
        private static double[] Intersect(double[] a, double[] b, Edge edge, double bound)
        {
            switch (edge)
            {
                case Edge.MinX:
                case Edge.MaxX:
                {
                    double dx = b[0] - a[0];
                    double t = dx == 0 ? 0 : (bound - a[0]) / dx;
                    return new[] { bound, a[1] + (b[1] - a[1]) * t };
                }
                default:
                {
                    double dy = b[1] - a[1];
                    double t = dy == 0 ? 0 : (bound - a[1]) / dy;
                    return new[] { a[0] + (b[0] - a[0]) * t, bound };
                }
            }
        }

        private static List<double[]> ClipHalfPlane(List<double[]> input, Edge edge, double bound)
        {
            var output = new List<double[]>(input.Count + 4);
            int count = input.Count;
            for (int i = 0; i < count; i++)
            {
                var prev = input[(i + count - 1) % count];
                var cur = input[i];
                bool prevIn = Inside(prev, edge, bound);
                bool curIn = Inside(cur, edge, bound);

                if (curIn)
                {
                    if (!prevIn) output.Add(Intersect(prev, cur, edge, bound));
                    output.Add(cur);
                }
                else if (prevIn)
                {
                    output.Add(Intersect(prev, cur, edge, bound));
                }
            }
            return output;
        }

        private static bool Near(double[] a, double[] b, double tol)
        {
            double dx = a[0] - b[0];
            double dy = a[1] - b[1];
            return dx * dx + dy * dy <= tol * tol;
        }
    }
}
