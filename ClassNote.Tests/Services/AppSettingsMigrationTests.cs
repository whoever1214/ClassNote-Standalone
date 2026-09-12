using System.Text.Json;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 升级兼容：v0.4.x 的 settings.json（只有 ScheduleMicId/ScheduleMicName、没有录音来源字段）
/// 必须能被读取，并且用户已选的麦克风不能丢。
/// </summary>
public class AppSettingsMigrationTests
{
    private static AppSettingsData Deserialize(string json)
        => JsonSerializer.Deserialize<AppSettingsData>(json) ?? new AppSettingsData();

    [Fact]
    public void LegacySettings_MigratesScheduleMicToRecordingMic()
    {
        // v0.4.x 落盘形态：有 ScheduleMic*，没有 Recording* 与 RecordingSource
        const string legacy = """
        {
          "LlmApiKey": "dpapi:xxx",
          "LlmBaseUrl": "https://api.deepseek.com",
          "LlmModel": "deepseek-chat",
          "LlmVisionModel": "",
          "LlmFallbackApiKey": "",
          "LlmFallbackBaseUrl": "",
          "LlmTimeoutSeconds": 600,
          "ScheduleEnabled": true,
          "ScheduleLaunchAtStartup": false,
          "ScheduleMicId": "{0.0.1.00000000}.{abc}",
          "ScheduleMicName": "USB 麦克风"
        }
        """;

        var data = Deserialize(legacy);
        AppSettings.MigrateLegacyFields(data);

        // 已选的麦克风被搬到通用录音项，且不会因为字段改名而丢失
        Assert.Equal("{0.0.1.00000000}.{abc}", data.RecordingMicId);
        Assert.Equal("USB 麦克风", data.RecordingMicName);
        Assert.Equal("", data.ScheduleMicId);
        Assert.Equal("", data.ScheduleMicName);

        // 旧文件没有录音来源字段 → 回退到麦克风（等于旧版行为）
        Assert.Equal(AudioSourceKind.Microphone, AudioSourceKinds.FromStorage(data.RecordingSource));
        // 其余旧字段照常读入
        Assert.Equal("https://api.deepseek.com", data.LlmBaseUrl);
        Assert.Equal(600, data.LlmTimeoutSeconds);
        Assert.True(data.ScheduleEnabled);
    }

    [Fact]
    public void NewSettings_TakePrecedenceOverLegacyFields()
    {
        // 已经保存过新字段的用户：迁移不得覆盖新配置
        const string mixed = """
        {
          "RecordingMicId": "{0.0.1.00000000}.{new}",
          "RecordingMicName": "新麦克风",
          "ScheduleMicId": "{0.0.1.00000000}.{old}",
          "ScheduleMicName": "旧麦克风"
        }
        """;

        var data = Deserialize(mixed);
        AppSettings.MigrateLegacyFields(data);

        Assert.Equal("{0.0.1.00000000}.{new}", data.RecordingMicId);
        Assert.Equal("新麦克风", data.RecordingMicName);
        Assert.Equal("", data.ScheduleMicId); // 旧字段被清理，不再写回
    }

    [Fact]
    public void RemovedFallbackFields_AreIgnoredOnRead()
    {
        // v0.5.0 删除了备用 Key/地址：旧文件里残留这两个键不得导致反序列化失败
        const string legacy = """
        {
          "LlmApiKey": "dpapi:xxx",
          "LlmFallbackApiKey": "dpapi:yyy",
          "LlmFallbackBaseUrl": "https://backup.example.com"
        }
        """;

        var data = Deserialize(legacy);

        Assert.Equal("dpapi:xxx", data.LlmApiKey);
        // 主配置不受影响，且类型上已不存在备用字段
        Assert.Equal("https://api.deepseek.com", data.LlmBaseUrl);
        Assert.Equal("deepseek-chat", data.LlmModel);
        Assert.Null(typeof(AppSettingsData).GetProperty("LlmFallbackApiKey"));
        Assert.Null(typeof(AppSettingsData).GetProperty("LlmFallbackBaseUrl"));
    }

    [Fact]
    public void MissingRecordingFields_FallBackToDefaults()
    {
        const string minimal = """{ "LlmApiKey": "" }""";

        var data = Deserialize(minimal);

        Assert.Equal("", data.RecordingMicId);
        Assert.Equal("", data.RecordingOutputDeviceId);
        Assert.Equal(AudioSourceKind.Microphone, AudioSourceKinds.FromStorage(data.RecordingSource));
    }

    [Fact]
    public void ToRecordingConfig_MapsSettingsToCaptureConfig()
    {
        var data = new AppSettingsData
        {
            RecordingSource = nameof(AudioSourceKind.Both),
            RecordingMicId = "mic-1",
            RecordingOutputDeviceId = "spk-1",
        };

        var config = AppSettings.ToRecordingConfig(data);

        Assert.Equal(AudioSourceKind.Both, config.Source);
        Assert.Equal("mic-1", config.MicId);
        Assert.Equal("spk-1", config.OutputDeviceId);
        Assert.Equal(2, config.ChannelCount);
    }

    [Fact]
    public void ToRecordingConfig_EmptyIdsBecomeNullForDefaultDevices()
    {
        // 空串必须转成 null（= 用系统默认设备），否则采集层会拿空字符串去查设备
        var config = AppSettings.ToRecordingConfig(new AppSettingsData());

        Assert.Null(config.MicId);
        Assert.Null(config.OutputDeviceId);
    }

    [Fact]
    public void ToRecordingConfig_CorruptSourceFallsBackToMicrophone()
    {
        var config = AppSettings.ToRecordingConfig(new AppSettingsData { RecordingSource = "garbage" });

        Assert.Equal(AudioSourceKind.Microphone, config.Source);
        Assert.True(config.NeedsMicrophone);
        Assert.False(config.NeedsSystemAudio);
    }
}
