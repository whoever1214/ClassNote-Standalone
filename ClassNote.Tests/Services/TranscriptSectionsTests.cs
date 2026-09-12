using System;
using System.Collections.Generic;
using System.Linq;
using ClassNote.Services;
using Xunit;

namespace ClassNote.Tests.Services;

/// <summary>
/// 分轨录音的来源标注契约（v0.6.0）。
///
/// 这套标注是"让 LLM 分得清谁在说话"的唯一依据：生成端（NoteProcessor）拼出来的标记，
/// 必须和提示词端（NotePromptBuilder）识别/叮嘱的标记完全一致——
/// 拼错一个字就会静默退化成"不给模型任何来源信息"，而且不会有任何报错。
/// </summary>
public class TranscriptSectionsTests
{
    [Fact]
    public void Render_LabelsEachTrackWithItsOwnSource()
    {
        var tracks = new List<TranscriptTrack>
        {
            new(RecordingAudioSource.Microphone, "老师现场讲的内容"),
            new(RecordingAudioSource.System, "课件里播的内容"),
        };

        string text = TranscriptSections.Render(tracks);

        // 每一路的正文都必须紧跟在自己的来源标注之后
        int micLabel = text.IndexOf(TranscriptSections.MicrophoneLabel, StringComparison.Ordinal);
        int micBody = text.IndexOf("老师现场讲的内容", StringComparison.Ordinal);
        int sysLabel = text.IndexOf(TranscriptSections.SystemLabel, StringComparison.Ordinal);
        int sysBody = text.IndexOf("课件里播的内容", StringComparison.Ordinal);

        Assert.True(micLabel >= 0 && micBody > micLabel, "麦克风正文必须在其标注之后");
        Assert.True(sysLabel >= 0 && sysBody > sysLabel, "系统声音正文必须在其标注之后");
    }

    [Fact]
    public void Render_SkipsEmptyTracks_InsteadOfEmittingEmptySections()
    {
        // 回环全程静音时系统声音没有转写：不能输出一个空的来源分区，
        // 那会让模型以为"课件那侧确实没内容"，反而干扰判断
        var tracks = new List<TranscriptTrack>
        {
            new(RecordingAudioSource.Microphone, "只有现场有声音"),
            new(RecordingAudioSource.System, "   "),
        };

        string text = TranscriptSections.Render(tracks);

        Assert.Contains("只有现场有声音", text);
        Assert.DoesNotContain(TranscriptSections.SystemLabel, text);
    }

    [Fact]
    public void Render_EmptyAndBlankTracks_ProducesEmptyText()
    {
        Assert.Equal("", TranscriptSections.Render(Array.Empty<TranscriptTrack>()));
        Assert.Equal("", TranscriptSections.Render(new[] { new TranscriptTrack(RecordingAudioSource.Microphone, "") }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("只有一路的普通转写，什么标注都没有")]
    [InlineData("【麦克风（教室现场）】\n只有这一路的标注")]
    public void IsMultiSource_FalseUnlessBothLabelsPresent(string? transcript)
    {
        Assert.False(TranscriptSections.IsMultiSource(transcript));
    }

    [Fact]
    public void IsMultiSource_TrueForRenderedTwoTrackTranscript()
    {
        string text = TranscriptSections.Render(new[]
        {
            new TranscriptTrack(RecordingAudioSource.Microphone, "现场"),
            new TranscriptTrack(RecordingAudioSource.System, "课件"),
        });

        Assert.True(TranscriptSections.IsMultiSource(text));
    }

    [Fact]
    public void IsMultiSource_OrderIndependent()
    {
        // 无论哪一路在前都要认出来（当前固定先麦克风，但判定不该依赖顺序）
        string sysFirst = TranscriptSections.SystemLabel + "\n课件\n\n" + TranscriptSections.MicrophoneLabel + "\n现场";

        Assert.True(TranscriptSections.IsMultiSource(sysFirst));
    }

    [Fact]
    public void LabelFor_MapsEachSource()
    {
        Assert.Equal(TranscriptSections.MicrophoneLabel, TranscriptSections.LabelFor(RecordingAudioSource.Microphone));
        Assert.Equal(TranscriptSections.SystemLabel, TranscriptSections.LabelFor(RecordingAudioSource.System));
    }

    [Fact]
    public void MultiSourceRule_UsesTheSameLabelsAsRender()
    {
        // 规则文本里提到的分区名必须与实际渲染出来的标注一致
        Assert.Contains("麦克风（教室现场）", TranscriptSections.MultiSourceRule);
        Assert.Contains("系统声音（课件/网课播放）", TranscriptSections.MultiSourceRule);
    }

    [Fact]
    public void RecordingAudio_ExposesPathsPerSource()
    {
        var audio = new RecordingAudio(new[]
        {
            new RecordingAudioFile(RecordingAudioSource.Microphone, @"C:\tmp\mic.wav"),
            new RecordingAudioFile(RecordingAudioSource.System, @"C:\tmp\system.wav"),
        });

        Assert.False(audio.IsEmpty);
        Assert.Equal(@"C:\tmp\mic.wav", audio.MicrophonePath);
        Assert.Equal(@"C:\tmp\system.wav", audio.SystemPath);
    }

    [Fact]
    public void RecordingAudio_SingleTrack_OtherPathIsNull()
    {
        var micOnly = new RecordingAudio(new[]
        {
            new RecordingAudioFile(RecordingAudioSource.Microphone, @"C:\tmp\mic.wav"),
        });

        Assert.Equal(@"C:\tmp\mic.wav", micOnly.MicrophonePath);
        Assert.Null(micOnly.SystemPath);

        Assert.True(RecordingAudio.None.IsEmpty);
        Assert.Null(RecordingAudio.None.MicrophonePath);
        Assert.Null(RecordingAudio.None.SystemPath);
    }

    [Fact]
    public void Build_MultiSourceTranscript_FitsInOneSegmentWhenShort()
    {
        // 两端标注本身会占字符，短素材仍应保持单段（多来源不该无谓地触发分段）
        string text = TranscriptSections.Render(new[]
        {
            new TranscriptTrack(RecordingAudioSource.Microphone, "现场内容"),
            new TranscriptTrack(RecordingAudioSource.System, "课件内容"),
        });

        var plan = NoteSegmenter.Build("高等数学", text, "");

        Assert.False(plan.IsSegmented);
        Assert.Single(plan.Segments);
        Assert.Contains(TranscriptSections.MicrophoneLabel, plan.Segments[0].Transcript);
        Assert.Contains(TranscriptSections.SystemLabel, plan.Segments[0].Transcript);
    }

    [Fact]
    public void Build_LongMultiSourceTranscript_EverySegmentKeepsBothLabels()
    {
        // 分段后每一段都必须仍带两个标注行，否则该段的模型不知道两边的来源
        string mic = new string('a', 12000);
        string sys = new string('b', 12000);
        string text = TranscriptSections.Render(new[]
        {
            new TranscriptTrack(RecordingAudioSource.Microphone, mic),
            new TranscriptTrack(RecordingAudioSource.System, sys),
        });

        var plan = NoteSegmenter.Build("高等数学", text, "");

        Assert.True(plan.IsSegmented);
        Assert.All(plan.Segments, s => Assert.Contains(TranscriptSections.MicrophoneLabel, s.Transcript));
        Assert.All(plan.Segments, s => Assert.Contains(TranscriptSections.SystemLabel, s.Transcript));
    }
}
