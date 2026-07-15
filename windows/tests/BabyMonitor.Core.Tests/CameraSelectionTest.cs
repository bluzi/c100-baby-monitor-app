using BabyMonitor.Core.Ui;
using Xunit;

namespace BabyMonitor.Core.Tests;

public class CameraSelectionTest
{
    [Fact(DisplayName = "CAM-6 exactly one camera is auto-selected — zero or many still show the picker")]
    public void AutoSelectsSingleCamera()
    {
        Assert.True(CameraSelection.AutoSelectsSingle(1));
        Assert.False(CameraSelection.AutoSelectsSingle(0)); // no cameras: CAM-5 says so, never auto
        Assert.False(CameraSelection.AutoSelectsSingle(2)); // a real choice: the picker
        Assert.False(CameraSelection.AutoSelectsSingle(5));
    }
}
