using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

public class AudioSourceKindsTests
{
    [Theory]
    [InlineData(AudioSourceKind.Microphone)]
    [InlineData(AudioSourceKind.System)]
    [InlineData(AudioSourceKind.Both)]
    public void StorageRoundTrip_PreservesKind(AudioSourceKind kind)
    {
        var stored = AudioSourceKinds.ToStorage(kind);
        Assert.Equal(kind, AudioSourceKinds.FromStorage(stored));
    }

    [Theory]
    [InlineData(AudioSourceKind.Microphone)]
    [InlineData(AudioSourceKind.System)]
    [InlineData(AudioSourceKind.Both)]
    public void DisplayRoundTrip_PreservesKind(AudioSourceKind kind)
    {
        var display = AudioSourceKinds.ToDisplayName(kind);
        Assert.False(string.IsNullOrWhiteSpace(display));
        Assert.Equal(kind, AudioSourceKinds.FromDisplayName(display));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("系统声音")]      // 界面文案，不是存储值
    [InlineData("3")]
    [InlineData("unknown")]
    public void FromStorage_UnknownValue_FallsBackToMicrophone(string? value)
    {
        // 旧版 settings.json 没有该字段，必须等价于旧行为（仅麦克风）
        Assert.Equal(AudioSourceKind.Microphone, AudioSourceKinds.FromStorage(value));
    }

    [Fact]
    public void FromStorage_IsCaseInsensitive()
    {
        Assert.Equal(AudioSourceKind.System, AudioSourceKinds.FromStorage("system"));
        Assert.Equal(AudioSourceKind.Both, AudioSourceKinds.FromStorage("BOTH"));
    }

    [Fact]
    public void DisplayNames_MatchEnumOrder()
    {
        // 设置窗口用 SelectedIndex 直接映射枚举，顺序必须与枚举一致
        Assert.Equal(3, AudioSourceKinds.DisplayNames.Length);
        Assert.Equal(AudioSourceKind.Microphone, AudioSourceKinds.FromDisplayName(AudioSourceKinds.DisplayNames[0]));
        Assert.Equal(AudioSourceKind.System, AudioSourceKinds.FromDisplayName(AudioSourceKinds.DisplayNames[1]));
        Assert.Equal(AudioSourceKind.Both, AudioSourceKinds.FromDisplayName(AudioSourceKinds.DisplayNames[2]));
    }

    [Fact]
    public void RecordingConfig_ReportsWhichSourcesAreNeeded()
    {
        var mic = new RecordingConfig(AudioSourceKind.Microphone, "mic-1");
        Assert.True(mic.NeedsMicrophone);
        Assert.False(mic.NeedsSystemAudio);
        Assert.Equal(1, mic.ChannelCount);

        var system = new RecordingConfig(AudioSourceKind.System, null, "spk-1");
        Assert.False(system.NeedsMicrophone);
        Assert.True(system.NeedsSystemAudio);
        Assert.Equal(1, system.ChannelCount);

        var both = new RecordingConfig(AudioSourceKind.Both, "mic-1", "spk-1");
        Assert.True(both.NeedsMicrophone);
        Assert.True(both.NeedsSystemAudio);
        Assert.Equal(2, both.ChannelCount);
    }

    [Fact]
    public void Default_IsMicrophoneOnSystemDevices()
    {
        Assert.Equal(AudioSourceKind.Microphone, RecordingConfig.Default.Source);
        Assert.Null(RecordingConfig.Default.MicId);
        Assert.Null(RecordingConfig.Default.OutputDeviceId);
    }
}
