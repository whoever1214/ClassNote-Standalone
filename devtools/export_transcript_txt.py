# -*- coding: utf-8 -*-
"""导出最近一次课堂记录（最新会话）的 STT 转写全文 + OCR 截图识别全文到一个 txt。

数据来源：%LOCALAPPDATA%\ClassNote\classnote.db
  - transcript_chunks(session_id, track, chunk_index, text)：track 0=麦克风，1=系统声音
  - screenshots(session_id, seq_no, timestamp, type, ocr_text)
拼接口径与 App 内 NoteProcessor 完全一致：
  - 同一路各块用单个空格连接后 trim（SenseVoiceSttService.JoinChunks）
  - 多路按【麦克风（教室现场）】/【系统声音（课件/网课播放）】分区
  - OCR 条目标记 [type @ hh:mm:ss]（NoteProcessor.FormatTime）
"""
import os
import re
import sqlite3
import datetime

DB = os.path.join(os.environ['LOCALAPPDATA'], 'ClassNote', 'classnote.db')
DESKTOP = os.path.join(os.environ['USERPROFILE'], 'Desktop')

MIC_LABEL = '【麦克风（教室现场）】'
SYS_LABEL = '【系统声音（课件/网课播放）】'

TAG_RE = re.compile(r'<\|[^|]*\|>')      # SenseVoice 事件标签，如 <|Speech|><|woitn|>
EMOTE_RE = re.compile(r'\[[^\]]{0,20}\]')  # 情感/事件标记，如 [laughter]


def clean_chunk(text):
    """清洗块文本：去掉事件标签与方括号标记，压缩空白（与 App 侧清洗同义）。"""
    if not text:
        return ''
    text = TAG_RE.sub('', text)
    text = EMOTE_RE.sub('', text)
    return re.sub(r'\s+', ' ', text).strip()


def join_chunks(texts):
    """JoinChunks：非空块用单个空格连接后 trim（唯一的拼接出处）。"""
    return ' '.join(t for t in (clean_chunk(x) for x in texts) if t).strip()


def fmt_time(seconds):
    """NoteProcessor.FormatTime -> hh:mm:ss"""
    seconds = int(round(float(seconds or 0)))
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f'{h:02d}:{m:02d}:{s:02d}'


def parse_iso(ts):
    if not ts:
        return None
    raw = ts.rstrip('Z')
    if '.' in raw:
        head, frac = raw.split('.', 1)
        raw = head + '.' + (frac + '000000')[:6]
    try:
        return datetime.datetime.fromisoformat(raw)
    except ValueError:
        return None


def hms(seconds):
    seconds = int(round(seconds or 0))
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f'{h}小时{m:02d}分{s:02d}秒' if h else f'{m}分{s:02d}秒'


def norm_for_dup(content):
    """与 NoteSegmenter.NormalizeForComparison 一致：去掉所有空白/全角空格。"""
    return ''.join(ch for ch in (content or '') if not ch.isspace() and ch != '\u3000')


