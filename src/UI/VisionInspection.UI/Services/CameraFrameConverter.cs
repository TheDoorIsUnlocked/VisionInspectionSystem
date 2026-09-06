using SkiaSharp;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 相机帧转换工具：把 CameraManager 槽位的最新帧（CameraImageData）转为 SKBitmap，
    /// 供 SOP 区域标定等多相机画面场景复用。
    /// </summary>
    public static class CameraFrameConverter
    {
        /// <summary>
        /// 将相机图像数据转换为 SKBitmap（Bgra8888）
        /// </summary>
        public static SKBitmap? ToSKBitmap(CameraImageData imageData)
        {
            try
            {
                if (imageData?.Data == null || imageData.Data.Length == 0)
                    return null;

                var info = new SKImageInfo(imageData.Width, imageData.Height, SKColorType.Bgra8888);
                var bitmap = new SKBitmap(info);

                if (imageData.IsColor && imageData.Channels == 3)
                {
                    ConvertRgb24ToBgra32(imageData.Data, bitmap, imageData.Width, imageData.Height);
                }
                else
                {
                    ConvertGray8ToBgra32(imageData.Data, bitmap, imageData.Width, imageData.Height);
                }

                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>创建深灰色占位帧（用于某相机当前无画面但 YAML 已含其区域时的标定画布）</summary>
        public static SKBitmap CreatePlaceholderFrame(int width = 1920, int height = 1080)
        {
            var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor(51, 51, 51));
            return bitmap;
        }

        /// <summary>将 RGB24 数据转换为 BGRA32</summary>
        private static unsafe void ConvertRgb24ToBgra32(byte[] rgbData, SKBitmap bitmap, int width, int height)
        {
            byte* ptr = (byte*)bitmap.GetPixels().ToPointer();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = (y * width + x) * 3;
                    int dstIndex = (y * width + x) * 4;

                    ptr[dstIndex] = rgbData[srcIndex + 2];     // B
                    ptr[dstIndex + 1] = rgbData[srcIndex + 1]; // G
                    ptr[dstIndex + 2] = rgbData[srcIndex];     // R
                    ptr[dstIndex + 3] = 255;                   // A
                }
            }
        }

        /// <summary>将 Gray8 数据转换为 BGRA32</summary>
        private static unsafe void ConvertGray8ToBgra32(byte[] grayData, SKBitmap bitmap, int width, int height)
        {
            byte* ptr = (byte*)bitmap.GetPixels().ToPointer();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int srcIndex = y * width + x;
                    int dstIndex = srcIndex * 4;
                    byte gray = grayData[srcIndex];

                    ptr[dstIndex] = gray;     // B
                    ptr[dstIndex + 1] = gray; // G
                    ptr[dstIndex + 2] = gray; // R
                    ptr[dstIndex + 3] = 255;  // A
                }
            }
        }
    }
}
