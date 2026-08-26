using System;
using VideoDirector.Models;
using Xunit;

namespace VideoDirector.Tests
{
    public class ClipRulesTests
    {
        [Fact]
        public void TenSecondClipPlusOneSecondFade_IsElevenSecondsNotTwelve()
        {
            // OpDuration already includes the additive fade (10s picture + 1s fade = 11s).
            var total = ClipRules.LatestStoryEnd(new[]
            {
                (TimeSpan.Zero, TimeSpan.FromSeconds(11))
            });
            Assert.Equal(TimeSpan.FromSeconds(11), total);
            Assert.NotEqual(TimeSpan.FromSeconds(12), total);
        }

        [Fact]
        public void FadeLiesInsideTheClipWindow()
        {
            var start = TimeSpan.FromSeconds(2);
            var op = TimeSpan.FromSeconds(11);
            var fade = TimeSpan.FromSeconds(1);
            var end = ClipRules.StoryEnd(start, op);
            Assert.True(start + op - fade >= start);
            Assert.True(start + op - fade < end);
            Assert.Equal(TimeSpan.FromSeconds(13), end);
        }

        [Fact]
        public void MidAloneCountsAsAModification()
        {
            Assert.False(ClipRules.HasMarkModifications(startIsIdentity: true, endIsIdentity: true, hasMid: false));
            Assert.True(ClipRules.HasMarkModifications(startIsIdentity: true, endIsIdentity: true, hasMid: true));
        }

        [Fact]
        public void ExportAudioSkipsNonOneXSpeed()
        {
            Assert.True(ClipRules.CanMixExportAudio(1.0));
            Assert.False(ClipRules.CanMixExportAudio(2.0));
            Assert.False(ClipRules.CanMixExportAudio(0.5));
        }
    }
}
