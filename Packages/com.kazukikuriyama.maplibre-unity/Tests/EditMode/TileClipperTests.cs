using System;
using System.Collections.Generic;
using MapLibre.Unity.Source;
using NUnit.Framework;

namespace MapLibre.Unity.Tests.EditMode
{
    /// <summary>
    /// Tests for the Sutherland-Hodgman ring clipper that GeoJsonToVectorTile.GenerateTile
    /// uses to cut polygons down to the buffered tile box (R7b).
    ///
    /// Strategy, deliberately: assert on signed AREA and point count, never on vertex order.
    /// A correct clip is free to start the output ring at any vertex of the result, and
    /// pinning a rotation would couple these tests to the half-plane order inside the
    /// algorithm. Area and count pin down the SHAPE; the sign of the area separately pins
    /// down the winding, which is the property GeometryDecoder.ClassifyPolygonRings reads to
    /// tell an exterior ring from a hole (see fork commit dd84f0e -- a flipped sign there
    /// silently discards real geometry).
    /// </summary>
    public class TileClipperTests
    {
        // The clip box for every test below.
        const double MinX = 0, MinY = 0, MaxX = 4, MaxY = 4;

        /// <summary>Shoelace area. Positive = counter-clockwise in a y-up reading.</summary>
        static double SignedArea(IReadOnlyList<double[]> ring)
        {
            // Tolerate either form: a closed ring repeats its first point, and the wrap
            // below would otherwise count a zero-length edge.
            int n = ring.Count;
            if (n >= 2 && ring[0][0] == ring[n - 1][0] && ring[0][1] == ring[n - 1][1]) n--;

            double area = 0;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                area += a[0] * b[1] - b[0] * a[1];
            }
            return area * 0.5;
        }

        /// <summary>An axis-aligned rectangle, closed, wound counter-clockwise (y-up).</summary>
        static List<double[]> Rect(double x0, double y0, double x1, double y1)
        {
            return new List<double[]>
            {
                new[] { x0, y0 },
                new[] { x1, y0 },
                new[] { x1, y1 },
                new[] { x0, y1 },
                new[] { x0, y0 }   // closed, as GeoJSON RFC 7946 requires
            };
        }

        static List<double[]> Clip(List<double[]> ring) =>
            TileClipper.ClipRing(ring, MinX, MinY, MaxX, MaxY);

        [Test]
        public void Ring_FullyInside_IsUnchanged()
        {
            var ring = Rect(1, 1, 2, 2);
            var clipped = Clip(ring);

            Assert.AreEqual(5, clipped.Count,
                "a ring wholly inside the box must survive intact, still closed (4 distinct + the repeat)");
            Assert.AreEqual(SignedArea(ring), SignedArea(clipped), 1e-12,
                "clipping a ring that never meets an edge must not change its area");
        }

        [Test]
        public void Ring_FullyOutside_IsDropped()
        {
            var clipped = Clip(Rect(5, 5, 6, 6));

            Assert.AreEqual(0, clipped.Count,
                "a ring wholly outside the box contributes nothing to this tile and must clip to empty");
        }

        [Test]
        public void Ring_Straddling_IsCutAtTheEdge()
        {
            // (-1,-1)-(2,2) meets the box in (0,0)-(2,2): area 4, not the source ring's 9.
            var clipped = Clip(Rect(-1, -1, 2, 2));

            Assert.AreEqual(5, clipped.Count,
                "the clipped overlap is a rectangle: 4 distinct corners plus the closing repeat");
            Assert.AreEqual(4.0, SignedArea(clipped), 1e-12,
                "only the part inside the box may be kept -- an area of 9 means the whole ring "
                + "was encoded anyway, which is the R7b duplication this clipper exists to stop");
        }

        [Test]
        public void Ring_Enclosing_TheWholeTile_BecomesTheTile()
        {
            var clipped = Clip(Rect(-10, -10, 10, 10));

            Assert.AreEqual(5, clipped.Count,
                "a ring swallowing the whole box clips down to the box itself");
            Assert.AreEqual(16.0, SignedArea(clipped), 1e-12,
                "the box is 4x4; anything larger means geometry outside the tile survived");

            foreach (var p in clipped)
            {
                Assert.That(p[0], Is.InRange(MinX, MaxX), "clipped x must lie within the box");
                Assert.That(p[1], Is.InRange(MinY, MaxY), "clipped y must lie within the box");
            }
        }

