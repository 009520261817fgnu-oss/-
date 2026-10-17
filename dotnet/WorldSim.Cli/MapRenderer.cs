using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using WorldSim.Core;
using WorldSim.Core.World;

/// <summary>Top-down debug map (north up) written as PNG without external libraries.</summary>
internal static class MapRenderer
{
    public static void Save(WorldState w, string path, int pixelsPerCell)
    {
        Terrain t = w.Terrain;
        int width = t.Width * pixelsPerCell, height = t.Depth * pixelsPerCell;
        var rgb = new byte[width * height * 3];

        for (int z = 0; z < t.Depth; z++)
        for (int x = 0; x < t.Width; x++)
        {
            var c = new GridPos(x, z);
            int color = GroundColor(t, c);
            // Hillshade from the north-west.
            float slope = (t.GetVertexHeight(x, z + 1) - t.GetVertexHeight(x + 1, z)) * 0.04f;
            color = Shade(color, 1f + Math.Max(-0.35f, Math.Min(0.35f, slope)));
            if (w.HasRoad(c)) color = 0x505055;
            Building b = w.GetBuildingAt(c);
            if (b != null)
            {
                bool edge = x == b.Origin.X || z == b.Origin.Z || x == b.Footprint.MaxX - 1 || z == b.Footprint.MaxZ - 1;
                color = edge ? Shade(b.Def.Color, 0.75f) : b.Def.Color;
            }
            Fill(rgb, width, height, x * pixelsPerCell, z * pixelsPerCell, pixelsPerCell, color);

            byte veg = w.GetVegetation(c);
            if (veg > 0 && b == null && !w.HasRoad(c))
            {
                int size = Math.Max(1, pixelsPerCell * (40 + veg) / 400);
                int off = (pixelsPerCell - size) / 2;
                Fill(rgb, width, height, x * pixelsPerCell + off, z * pixelsPerCell + off, size, 0x2E5E2A);
            }
        }
        WritePng(path, width, height, rgb);
    }

    private static int GroundColor(Terrain t, GridPos c)
    {
        float h = (t.CellMinHeight(c) + t.CellMaxHeight(c)) * 0.5f;
        if (t.IsCellUnderwater(c))
        {
            float depth = Math.Min(1f, (t.SeaLevel - h) / 12f);
            return Lerp(0x4A8CC8, 0x1E4C8A, depth);
        }
        float a = h - t.SeaLevel;
        if (a < 1.5f) return 0xD8C890;
        if (a < 25f) return Lerp(0x7FB060, 0x5E8C48, a / 25f);
        if (a < 55f) return Lerp(0x8A8A6A, 0x8C7F70, (a - 25f) / 30f);
        if (a < 80f) return 0x9A9590;
        return 0xF2F2F5;
    }

    private static int Lerp(int a, int b, float t)
    {
        t = Math.Max(0, Math.Min(1, t));
        int r = (int)(((a >> 16) & 255) + ((((b >> 16) & 255) - ((a >> 16) & 255)) * t));
        int g = (int)(((a >> 8) & 255) + ((((b >> 8) & 255) - ((a >> 8) & 255)) * t));
        int bl = (int)((a & 255) + (((b & 255) - (a & 255)) * t));
        return (r << 16) | (g << 8) | bl;
    }

    private static int Shade(int c, float f)
    {
        int r = Math.Min(255, (int)(((c >> 16) & 255) * f));
        int g = Math.Min(255, (int)(((c >> 8) & 255) * f));
        int b = Math.Min(255, (int)((c & 255) * f));
        return (r << 16) | (g << 8) | b;
    }

    private static void Fill(byte[] rgb, int width, int height, int px, int pz, int size, int color)
    {
        for (int dz = 0; dz < size; dz++)
        for (int dx = 0; dx < size; dx++)
        {
            int row = height - 1 - (pz + dz); // north (high Z) at the top of the image
            int i = (row * width + px + dx) * 3;
            rgb[i] = (byte)(color >> 16);
            rgb[i + 1] = (byte)(color >> 8);
            rgb[i + 2] = (byte)color;
        }
    }

    private static void WritePng(string path, int width, int height, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, width);
        WriteBE(ihdr, 4, height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        Chunk(fs, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
        {
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * width * 3, width * 3);
            }
        }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4];
        WriteBE(c, 0, (int)crc);
        s.Write(c);
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }

    private static void WriteBE(byte[] buf, int offset, int v)
    {
        buf[offset] = (byte)(v >> 24);
        buf[offset + 1] = (byte)(v >> 16);
        buf[offset + 2] = (byte)(v >> 8);
        buf[offset + 3] = (byte)v;
    }
}
