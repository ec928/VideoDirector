using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    // The anchor rule: a clip may travel until its edge meets the canvas edge, and no further.
    //
    // Zero overlap is deliberately legal - a clip resting its edge on the boundary line is still
    // anchored, because the outline tells you where it is. What is forbidden is standing clear of
    // the canvas altogether, which leaves nothing on screen pointing at it.
    public class ContactBoundaryTests
    {
        private const double Canvas = 1920, Fit = 1920;   // source aspect matches the canvas

        [Theory]
        [InlineData(0.5)]
        [InlineData(0.1)]
        [InlineData(0.9)]
        public void A_centre_inside_the_canvas_is_untouched(double cx)
        {
            Assert.Equal(cx, ClipGeometry.ContactCentre(cx, Fit, 0.3, Canvas), 10);
        }

        // A 30% clip may hang half its own width past either edge: -0.15 .. 1.15.
        [Theory]
        [InlineData(-0.15)]
        [InlineData(1.15)]
        public void The_edge_may_rest_exactly_on_the_boundary_line(double cx)
        {
            Assert.Equal(cx, ClipGeometry.ContactCentre(cx, Fit, 0.3, Canvas), 10);
        }

        [Theory]
        [InlineData(-5.0, -0.15)]
        [InlineData(-0.16, -0.15)]
        [InlineData(1.16, 1.15)]
        [InlineData(99.0, 1.15)]
        public void Past_it_the_clip_stops_dead(double cx, double expected)
        {
            Assert.Equal(expected, ClipGeometry.ContactCentre(cx, Fit, 0.3, Canvas), 10);
        }

        // A full-canvas clip can travel a whole canvas width, because contact is the test rather
        // than containment. The old preset rule pinned this case to dead centre.
        [Fact]
        public void A_full_size_clip_may_still_travel_to_contact()
        {
            Assert.Equal(-0.5, ClipGeometry.ContactCentre(-2.0, Fit, 1.0, Canvas), 10);
            Assert.Equal(1.5, ClipGeometry.ContactCentre(9.0, Fit, 1.0, Canvas), 10);
        }

        // THE UNITS TRAP. The centre is a fraction of the CANVAS; PlacementWidth is a fraction of
        // the clip's own FIT. On a source whose fit is half the canvas, a placement of 1.0 is a
        // clip half the canvas wide, so the limit is 0.25 out - not 0.5.
        [Fact]
        public void Placement_is_a_fraction_of_the_fit_not_the_canvas()
        {
            Assert.Equal(-0.25, ClipGeometry.ContactCentre(-3.0, fitSize: 960, placeFraction: 1.0, canvasSize: 1920), 10);
        }

        // Geometry that is not known yet must not be invented: leave the value alone rather than
        // clamp against a canvas of zero and slam every clip to the middle.
        [Theory]
        [InlineData(0, 1920)]
        [InlineData(1920, 0)]
        public void Unknown_geometry_leaves_the_centre_alone(double fit, double canvas)
        {
            Assert.Equal(7.5, ClipGeometry.ContactCentre(7.5, fit, 0.3, canvas), 10);
        }
    }
}
