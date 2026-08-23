using System;
using System.Collections.Generic;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelPair
{
    // 단일 레이어 모델 (픽셀 그리드 + 불투명도/가시성/잠금/메타데이터)
    public sealed class Layer
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Visible { get; set; } = true;
        public bool Locked { get; set; } = false;
        public float Opacity { get; set; } = 1.0f; // 0.0 ~ 1.0
        public Rgba32?[,] Pixels { get; set; }

        public Layer(string? name = null, int gridSize = 64, string? id = null)
        {
            Id = id ?? Guid.NewGuid().ToString("N")[..8];
            Name = string.IsNullOrWhiteSpace(name) ? "Layer 1" : name;
            Pixels = new Rgba32?[gridSize, gridSize];
        }

        public Layer Clone(string? newId = null, string? newName = null)
        {
            int w = Pixels.GetLength(0);
            int h = Pixels.GetLength(1);
            var cloned = new Layer(newName ?? Name, w, newId ?? Id)
            {
                Visible = Visible,
                Locked = Locked,
                Opacity = Opacity,
                Pixels = new Rgba32?[w, h]
            };
            Array.Copy(Pixels, cloned.Pixels, Pixels.Length);
            return cloned;
        }

        public int CountOccupied()
        {
            int count = 0;
            int w = Pixels.GetLength(0);
            int h = Pixels.GetLength(1);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (Pixels[x, y] != null) count++;
                }
            }
            return count;
        }

        public void Clear()
        {
            Array.Clear(Pixels, 0, Pixels.Length);
        }
    }
}