        [Test]
        public void Ring_TouchingOnlyAtACorner_IsDegenerateAndDropped()
        {
            // (-1,-1)-(0,0) shares exactly one point with the box. Sutherland-Hodgman
            // collapses it to a run of that single corner: no area, nothing to draw, and a
            // ring of fewer than 3 distinct points would make EncodePolygons emit a
            // malformed (or zero-area) polygon.
            var clipped = Clip(Rect(-1, -1, 0, 0));

            Assert.AreEqual(0, clipped.Count,
                "a corner touch encloses no area and must clip to empty, not to a degenerate ring");
        }

        [Test]
        public void Ring_ClippedRing_KeepsItsWinding()
        {
            var ccw = Rect(-1, -1, 2, 2);
            Assert.Greater(SignedArea(ccw), 0, "precondition: the source ring is counter-clockwise");

            var clipped = Clip(ccw);
            Assert.Greater(SignedArea(clipped), 0,
                "clipping must not reverse a ring. ClassifyPolygonRings decides exterior-vs-hole "
                + "from the sign of the area, so a flipped ring is silently dropped geometry (dd84f0e).");

            // ...and the same ring wound the other way must stay the other way.
            var cw = new List<double[]>(ccw);
            cw.Reverse();
            Assert.Less(SignedArea(cw), 0, "precondition: the reversed ring is clockwise");

            var clippedCw = Clip(cw);
            Assert.Less(SignedArea(clippedCw), 0, "clipping must not reverse a clockwise ring either");
            Assert.AreEqual(-SignedArea(clipped), SignedArea(clippedCw), 1e-12,
                "the two windings must clip to the same shape, differing only in sign");
        }

        [Test]
        public void Ring_OpenInput_IsAccepted_AndComesBackClosed()
        {
            // GeoJsonToVectorTile.EncodePolygons requires >= 4 points and treats the last as a
            // closing duplicate it does not emit, so the clipper must always close its output
            // -- whichever form it was handed.
            var open = Rect(1, 1, 2, 2);
            open.RemoveAt(open.Count - 1);

            var clipped = Clip(open);

            Assert.AreEqual(5, clipped.Count, "an open input must come back closed");
            Assert.AreEqual(clipped[0][0], clipped[clipped.Count - 1][0], 1e-12);
            Assert.AreEqual(clipped[0][1], clipped[clipped.Count - 1][1], 1e-12);
            Assert.AreEqual(1.0, SignedArea(clipped), 1e-12);
        }

        // === The collinear/duplicate post-pass (fix round 1 of the R7b pinhole artefact) ===
        //
        // A clipped ring used to keep every point Sutherland-Hodgman emitted, including
        // repeats where a vertex sat on a clip edge and runs of points collinear to far below
        // the precision EncodePolygons can represent. Rounded to integers those become
        // zero-length edges and zero-area ears; where EarClipTriangulator drops one, a
        // one-pixel hole opens in the fill. Measured on the HealthAtlas twin's Web build as
        // short dotted lines running along tile boundaries.

