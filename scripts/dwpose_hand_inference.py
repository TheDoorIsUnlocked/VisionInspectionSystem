#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
DWPose 手部 21 关键点推理脚本（ONNX 直跑版）
===========================================
直接加载项目里已有的两个 ONNX 模型，复现 C# 端 DWPoseHandDetector 的双阶段逻辑，
用于快速验证 DWPose 是否比 MediaPipe 更丝滑、遮挡/握拳场景更稳。

模型（已下载好，见 E:/yolo/YoloDotNet-master/DWPose-onnx/models）：
  - yolox_l.onnx          人体检测（YOLOX-L, 输入 640x640）
  - dw-ll_ucoco_384.onnx  全身姿态（DWPose-L, SIMCC 输出 133 关键点，含双手各 21 点）

依赖：
  pip install onnxruntime opencv-python numpy

用法：
  python dwpose_hand_inference.py --source 0              # 摄像头实时
  python dwpose_hand_inference.py --source frame.jpg      # 单张图片
  python dwpose_hand_inference.py --source clip.mp4 --out out.mp4

说明：
  - 与 C# 端 DWPoseHandDetector 推理逻辑一一对应（YOLOX 检测 -> DWPose 姿态 -> SIMCC 解码 -> 手部 91-112/113-134）。
  - 关键点顺序与 MediaPipe 21 点一致，便于直接替换。
  - Python 端采用标准 YOLOX 预处理（/255）；C# 端 PreprocessForDetection 当前未做 /255，
    若你发现 C# 端人体检测召回偏低，可在该处 tensor 后补 `tensor /= 255f`（见接入指南「调参」一节）。