def main():
    con = sqlite3.connect(f'file:{DB}?mode=ro', uri=True)
    con.text_factory = str
    cur = con.cursor()

    row = cur.execute("""
        SELECT id, course, title, start_time, end_time, duration, status
        FROM sessions ORDER BY start_time DESC LIMIT 1""").fetchone()
    if not row:
        raise SystemExit('库里没有任何会话记录')
    sid, course, title, start_time, end_time, duration, status = row
    print('最新会话:', sid, course, title, start_time)

    # ── STT：按 track 取块 ────────────────────────────────
    tracks = []
    for track, label in ((0, MIC_LABEL), (1, SYS_LABEL)):
        rows = cur.execute("""
            SELECT chunk_index, start_seconds, text FROM transcript_chunks
            WHERE session_id = ? AND track = ? ORDER BY chunk_index""", (sid, track)).fetchall()
        if not rows:
            continue
        text = join_chunks([r[2] for r in rows])
        tracks.append({
            'track': track, 'label': label, 'rows': rows, 'text': text,
            'chars': len(text), 'empty': sum(1 for r in rows if not (r[2] or '').strip()),
        })

    # 单路来源时不加分区标注（与 Render 一致：只保留非空的一路，单路无标注）
    multi = len([t for t in tracks if t['text']]) > 1

    # ── OCR：全部截图（含 ocr_text 非空者）────────────────
    shots = []
    for seq_no, ts, stype, ocr in cur.execute("""
            SELECT seq_no, timestamp, type, ocr_text FROM screenshots
            WHERE session_id = ? ORDER BY seq_no""", (sid,)):
        ocr = (ocr or '').replace('\r\n', '\n').replace('\r', '\n').strip('\n')
        shots.append({'seq': seq_no, 'ts': ts, 'type': stype, 'ocr': ocr,
                      'has': bool(ocr.strip())})

    # 折叠"相邻且归一化后完全相同"的重复条目（与 CollapseDuplicateOcr 同规则）
    dedup = []
    last_key = None
    for sh in shots:
        if not sh['has']:
            continue
        key = norm_for_dup(sh['ocr'])
        if key and key == last_key:
            if dedup:
                dedup[-1]['dup_count'] += 1
                dedup[-1]['dup_until'] = sh['ts']
            continue
        last_key = key
        item = dict(sh)
        item['dup_count'] = 0
        item['dup_until'] = None
        dedup.append(item)

    st = parse_iso(start_time)
    et = parse_iso(end_time)
    span = (et - st).total_seconds() if (st and et) else (duration or 0)

    L = []
    L.append('ClassNote 课堂记录导出（STT 语音转写 + OCR 截图文字）')
    L.append('=' * 60)
    L.append(f'导出时间：{datetime.datetime.now():%Y-%m-%d %H:%M:%S}')
    L.append(f'数据来源：{DB}')
    L.append(f'会话 ID：{sid}')
    L.append(f'课程：{course}' + (f'    标题：{title}' if title else ''))
    L.append(f'开始：{start_time}' + (f'    结束：{end_time}' if end_time else ''))
    L.append(f'时长：约 {hms(span)}    状态：{status}')
    L.append('')
    L.append('本次导出内容概览')
    L.append('-' * 60)
    for t in tracks:
        name = '麦克风（教室现场）' if t['track'] == 0 else '系统声音（课件/网课播放）'
        L.append(f'· STT track {t["track"]} {name}：{len(t["rows"])} 块 / {t["chars"]} 字'
                 + (f'（其中 {t["empty"]} 块为空）' if t['empty'] else ''))
    L.append(f'· OCR 截图：{len(shots)} 张（有文字 {sum(1 for s in shots if s["has"])} 张，'
             f'无文字 {sum(1 for s in shots if not s["has"])} 张）')
    L.append(f'· OCR 去重后条目：{len(dedup)} 条（相邻且内容完全相同的重复截图已折叠）')
    L.append('')
    L.append('=[ 第一部分：STT 语音转写全文 ]=' + '=' * 30)
    L.append('')
    if not tracks:
        L.append('（本次录音没有任何转写文本）')
    for t in tracks:
        content = t['text']
        if multi and content:
            L.append(t['label'])
        L.append(content if content else '（该路没有转写文本）')
        L.append('')
    L.append('')
    L.append('=[ 第二部分：OCR 截图识别全文 ]=' + '=' * 28)
    L.append('')
    L.append(f'--- A. 去重后（{len(dedup)} 条，与生成笔记时喂给模型的口径一致）---')
    L.append('')
    if not dedup:
        L.append('（本次没有任何截图识别文字）')
    for item in dedup:
        L.append(f'[{item["type"]} @ {fmt_time(item["ts"])}]')
        L.append(item['ocr'])
        if item['dup_count']:
            L.append(f'（此后 {item["dup_count"]} 张截图内容与之相同，'
                     f'时间到 {fmt_time(item["dup_until"])}）')
        L.append('')
    L.append('')
    L.append(f'--- B. 原始逐张清单（{len(shots)} 张，未去重、未删空）---')
    L.append('')
    for sh in shots:
        L.append(f'--- #{sh["seq"]:03d} [{sh["type"]} @ {fmt_time(sh["ts"])}] ---')
        L.append(sh['ocr'] if sh['has'] else '（无文字）')
        L.append('')
    L.append('=' * 60)
    L.append('（本文件由 ClassNote 数据库直接导出，STT/OCR 文本为引擎原始输出，未做人工修正）')

    text = '\n'.join(L).replace('\r\n', '\n') + '\n'

    safe = re.sub(r'[\\/:*?"<>|]', '_', f'{course or "未命名"}_{title or ""}'.strip('_')) or '未命名'
    stamp = st.strftime('%Y%m%d_%H%M') if st else datetime.datetime.now().strftime('%Y%m%d_%H%M')
    out = os.path.join(DESKTOP, f'ClassNote转写_{safe}_{stamp}.txt')
    n = 2
    while os.path.exists(out):
        out = os.path.join(DESKTOP, f'ClassNote转写_{safe}_{stamp}({n}).txt')
        n += 1
    with open(out, 'w', encoding='utf-8-sig', newline='\r\n') as f:
        f.write(text)
    print('已写出:', out)
    print('大小:', os.path.getsize(out), 'bytes /', len(text), 'chars')
    con.close()


if __name__ == '__main__':
    main()