        /// <summary>Perpendicular distance of b from the line a-c, the collinearity measure.</summary>
        static double DistanceFromLine(double[] a, double[] b, double[] c)
        {
            double dx = c[0] - a[0], dy = c[1] - a[1];
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len == 0) return 0;
            return Math.Abs(dx * (b[1] - a[1]) - dy * (b[0] - a[0])) / len;
        }

        static double SmallestGap(IReadOnlyList<double[]> ring)
        {
            int n = ring.Count;
            if (n >= 2 && ring[0][0] == ring[n - 1][0] && ring[0][1] == ring[n - 1][1]) n--;
            double min = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % n];
                min = Math.Min(min, Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])));
            }
            return min;
        }

        static double SmallestLineDistance(IReadOnlyList<double[]> ring)
        {
            int n = ring.Count;
            if (n >= 2 && ring[0][0] == ring[n - 1][0] && ring[0][1] == ring[n - 1][1]) n--;
            double min = double.MaxValue;
            for (int i = 0; i < n; i++)
                min = Math.Min(min, DistanceFromLine(ring[(i + n - 1) % n], ring[i], ring[(i + 1) % n]));
            return min;
        }

        /// <summary>
        /// The tolerance ClipRing derives from this box: one side / 8192, i.e. a hair over half
        /// an integer step at MVT extent 4096 plus a 64-unit buffer.
        /// </summary>
        const double Tol = (MaxX - MinX) / 8192.0;

        /// <summary>
        /// A rectangle straddling the box, carrying redundant vertices halfway along the two
        /// edges that survive the clip. Those points are exactly collinear and must not reach
        /// the encoder.
        /// </summary>
        static List<double[]> StraddlingRectWithCollinearVertices()
        {
            return new List<double[]>
            {
                new[] { -1.0, -1.0 },
                new[] {  2.0, -1.0 },
                new[] {  2.0,  0.5 },   // redundant: mid-point of the right edge
                new[] {  2.0,  2.0 },
                new[] {  0.5,  2.0 },   // redundant: mid-point of the top edge
                new[] { -1.0,  2.0 },
                new[] { -1.0, -1.0 }
            };
        }

        [Test]
        public void ClippedRing_HasNoThreeConsecutiveCollinearPoints()
        {
            var clipped = Clip(StraddlingRectWithCollinearVertices());

            Assert.AreEqual(5, clipped.Count,
                "the clipped overlap is the square (0,0)-(2,2): 4 corners plus the closing "
                + "repeat. 7 means the two mid-edge points survived, and those are exactly the "
                + "vertices that round to a zero-length edge and cost a triangle.");
            Assert.Greater(SmallestLineDistance(clipped), Tol,
                "no vertex may lie within tolerance of the line through its two neighbours");
        }

        [Test]
        public void ClippedRing_NearDuplicatePoints_AreMerged()
        {
            // The vertex just INSIDE the clip edge is 1e-5 from the intersection point that the
            // previous, outside vertex generates -- far below Tol (~4.9e-4 for this box), but
            // not bit-identical, so exact-equality de-duplication leaves both.
            var ring = new List<double[]>
            {
                new[] { -1.0,   1.0 },
                new[] {  1e-5,  1.0 },
                new[] {  3.0,   1.0 },
                new[] {  3.0,   3.0 },
                new[] { -1.0,   3.0 }
            };

            var clipped = Clip(ring);

            Assert.AreEqual(5, clipped.Count,
                "(0,1) and (1e-5,1) are the same point at this scale and must merge; 6 means "
                + "only bit-identical repeats were removed");
            Assert.Greater(SmallestGap(clipped), Tol,
                "no two consecutive vertices may sit within tolerance of each other");
            Assert.AreEqual(6.0, SignedArea(clipped), 1e-4,
                "the surviving shape is still the 3x2 rectangle the clip produced");
        }

        [Test]
        public void ClippedRing_SimplificationChangesNeitherAreaNorWinding()
        {
            var ccw = StraddlingRectWithCollinearVertices();
            Assert.Greater(SignedArea(ccw), 0, "precondition: the source ring is counter-clockwise");

            var clipped = Clip(ccw);
            Assert.AreEqual(4.0, SignedArea(clipped), 1e-12,
                "removing a point that lies exactly on the line between its neighbours removes "
                + "no area at all -- the clipped square (0,0)-(2,2) is still 4");
            Assert.Greater(SignedArea(clipped), 0, "and the ring is still counter-clockwise");

            var cw = new List<double[]>(ccw);
            cw.Reverse();
            var clippedCw = Clip(cw);
            Assert.Less(SignedArea(clippedCw), 0, "a clockwise ring stays clockwise through the pass");
            Assert.AreEqual(-SignedArea(clipped), SignedArea(clippedCw), 1e-12,
                "both windings simplify to the same shape, differing only in sign");
            Assert.AreEqual(clipped.Count, clippedCw.Count,
                "and to the same number of points");
        }

        [Test]
        public void ClippedRing_SmallerThanTheTolerance_SurvivesInsteadOfVanishing()
        {
            // A ring three orders of magnitude below the merge tolerance. Every point is
            // within tolerance of every other, so tolerance-based merging alone would collapse
            // it to one point and drop it -- but the ring is REAL, merely sub-pixel at this
            // zoom, and a tile is more than what gets drawn: QuerySourceFeatures, selection and
            // every style filter read the features in it.
            //
            // Found the hard way. StyleNullHeightTests transcodes 0.001-degree buildings into
            // tile 0/0/0, where the tolerance is about 45x the whole building; the first
            // version of the simplification pass emitted no 'buildings' layer at all and took
            // four PlayMode tests with it.
            var tiny = Rect(1.0, 1.0, 1.0 + 1e-6, 1.0 + 1e-6);
            Assert.Less(1e-6, Tol, "precondition: this ring really is smaller than the tolerance");

            var clipped = Clip(tiny);

            Assert.AreEqual(5, clipped.Count,
                "a sub-tolerance ring keeps its four corners -- dropping it loses a feature "
                + "that exists in the source");
            Assert.AreEqual(1e-12, SignedArea(clipped), 1e-18, "and keeps its (tiny) area");
        }

        [Test]
        public void Ring_WithTooFewPoints_IsDropped()
        {
            Assert.AreEqual(0, Clip(new List<double[]>()).Count);
            Assert.AreEqual(0, Clip(new List<double[]> { new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } }).Count,
                "a two-point 'ring' encloses no area");
        }
    }
}
