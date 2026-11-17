namespace ClassNote.Services;

/// <summary>
/// 生产环境的笔记管线装配（**唯一来源**）：录音结束后的处理与笔记页的「重新生成」
/// 必须用同一套依赖，否则两条路会因为 OCR/STT 实例不同而产出不一样的结果。
/// STT 用进程级共享实例——模型 241 MB / 加载 6–33 秒，绝不能每次重建。
/// </summary>
public static class NoteProcessorFactory
{
    /// <summary>按当前设置装配一条完整管线。</summary>
    /// <param name="ocr">可选：复用一个已有的 OCR 实例（录音页需要与后台 OCR 队列共用）。</param>
    /// <param name="ocrQueue">可选：录音期间的后台 OCR 队列。</param>
    public static NoteProcessor CreateDefault(IOcrService? ocr = null, BackgroundOcrQueue? ocrQueue = null)
    {
        var stt = SenseVoiceSttService.Shared;
        var ocrService = ocr ?? new WindowsOcrService();
        var repo = LocalRepository.Instance;
        return new NoteProcessor(stt, ocrService, new LlmService(),
            new TranscriptAssembler(repo, stt), ocrQueue);
    }
}
