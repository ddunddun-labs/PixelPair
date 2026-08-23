using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PixelPair
{
    // 크로스플랫폼 픽셀/이미지 처리 (ImageSharp 기반, GDI+ 미사용)
    public static class ImageEngine
    {
        public static List<PixelInfo> DecodeToPixelGrid(byte[] imageBytes, int gridSize)
        {
            using var image = LoadImageFromBytes(imageBytes);
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(gridSize, gridSize),
                Sampler = KnownResamplers.NearestNeighbor,
                Mode = ResizeMode.Stretch
            }));

            var pixels = new List<PixelInfo>();
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    Rgba32 c = image[x, y];
                    if (c.A > 64)
                        pixels.Add(new PixelInfo { X = x, Y = y, Color = ToHex(c) });
                }
            }
            return pixels;
        }

        private static Image<Rgba32> LoadImageFromBytes(byte[] imageBytes)
        {
            if (imageBytes.Length >= 6 && imageBytes[0] == 0 && imageBytes[1] == 0 && imageBytes[2] == 1 && imageBytes[3] == 0)
            {
                var icoImg = TryLoadIco(imageBytes);
                if (icoImg != null) return icoImg;
            }
            return Image.Load<Rgba32>(imageBytes);
        }

        private static Image<Rgba32>? TryLoadIco(byte[] icoBytes)
        {
            if (icoBytes.Length < 6) return null;
            ushort count = BitConverter.ToUInt16(icoBytes, 4);
            if (count == 0) return null;

            int bestIndex = -1;
            int bestScore = -1;

            for (int i = 0; i < count; i++)
            {
                int entryOffset = 6 + i * 16;
                if (entryOffset + 16 > icoBytes.Length) break;
                int w = icoBytes[entryOffset];
                int h = icoBytes[entryOffset + 1];
                if (w == 0) w = 256;
                if (h == 0) h = 256;
                ushort bpp = BitConverter.ToUInt16(icoBytes, entryOffset + 6);
                int score = w * h + bpp;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) return null;
            int bestEntry = 6 + bestIndex * 16;
            uint bytesInRes = BitConverter.ToUInt32(icoBytes, bestEntry + 8);
            uint imageOffset = BitConverter.ToUInt32(icoBytes, bestEntry + 12);

            if (imageOffset + bytesInRes > icoBytes.Length) return null;
            var subBytes = new byte[bytesInRes];
            Array.Copy(icoBytes, imageOffset, subBytes, 0, bytesInRes);

            // Check if embedded PNG
            if (subBytes.Length >= 8 && subBytes[0] == 0x89 && subBytes[1] == 0x50 && subBytes[2] == 0x4E && subBytes[3] == 0x47)
            {
                return Image.Load<Rgba32>(subBytes);
            }

            // DIB format
            if (subBytes.Length >= 40)
            {
                int dibW = BitConverter.ToInt32(subBytes, 4);
                int dibH = BitConverter.ToInt32(subBytes, 8) / 2; // XOR + AND mask combined height
                ushort dibBpp = BitConverter.ToUInt16(subBytes, 14);

                if (dibW > 0 && dibH > 0)
                {
                    var img = new Image<Rgba32>(dibW, dibH);
                    int offset = 40;

                    if (dibBpp == 32)
                    {
                        // 32-bit BGRA (bottom-up)
                        for (int y = dibH - 1; y >= 0; y--)
                        {
                            for (int x = 0; x < dibW; x++)
                            {
                                int idx = offset + (y * dibW + x) * 4;
                                if (idx + 3 < subBytes.Length)
                                {
                                    byte b = subBytes[idx];
                                    byte g = subBytes[idx + 1];
                                    byte r = subBytes[idx + 2];
                                    byte a = subBytes[idx + 3];
                                    img[x, dibH - 1 - y] = new Rgba32(r, g, b, a);
                                }
                            }
                        }
                        return img;
                    }
                    else if (dibBpp == 24)
                    {
                        // 24-bit BGR with row stride padding
                        int rowStride = ((dibW * 3 + 3) / 4) * 4;
                        for (int y = dibH - 1; y >= 0; y--)
                        {
                            for (int x = 0; x < dibW; x++)
                            {
                                int idx = offset + y * rowStride + x * 3;
                                if (idx + 2 < subBytes.Length)
                                {
                                    byte b = subBytes[idx];
                                    byte g = subBytes[idx + 1];
                                    byte r = subBytes[idx + 2];
                                    img[x, dibH - 1 - y] = new Rgba32(r, g, b, 255);
                                }
                            }
                        }
                        return img;
                    }
                }
            }

            return null;
        }

        public static byte[] RenderPng(Rgba32?[,] pixels, int gridSize, int outputSize)
        {
            using var image = new Image<Rgba32>(outputSize, outputSize);
            int sc = Math.Max(1, outputSize / gridSize);
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    if (pixels[x, y] is not Rgba32 c) continue;
                    FillBlock(image, x * sc, y * sc, sc, sc, c);
                }
            }
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        public static byte[] RenderRegionPng(Rgba32?[,] pixels, int gridSize, int x0, int y0, int w, int h, int scale)
        {
            int sc = Math.Max(1, scale);
            using var image = new Image<Rgba32>(Math.Max(1, w * sc), Math.Max(1, h * sc));
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int gx = x0 + x, gy = y0 + y;
                    if (gx < 0 || gy < 0 || gx >= gridSize || gy >= gridSize) continue;
                    if (pixels[gx, gy] is not Rgba32 c) continue;
                    FillBlock(image, x * sc, y * sc, sc, sc, c);
                }
            }
            using var ms = new MemoryStream();
            image.SaveAsPng(ms);
            return ms.ToArray();
        }

        public static void SavePng(string path, Rgba32?[,] pixels, int gridSize, int outputSize)
        {
            File.WriteAllBytes(path, RenderPng(pixels, gridSize, outputSize));
        }

        // PNG 기반 고품질 ICO 바이트 생성
        public static byte[] RenderIco(Rgba32?[,] pixels, int gridSize, int outputSize)
        {
            byte[] pngBytes = RenderPng(pixels, gridSize, outputSize);
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((short)0);      // Reserved
                writer.Write((short)1);      // Type (1 = Icon)
                writer.Write((short)1);      // Count

                byte w = (byte)(outputSize >= 256 ? 0 : outputSize);
                byte h = (byte)(outputSize >= 256 ? 0 : outputSize);
                writer.Write(w);
                writer.Write(h);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(pngBytes.Length);
                writer.Write(22);
                writer.Write(pngBytes);
            }
            return ms.ToArray();
        }

        public static void SaveIco(string path, Rgba32?[,] pixels, int gridSize, int outputSize)
        {
            File.WriteAllBytes(path, RenderIco(pixels, gridSize, outputSize));
        }

        private static void FillBlock(Image<Rgba32> image, int px, int py, int w, int h, Rgba32 color)
        {
            int maxX = Math.Min(image.Width, px + w);
            int maxY = Math.Min(image.Height, py + h);
            for (int y = Math.Max(0, py); y < maxY; y++)
                for (int x = Math.Max(0, px); x < maxX; x++)
                    image[x, y] = color;
        }

        public static int ColorDistance(Rgba32 a, Rgba32 b) =>
            Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        public static Rgba32? ParseHex(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            hex = hex.Trim();
            if (hex.StartsWith('#')) hex = hex[1..];

            string full = hex.Length switch
            {
                3 => string.Concat(hex.Select(c => new string(c, 2))),
                6 or 8 => hex,
                _ => ""
            };
            if (full.Length != 6 && full.Length != 8) return null;

            if (!byte.TryParse(full[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)) return null;
            if (!byte.TryParse(full[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)) return null;
            if (!byte.TryParse(full[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) return null;
            byte a = 255;
            if (full.Length == 8 && !byte.TryParse(full[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a)) return null;
            return new Rgba32(r, g, b, a);
        }

        public static string ToHex(Rgba32 c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>불투명 픽셀을 maxColors개로 줄인다. 이미 이하면 그대로.</summary>
        public static List<PixelInfo> LimitColors(List<PixelInfo> pixels, int maxColors)
        {
            if (maxColors < 2 || pixels.Count == 0) return pixels;
            var parsed = new List<(PixelInfo p, Rgba32 c)>();
            foreach (var p in pixels)
            {
                if (ParseHex(p.Color) is Rgba32 col) parsed.Add((p, col));
            }
            var unique = parsed.Select(x => ToHex(x.c)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (unique <= maxColors) return pixels;

            var palette = MedianCut(parsed.Select(x => x.c).ToList(), maxColors);
            foreach (var (p, c) in parsed)
            {
                Rgba32 best = palette[0];
                int bestD = int.MaxValue;
                foreach (var pal in palette)
                {
                    int d = ColorDistance(c, pal);
                    if (d < bestD) { bestD = d; best = pal; }
                }
                p.Color = ToHex(best);
            }
            return pixels;
        }

        private static List<Rgba32> MedianCut(List<Rgba32> colors, int maxColors)
        {
            var boxes = new List<List<Rgba32>> { colors };
            while (boxes.Count < maxColors)
            {
                int idx = 0;
                int bestRange = -1;
                for (int i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    int r = b.Max(c => c.R) - b.Min(c => c.R);
                    int g = b.Max(c => c.G) - b.Min(c => c.G);
                    int bl = b.Max(c => c.B) - b.Min(c => c.B);
                    int range = Math.Max(r, Math.Max(g, bl));
                    if (range > bestRange && b.Count > 1) { bestRange = range; idx = i; }
                }
                if (bestRange <= 0) break;
                var box = boxes[idx];
                int rr = box.Max(c => c.R) - box.Min(c => c.R);
                int gg = box.Max(c => c.G) - box.Min(c => c.G);
                int bb = box.Max(c => c.B) - box.Min(c => c.B);
                IOrderedEnumerable<Rgba32> ordered = rr >= gg && rr >= bb
                    ? box.OrderBy(c => c.R)
                    : gg >= bb ? box.OrderBy(c => c.G) : box.OrderBy(c => c.B);
                var sorted = ordered.ToList();
                int mid = sorted.Count / 2;
                boxes.RemoveAt(idx);
                boxes.Add(sorted.GetRange(0, mid));
                boxes.Add(sorted.GetRange(mid, sorted.Count - mid));
            }

            return boxes.Select(b =>
            {
                int r = (int)b.Average(c => c.R);
                int g = (int)b.Average(c => c.G);
                int bl = (int)b.Average(c => c.B);
                return new Rgba32((byte)r, (byte)g, (byte)bl, (byte)255);
            }).ToList();
        }
    }
}
