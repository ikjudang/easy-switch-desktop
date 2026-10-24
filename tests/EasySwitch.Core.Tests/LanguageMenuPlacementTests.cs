using EasySwitch.Core;

namespace EasySwitch.Core.Tests;

public sealed class LanguageMenuPlacementTests
{
    [Theory]
    [InlineData(120,100,200,132,120,134)]
    [InlineData(220,590,300,622,220,508)]
    [InlineData(120,518,200,550,120,552)]
    [InlineData(120,519,200,551,120,437)]
    [InlineData(300,100,350,132,238,134)]
    public void MatchesAndroidDropdownPlacement(double l,double t,double r,double b,double x,double y)
    {
        Assert.Equal(new(x,y),LanguageMenuPlacement.Place(new(l,t,r,b),new(0,0,360,640),112,80));
    }
    [Fact]
    public void NegativeMonitorOriginPreservesGapAndClampsToItsWorkArea()
    {
        Assert.Equal(new(-122,134),LanguageMenuPlacement.Place(new(-60,100,-10,132),new(-360,0,0,640),112,80));
        Assert.Equal(new(120,8),LanguageMenuPlacement.Place(new(120,40,200,72),new(0,0,360,120),112,80));
    }
}
