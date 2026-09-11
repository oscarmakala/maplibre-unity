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

        [Test]
        public void Ring_WithTooFewPoints_IsDropped()
        {
            Assert.AreEqual(0, Clip(new List<double[]>()).Count);
            Assert.AreEqual(0, Clip(new List<double[]> { new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } }).Count,
                "a two-point 'ring' encloses no area");
        }
    }
}
