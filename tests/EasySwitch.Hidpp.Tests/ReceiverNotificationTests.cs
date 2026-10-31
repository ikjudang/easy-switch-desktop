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

    // A long report whose byte[2] is 0x40/0x41 is a HID++ 2.0 event or reply of feature index 0x40/0x41,
    // not a receiver notice; reading it as one could invalidate (deselect) the device.
    [Theory]
    [InlineData("11 03 41 00 41 42 B0 00 00 00 00 00 00 00 00 00 00 00 00 00")]
    [InlineData("10 FF 41 10 41 66 B3")]
    [InlineData("10 00 41 10 41 66 B3")]
    [InlineData("10 07 41 10 41 66 B3")]
    public void OnlyShortSlotReportsAreConnectionNotices(string hex)
        => Assert.Null(ReceiverMessages.ParseDeviceConnection(Hex.Parse(hex)));

    [Theory]
    [InlineData("10 02 40 02 00 00 00", true)]
    [InlineData("10 06 40 02 00 00 00", true)]
    [InlineData("11 02 40 00 01 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00", false)]
    [InlineData("10 FF 40 02 00 00 00", false)]
    [InlineData("10 02 41 10 41 66 B3", false)]
    public void UnpairingNoticeRequiresAShortSlotReport(string hex, bool expected)
        => Assert.Equal(expected, ReceiverMessages.IsDeviceDisconnection(Hex.Parse(hex)));

    [Theory]
    [InlineData("11 03 09 00 02 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 1)]
    [InlineData("11 03 09 00 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 0)]
    public void CapturedBoltKeyboardEventAcceptsNonzeroSourceHost(string hex, int target)
    {
        var frame = Assert.IsType<HidppFrame>(HidppFrameParser.Parse(Hex.Parse(hex)));
        Assert.Equal(target, ChangeHostFeature.ParseHostChangeEvent(frame, 9));
    }
}
