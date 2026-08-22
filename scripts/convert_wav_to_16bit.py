#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
将 WAV 从任意位深（如 24-bit）转换为 16-bit PCM，使 System.Media.SoundPlayer 可播放。
SoundPlayer 仅支持 8/16-bit PCM，24-bit 会静默失败（错误发生在内部播放线程，无异常抛出）。
用法：python convert_wav_to_16bit.py <input.wav> <output.wav>
"""
import sys, struct, wave

def convert(src, dst):
    w = wave.open(src, 'rb')
    nch = w.getnchannels()
    sw = w.getsampwidth()
    fr = w.getframerate()
    nframes = w.getnframes()
    raw = w.readframes(nframes)
    w.close()

    print(f"[conv] 源: channels={nch} sampwidth={sw} rate={fr} frames={nframes}")

    if sw == 2:
        # 已经是 16-bit，直接复制
        with open(dst, 'wb') as f:
            w2 = wave.open(f, 'wb')
            w2.setnchannels(nch); w2.setsampwidth(2); w2.setframerate(fr)
            w2.writeframes(raw)
            w2.close()
        print(f"[conv] 已是 16-bit，直接复制 -> {dst}")
        return

    # 读取 sw 字节的有符号样本（little-endian），转成 16-bit
    samples = []
    for i in range(0, len(raw), sw * nch):
        frame = raw[i:i + sw * nch]
        if len(frame) < sw * nch:
            break
        for c in range(nch):
            chunk = frame[c*sw:(c+1)*sw]
            # 符号扩展到 32 位
            val = int.from_bytes(chunk, 'little', signed=True)
            # 24-bit -> 右移 8 位得到 16-bit（等同截断低 8 位）
            val16 = val >> (8 * (sw - 2)) if sw > 2 else val
            # 夹紧到 16-bit 范围
            if val16 > 32767: val16 = 32767
            if val16 < -32768: val16 = -32768
            samples.append(val16)

    out = b''.join(struct.pack('<h', s) for s in samples)
    with open(dst, 'wb') as f:
        w2 = wave.open(f, 'wb')
        w2.setnchannels(nch); w2.setsampwidth(2); w2.setframerate(fr)
        w2.writeframes(out)
        w2.close()
    print(f"[conv] 已转 16-bit -> {dst} (channels={nch} rate={fr} samples={len(samples)})")

if __name__ == '__main__':
    if len(sys.argv) != 3:
        print("usage: python convert_wav_to_16bit.py <in.wav> <out.wav>")
        sys.exit(1)
    convert(sys.argv[1], sys.argv[2])
