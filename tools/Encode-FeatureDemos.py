"""將 Render-FeatureDemos.mjs 的確定性畫格轉為 GIF、靜態圖與驗收縮圖。"""

import json
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "artifacts" / "feature-demos"
TARGET = ROOT / "docs" / "images"
catalog = json.loads((SOURCE / "manifest.json").read_text(encoding="utf-8"))
overview = []

for name, demo in catalog.items():
    frames = []
    for i in range(len(demo["frames"])):
        with Image.open(SOURCE / name / f"{i:03}.png") as source:
            frames.append(source.convert("RGB"))
    # 所有狀態共用色盤，避免游標或語法著色切換時整張圖閃色。
    sample = Image.new("RGB", (1100, 620 * len(frames)), "white")
    for i, frame in enumerate(frames):
        sample.paste(frame, (0, i * 620))
    palette = sample.quantize(colors=176)
    # 保留面積很小的 SQL 語法色；縮圖取樣會把純藍與白底平均成灰藍。
    reserved = [0, 0, 255, 0, 128, 128, 163, 21, 21, 0, 128, 0,
                255, 255, 255, 36, 36, 36, 126, 101, 29, 104, 82, 27]
    colors = reserved + palette.getpalette()[:176 * 3]
    palette.putpalette(colors + [0] * (768 - len(colors)))
    indexed = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
    target = TARGET / f"{name}-demo.gif"
    indexed[0].save(target, save_all=True, append_images=indexed[1:],
                    duration=demo["frames"], loop=0, optimize=True, disposal=1)
    frames[demo["poster"]].save(TARGET / f"{name}-demo.png", optimize=True)
    with Image.open(target) as check:
        duration = 0
        expected_index = 0
        expected_remaining = demo["frames"][0]
        poster_time = sum(demo["frames"][:demo["poster"]])
        poster_frame = 0
        for i in range(check.n_frames):
            check.seek(i)
            frame_duration = check.info["duration"]
            if duration <= poster_time < duration + frame_duration:
                poster_frame = i
            duration += frame_duration
            if check.size != (1100, 620):
                raise ValueError(f"{name}: 畫布尺寸錯誤")
            # GIF 編碼器會合併相鄰的相同畫格，必須比時間軸，不能直接比索引。
            remaining = frame_duration
            while remaining:
                if expected_index >= len(indexed):
                    raise ValueError(f"{name}: 動畫超過來源時間軸")
                if ImageChops.difference(check.convert("RGB"), indexed[expected_index].convert("RGB")).getbbox():
                    raise ValueError(f"{name}: 第 {i} 格有殘影或色盤變化")
                consumed = min(remaining, expected_remaining)
                remaining -= consumed
                expected_remaining -= consumed
                if expected_remaining == 0:
                    expected_index += 1
                    if expected_index < len(indexed):
                        expected_remaining = demo["frames"][expected_index]
        if duration != sum(demo["frames"]) or check.info.get("loop") != 0:
            raise ValueError(f"{name}: 播放時間或循環設定錯誤")
        if check.n_frames < 2 or target.stat().st_size > 2_000_000:
            raise ValueError(f"{name}: 缺少動畫或檔案過大")
        print(f"{target.name}: {check.n_frames} frames, {duration / 1000:.2f}s, {target.stat().st_size:,} bytes")
        check.seek(poster_frame)
        check.convert("RGB").resize((820, 462), Image.Resampling.LANCZOS).save(
            SOURCE / f"{name}-readme-size.png", optimize=True)
    sheet = Image.new("RGB", (1100, 334 * ((len(frames) + 1) // 2)), "#eeeeee")
    draw = ImageDraw.Draw(sheet)
    for i, frame in enumerate(frames):
        x, y = i % 2 * 550, i // 2 * 334
        draw.text((x + 8, y + 5), f"{name} / {i:02} / {demo['frames'][i]} ms", fill="black")
        sheet.paste(frame.resize((550, 310)), (x, y + 24))
    sheet.save(SOURCE / f"{name}-contact.png", optimize=True)
    overview.append(frames[demo["poster"]])

sheet = Image.new("RGB", (1100, 310 * ((len(overview) + 1) // 2)), "white")
for i, frame in enumerate(overview):
    sheet.paste(frame.resize((550, 310)), (i % 2 * 550, i // 2 * 310))
sheet.save(SOURCE / "overview.png", optimize=True)
