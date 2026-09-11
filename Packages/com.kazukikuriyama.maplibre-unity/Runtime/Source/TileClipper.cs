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
    /// KNOWN ARTEFACT, unfixed. That overlap does NOT make the render seam-free. A controlled
    /// Web capture of the HealthAtlas twin (same app build, only this package's pin differing)
    /// counts 248 one-pixel holes inside the isochrone band fill after clipping against 207
    /// before -- roughly 41 new ones, in short regularly-spaced near-vertical lines along tile
    /// boundaries, each a partial-coverage pixel with the backdrop showing through. It cannot
    /// be a gap BETWEEN tiles: they overlap by 2x the buffer (~6 px at that zoom) and
    /// VectorTileMeshBuilder never clamps to the extent. The leading hypothesis is a crack
    /// inside a single tile's own mesh -- Sutherland-Hodgman inserts collinear vertices along
    /// the box edge, and (for a concave ring whose overlap with the box is disjoint)
    /// zero-width bridge edges, either of which can make EarClipTriangulator drop an ear.
    /// Stripping collinear points from the clipped ring before returning is the first thing to
    /// try. Not attempted here: a fix needs a build-and-capture cycle to validate, and an
    /// unvalidated fix is worse than a recorded finding. Resolve before offering upstream.
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

            // Work on the open form. A closed input would otherwise put a duplicate vertex
            // through the clip and come back out as a duplicate that the caller has to strip
            // anyway; dropping it here keeps one representation through the whole routine.
            int n = ring.Count;
            if (n >= 2 && Same(ring[0], ring[n - 1])) n--;
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

            // Sutherland-Hodgman emits a repeat whenever a vertex sits exactly on a clip edge
            // (the intersection and the kept endpoint coincide), and collapses a ring that
            // only touches the box into a run of one point. Drop consecutive repeats, then the
            // wrap-around repeat, and decide degeneracy on what is left.
            var distinct = new List<double[]>(current.Count);
            foreach (var p in current)
            {
                if (distinct.Count == 0 || !Same(distinct[distinct.Count - 1], p))
                    distinct.Add(p);
            }
            while (distinct.Count > 1 && Same(distinct[0], distinct[distinct.Count - 1]))
                distinct.RemoveAt(distinct.Count - 1);

            if (distinct.Count < 3) return empty;

            distinct.Add(new[] { distinct[0][0], distinct[0][1] }); // close it
            return distinct;
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

        private static bool Same(double[] a, double[] b) => a[0] == b[0] && a[1] == b[1];
    }
}
