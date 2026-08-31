using System.Reflection;
using System.Runtime.InteropServices;
using GameHours.Windows.Input;

namespace GameHours.Windows.Tests;

public sealed class WindowsUserInteractionStateProviderPrivacyTests
{
    [Fact]
    public void XInputInterop_ExposesOnlyPacketNumberToManagedCode()
    {
        var providerType = typeof(WindowsUserInteractionStateProvider);
        var stateType = providerType.GetNestedType("XInputState", BindingFlags.NonPublic);

        Assert.NotNull(stateType);
        var fields = stateType!.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var packetField = Assert.Single(fields);

        Assert.Equal("PacketNumber", packetField.Name);
        Assert.Equal(typeof(uint), packetField.FieldType);
        Assert.Equal(16, Marshal.SizeOf(stateType));
        Assert.Null(providerType.GetNestedType("XInputGamepad", BindingFlags.NonPublic));
    }
}