"""

import argparse
import os
import sys

import numpy as np
import cv2

try:
    import onnxruntime as ort
except ImportError:
    sys.exit("请先安装 onnxruntime: pip install onnxruntime opencv-python numpy")


# ---------- 路径配置（与 C# 端 DWPoseHandEstimationService 默认一致）----------
DEFAULT_DET = r"e:\yolo\YoloDotNet-master\DWPose-onnx\models\yolox_l.onnx"
DEFAULT_POSE = r"e:\yolo\YoloDotNet-master\DWPose-onnx\models\dw-ll_ucoco_384.onnx"

# COCO-WholeBody 关键点布局：左手 91-112，右手 113-134（每个含 wrist + 20 点 = 21）
LEFT_HAND_START = 91
RIGHT_HAND_START = 113

# 21 点手部骨架连接（与项目 HandSkeletonConnections 完全一致）
HAND_CONNECTIONS = [
    (0, 1), (0, 5), (0, 9), (0, 13), (0, 17),   # 手腕 -> 各指根
    (1, 2), (2, 3), (3, 4),                     # 拇指
    (5, 6), (6, 7), (7, 8),                     # 食指
    (9, 10), (10, 11), (11, 12),                # 中指
    (13, 14), (14, 15), (15, 16),               # 无名指
    (17, 18), (18, 19), (19, 20),               # 小指
]

COL_DET = (0, 255, 0)      # 人体框：绿
COL_HAND = (0, 0, 255)     # 手部关键点：红


def load_session(path: str, use_gpu: bool):
    if not os.path.exists(path):
        sys.exit(f"[错误] 模型不存在: {path}")
    providers = (["CUDAExecutionProvider", "CPUExecutionProvider"]
                 if use_gpu else ["CPUExecutionProvider"])
    return ort.InferenceSession(path, providers=providers)


# ---------- 人体检测 (YOLOX) ----------
def preprocess_det(img: np.ndarray, size: int = 640):
    h, w = img.shape[:2]
    scale = min(size / w, size / h)
    nw, nh = int(w * scale), int(h * scale)
    resized = cv2.resize(img, (nw, nh))
    canvas = np.full((size, size, 3), 114, dtype=np.uint8)
    canvas[:nh, :nw] = resized
    # BGR -> RGB, HWC -> CHW, /255（标准 YOLOX 预处理）
    blob = canvas[:, :, ::-1].astype(np.float32).transpose(2, 0, 1)
    blob = np.expand_dims(blob, 0) / 255.0
    return blob, scale


def decode_yolox(out: np.ndarray, size: int = 640, conf: float = 0.1):
    """YOLOX 输出 [1, N, 85] -> (cx,cy,w,h,obj),cls..."""
    dets = out[0]
    strides = [8, 16, 32]
    grids, expanded = [], []
    for s in strides:
        for gy in range(size // s):
            for gx in range(size // s):
                grids.append((gx, gy))
                expanded.append(s)
    boxes = []
    for i in range(dets.shape[0]):
        if i >= len(grids):
            break
        cx = (dets[i, 0] + grids[i][0]) * expanded[i]
        cy = (dets[i, 1] + grids[i][1]) * expanded[i]
        bw = np.exp(dets[i, 2]) * expanded[i]
        bh = np.exp(dets[i, 3]) * expanded[i]
        obj = dets[i, 4]
        cls_score = dets[i, 5:].max()
        score = obj * cls_score
        if score < conf:
            continue
        if int(cls_score.argmax()) != 0:   # 只保留 person
            continue
        boxes.append([cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2, score])
    return np.array(boxes, dtype=np.float32)


def nms(boxes: np.ndarray, iou_thr: float = 0.3):
    if len(boxes) == 0:
        return []
    idxs = boxes[:, 4].argsort()[::-1]
    keep = []
    while len(idxs):
        i = idxs[0]
        keep.append(i)
        if len(idxs) == 1:
            break
        rest = idxs[1:]
        xx1 = np.maximum(boxes[i, 0], boxes[rest, 0])
        yy1 = np.maximum(boxes[i, 1], boxes[rest, 1])
        xx2 = np.minimum(boxes[i, 2], boxes[rest, 2])
        yy2 = np.minimum(boxes[i, 3], boxes[rest, 3])
        inter = np.maximum(0, xx2 - xx1) * np.maximum(0, yy2 - yy1)
        area_i = (boxes[i, 2] - boxes[i, 0]) * (boxes[i, 3] - boxes[i, 1])
        area_r = (boxes[rest, 2] - boxes[rest, 0]) * (boxes[rest, 3] - boxes[rest, 1])
        iou = inter / (area_i + area_r - inter + 1e-6)
        idxs = rest[iou < iou_thr]
    return keep


# ---------- 姿态 (DWPose SIMCC) ----------
IMAGENET_MEAN = np.array([123.675, 116.28, 103.53], dtype=np.float32)
IMAGENET_STD = np.array([58.395, 57.12, 57.375], dtype=np.float32)


def preprocess_pose(img: np.ndarray, person_box, in_w: int, in_h: int):
    x1, y1, x2, y2 = person_box
    cx = (x1 + x2) / 2.0
    cy = (y1 + y2) / 2.0
    bw = (x2 - x1) * 1.25
    bh = (y2 - y1) * 1.25
    if bw / in_w > bh / in_h:
        bh = bw * in_h / in_w
    else:
        bw = bh * in_w / in_h
    scale = np.array([bw, bh], dtype=np.float32)
    canvas = np.zeros((in_h, in_w, 3), dtype=np.uint8)
    for y in range(in_h):
        for x in range(in_w):
            sx = int(np.clip(cx + (x - in_w / 2.0) / (in_w / bw), 0, img.shape[1] - 1))
            sy = int(np.clip(cy + (y - in_h / 2.0) / (in_h / bh), 0, img.shape[0] - 1))
            canvas[y, x] = img[sy, sx]
    blob = canvas[:, :, ::-1].astype(np.float32)   # BGR->RGB
    blob = (blob - IMAGENET_MEAN) / IMAGENET_STD
    blob = blob.transpose(2, 0, 1)[None]
    return blob, np.array([cx, cy], dtype=np.float32), scale


def decode_simcc(simcc_x, simcc_y, center, scale, in_w, in_h, split: float = 2.0):
    kx = simcc_x[0].argmax(-1)
    ky = simcc_y[0].argmax(-1)
    x = kx / split / in_w * scale[0] + center[0] - scale[0] / 2
    y = ky / split / in_h * scale[1] + center[1] - scale[1] / 2
    conf = np.maximum(simcc_x[0].max(-1), simcc_y[0].max(-1))
    return np.stack([x, y, conf], -1)   # (133, 3)


def extract_hand(kpts: np.ndarray, start: int):
    wrist = kpts[start]
    if wrist[2] < 0.25:
        return None
    pts = kpts[start:start + 21]
    if (pts[:, 2] > 0.2).sum() < 3:    # 至少 3 个指尖有效
        return None
    return pts


def draw_hand(img: np.ndarray, pts: np.ndarray, color):
    for a, b in HAND_CONNECTIONS:
        if pts[a, 2] > 0.1 and pts[b, 2] > 0.1:
            pa = (int(pts[a, 0]), int(pts[a, 1]))
            pb = (int(pts[b, 0]), int(pts[b, 1]))
            cv2.line(img, pa, pb, color, 2)
    for p in pts:
        if p[2] > 0.1:
            cv2.circle(img, (int(p[0]), int(p[1])), 3, color, -1)


def process_image(session_det, session_pose, img: np.ndarray):
    det_in, scale = preprocess_det(img)
    det_out = session_det.run(None, {"images": det_in})[0]
    boxes = decode_yolox(det_out)
    if len(boxes):
        keep = nms(boxes)
        boxes = boxes[keep]
        boxes[:, :4] /= scale   # 还原到原图坐标
    for b in boxes:
        x1, y1, x2, y2 = b[:4]
        cv2.rectangle(img, (int(x1), int(y1)), (int(x2), int(y2)), COL_DET, 2)
        pose_in, center, pscale = preprocess_pose(img, [x1, y1, x2, y2], 384, 384)
        pose_out = session_pose.run(None, {"input": pose_in})
        kpts = decode_simcc(pose_out[0], pose_out[1], center, pscale, 384, 384)
        lh = extract_hand(kpts, LEFT_HAND_START)
        rh = extract_hand(kpts, RIGHT_HAND_START)
        if lh is not None:
            draw_hand(img, lh, COL_HAND)
        if rh is not None:
            draw_hand(img, rh, COL_HAND)
    return img


def main():
    ap = argparse.ArgumentParser(description="DWPose 手部 21 关键点推理（验证用）")
    ap.add_argument("--det", default=DEFAULT_DET, help="YOLOX 人体检测 ONNX")
    ap.add_argument("--pose", default=DEFAULT_POSE, help="DWPose 姿态 ONNX")
    ap.add_argument("--source", default="0", help="摄像头序号，或图片/视频路径")
    ap.add_argument("--out", default="", help="输出视频/图片路径")
    ap.add_argument("--cpu", action="store_true", help="强制使用 CPU")
    args = ap.parse_args()

    use_gpu = not args.cpu
    sdet = load_session(args.det, use_gpu)
    spose = load_session(args.pose, use_gpu)
    print(f"[OK] 模型已加载 | det={os.path.basename(args.det)} pose={os.path.basename(args.pose)} | GPU={use_gpu}")

    src = args.source
    def _show_frame(title, frame):
        try:
            cv2.imshow(title, frame)
            return True
        except cv2.error:
            return False  # headless 环境无 GUI

    if src.isdigit():
        cap = cv2.VideoCapture(int(src))
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            out = process_image(sdet, spose, frame)
            if _show_frame("DWPose Hand (ESC 退出)", out) and cv2.waitKey(1) & 0xFF == 27:
                break
        cap.release()
        cv2.destroyAllWindows()
    elif src.lower().endswith((".mp4", ".avi", ".mov")):
        cap = cv2.VideoCapture(src)
        out_path = args.out or "dwpose_out.mp4"
        vw = cv2.VideoWriter(out_path, cv2.VideoWriter_fourcc(*"mp4v"), 20,
                              (int(cap.get(3)), int(cap.get(4))))
        while True:
            ok, frame = cap.read()
            if not ok:
                break
            out = process_image(sdet, spose, frame)
            vw.write(out)
            if _show_frame("DWPose Hand (ESC 退出)", out) and cv2.waitKey(1) & 0xFF == 27:
                break
        cap.release()
        vw.release()
        cv2.destroyAllWindows()
        print(f"[OK] 视频已保存: {out_path}")
    else:
        img = cv2.imread(src)
        if img is None:
            sys.exit("图片读取失败")
        out = process_image(sdet, spose, img)
        out_path = args.out or "dwpose_result.jpg"
        cv2.imwrite(out_path, out)
        print(f"[OK] 结果已保存: {out_path}")
        try:
            cv2.imshow("DWPose Hand (任意键退出)", out)
            cv2.waitKey(0)
            cv2.destroyAllWindows()
        except cv2.error:
            # headless 环境无 GUI，跳过显示
            pass


if __name__ == "__main__":
    main()
