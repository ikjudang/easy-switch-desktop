using EasySwitch.Hidpp;

namespace EasySwitch.Hidpp.Tests;

public sealed class ReceiverNotificationTests
{
    [Fact]
    public void EnablingWirelessNotificationsPreservesOtherApplicationsFlags()
    {
        var request = ReceiverMessages.BuildEnableWirelessNotifications(Hex.Parse("10 FF 81 00 92 A8 74"));
        Assert.Equal(Hex.Parse("10 FF 80 00 92 A9 74"), request);
    }

    [Fact]
    public void ErrorResponseCannotBecomeAReceiverConfigurationWrite()
    {
        Assert.Throws<InvalidDataException>(() => ReceiverMessages.BuildEnableWirelessNotifications(Hex.Parse("10 FF 8F 81 00 03 00")));
    }

    [Theory]
    [InlineData("10 03 41 10 41 66 B3", false, 3, 0xB366)]
    [InlineData("10 03 41 10 01 66 B3", true, 3, 0xB366)]
    [InlineData("10 02 41 04 52 90 40", false, 2, 0x4090)]
    [InlineData("10 02 41 04 92 90 40", true, 2, 0x4090)]
    public void CapturedBoltAndUnifyingNotificationsRetainDeviceSlotAndLinkState(string hex, bool connected, int slot, int product)
    {
        var notice = Assert.IsType<DeviceConnectionNotice>(ReceiverMessages.ParseDeviceConnection(Hex.Parse(hex)));
        Assert.Equal(connected, notice.LinkEstablishedAssumed);
        Assert.Equal(slot, notice.DeviceIndex);
        Assert.Equal(product, notice.WirelessProductId);
    }

    [Theory]
    [InlineData("11 03 09 00 02 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 1)]
    [InlineData("11 03 09 00 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 0)]
    public void CapturedBoltKeyboardEventAcceptsNonzeroSourceHost(string hex, int target)
    {
        var frame = Assert.IsType<HidppFrame>(HidppFrameParser.Parse(Hex.Parse(hex)));
        Assert.Equal(target, ChangeHostFeature.ParseHostChangeEvent(frame, 9));
    }
}
