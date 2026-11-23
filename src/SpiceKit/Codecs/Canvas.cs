using SpiceKit.Wire;

namespace SpiceKit.Codecs;

/// <summary>Maschera a 1 bit posizionata rispetto al riquadro di disegno.</summary>
public sealed record DrawMask(int Width, int Height, bool[] Bits, bool Inverted, SpicePoint Origin)
{
    internal bool Allows(int boxX, int boxY)
    {
        long mx = (long)boxX + Origin.X, my = (long)boxY + Origin.Y;
        var bit = mx >= 0 && my >= 0 && mx < Width && my < Height && Bits[(my * Width) + mx];
        return bit != Inverted;
    }
}

/// <summary>Sorgente di un riempimento: colore xRGB oppure motivo ripetuto.</summary>
public abstract record FillSource
{
    public sealed record Color(uint Value) : FillSource;

    public sealed record Pattern(PixelBuffer Image, SpicePoint Origin) : FillSource;
}

/// <summary>
/// Operazioni raster software su una superficie, come i comandi di disegno SPICE (porting di
/// <c>SpiceCodecs/Canvas.swift</c>). Ogni operazione si limita alla superficie, al riquadro del
/// comando e ai suoi rettangoli di ritaglio, e restituisce il rettangolo modificato.
/// </summary>
public sealed partial class PixelBuffer
{
    /// <summary>Rettangoli da dipingere: riquadro ∩ ritaglio ∩ superficie.</summary>
    public List<SpiceRect> PaintRects(SpiceRect box, SpiceClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var boxed = box.Intersect(Bounds);
        var result = new List<SpiceRect>();
        if (boxed.IsEmpty) return result;
        if (clip.Rects is null)
        {
            result.Add(boxed);
            return result;
        }
        foreach (var rect in clip.Rects)
        {
            var r = rect.Intersect(boxed);
            if (!r.IsEmpty) result.Add(r);
        }
        return result;
    }

    /// <summary>Applica un descrittore ROP all'RGB; l'alfa di destinazione resta, salvo la copia semplice.</summary>
    internal static uint ApplyRop(Ropd rop, uint source, uint destination)
    {
        if (rop.HasFlag(Ropd.OpBlackness)) return destination & 0xFF00_0000;
        if (rop.HasFlag(Ropd.OpWhiteness)) return destination | 0x00FF_FFFF;
        if (rop.HasFlag(Ropd.OpInvers)) return destination ^ 0x00FF_FFFF;
        var src = source;
        var dst = destination;
        if ((rop & (Ropd.InversSource | Ropd.InversBrush)) != 0) src ^= 0x00FF_FFFF;
        if (rop.HasFlag(Ropd.InversDest)) dst ^= 0x00FF_FFFF;
        uint result;
        if (rop.HasFlag(Ropd.OpOr)) result = (dst & 0xFF00_0000) | ((src | dst) & 0x00FF_FFFF);
        else if (rop.HasFlag(Ropd.OpAnd)) result = (dst & 0xFF00_0000) | (src & dst & 0x00FF_FFFF);
        else if (rop.HasFlag(Ropd.OpXor)) result = (dst & 0xFF00_0000) | ((src ^ dst) & 0x00FF_FFFF);
        else result = src; // OP_PUT (e il caso predefinito)
        if (rop.HasFlag(Ropd.InversResult)) result ^= 0x00FF_FFFF;
        return result;
    }

    /// <summary>L'area non è vuota e sta dentro la sorgente (rifiuta rettangoli ostili prima dei calcoli di scala).</summary>
    internal static bool IsValid(SpiceRect area, PixelBuffer source) =>
        area.Left >= 0 && area.Top >= 0 && area.Right > area.Left && area.Bottom > area.Top
        && area.Right <= source.Width && area.Bottom <= source.Height;

    internal static bool IsPlainCopy(Ropd rop) => rop is Ropd.OpPut or Ropd.None;

    /// <summary><c>DRAW_FILL</c>.</summary>
    public SpiceRect Fill(SpiceRect box, SpiceClip clip, FillSource source, Ropd rop = Ropd.OpPut, DrawMask? mask = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var dirty = SpiceRect.Empty;
        var plain = IsPlainCopy(rop);
        foreach (var rect in PaintRects(box, clip))
        {
            dirty = dirty.Union(rect);
            for (var y = rect.Top; y < rect.Bottom; y++)
            {
                var row = Pixels.AsSpan(y * Width, Width);
                if (source is FillSource.Color solid && plain && mask is null)
                {
                    row[rect.Left..rect.Right].Fill(0xFF00_0000 | solid.Value);
                    continue;
                }
                for (var x = rect.Left; x < rect.Right; x++)
                {
                    if (mask is not null && !mask.Allows(x - box.Left, y - box.Top)) continue;
                    uint color;
                    if (source is FillSource.Color c)
                    {
                        color = 0xFF00_0000 | c.Value;
                    }
                    else
                    {
                        var pattern = (FillSource.Pattern)source;
                        var image = pattern.Image;
                        if (image.Width == 0 || image.Height == 0) continue;
                        var px = (int)((((long)x - pattern.Origin.X) % image.Width + image.Width) % image.Width);
                        var py = (int)((((long)y - pattern.Origin.Y) % image.Height + image.Height) % image.Height);
                        color = image.Pixels[(py * image.Width) + px];
                    }
                    row[x] = plain ? color : ApplyRop(rop, color, row[x]);
                }
            }
        }
        return dirty;
    }

    /// <summary><c>DRAW_BLACKNESS</c> / <c>WHITENESS</c> / <c>INVERS</c>.</summary>
    public SpiceRect ApplyUnary(SpiceRect box, SpiceClip clip, Ropd rop, DrawMask? mask = null)
    {
        var dirty = SpiceRect.Empty;
        foreach (var rect in PaintRects(box, clip))
        {
            dirty = dirty.Union(rect);
            for (var y = rect.Top; y < rect.Bottom; y++)
            {
                for (var x = rect.Left; x < rect.Right; x++)
                {
                    if (mask is not null && !mask.Allows(x - box.Left, y - box.Top)) continue;
                    var i = (y * Width) + x;
                    Pixels[i] = ApplyRop(rop, 0, Pixels[i]);
                }
            }
        }
        return dirty;
    }

    /// <summary>
    /// <c>DRAW_COPY</c> / <c>DRAW_BLEND</c> / <c>DRAW_OPAQUE</c> (senza pennello): copia
    /// <paramref name="sourceArea"/> di <paramref name="source"/> in <paramref name="box"/>, scalando se le misure differiscono.
    /// </summary>
    public SpiceRect Copy(PixelBuffer source, SpiceRect sourceArea, SpiceRect box, SpiceClip clip,
        Ropd rop = Ropd.OpPut, ScaleMode scaleMode = ScaleMode.Nearest, DrawMask? mask = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsValid(sourceArea, source) || box.Width <= 0 || box.Height <= 0) return SpiceRect.Empty;
        var scaled = sourceArea.Width != box.Width || sourceArea.Height != box.Height;
        var plain = IsPlainCopy(rop);
        var dirty = SpiceRect.Empty;
        foreach (var rect in PaintRects(box, clip))
        {
            dirty = dirty.Union(rect);
            var sx0 = (long)sourceArea.Left + rect.Left - box.Left;
            var sy0 = (long)sourceArea.Top + rect.Top - box.Top;
            // Copia di righe intere: senza scala né maschera (i decoder danno alfa opaco alle
            // sorgenti senza canale alfa, quindi nessuna correzione per pixel).
            if (!scaled && plain && mask is null && sx0 >= 0 && sy0 >= 0
                && sx0 + rect.Width <= source.Width && sy0 + rect.Height <= source.Height)
            {
                for (var row = 0; row < rect.Height; row++)
                {
                    source.Pixels.AsSpan((int)(((sy0 + row) * source.Width) + sx0), rect.Width)
                        .CopyTo(Pixels.AsSpan(((rect.Top + row) * Width) + rect.Left));
                }
                continue;
            }
            for (var y = rect.Top; y < rect.Bottom; y++)
            {
                var by = y - box.Top;
                for (var x = rect.Left; x < rect.Right; x++)
                {
                    var bx = x - box.Left;
                    if (mask is not null && !mask.Allows(bx, by)) continue;
                    uint color;
                    if (!scaled)
                    {
                        long sx = (long)sourceArea.Left + bx, sy = (long)sourceArea.Top + by;
                        if (sx < 0 || sy < 0 || sx >= source.Width || sy >= source.Height) continue;
                        color = source.Pixels[(sy * source.Width) + sx];
                    }
                    else if (scaleMode == ScaleMode.Interpolate)
                    {
                        color = SampleBilinear(source, sourceArea, box, bx, by);
                    }
                    else
                    {
                        var sx = sourceArea.Left + ((long)bx * sourceArea.Width / box.Width);
                        var sy = sourceArea.Top + ((long)by * sourceArea.Height / box.Height);
                        if (sx < 0 || sy < 0 || sx >= source.Width || sy >= source.Height) continue;
                        color = source.Pixels[(sy * source.Width) + sx];
                    }
                    if (!source.HasAlpha) color |= 0xFF00_0000;
                    var i = (y * Width) + x;
                    Pixels[i] = plain ? color : ApplyRop(rop, color, Pixels[i]);
                }
            }
        }
        return dirty;
    }

    private static uint SampleBilinear(PixelBuffer source, SpiceRect area, SpiceRect box, int bx, int by)
    {
        // Centri dei pixel; pesi in virgola fissa a 8 bit.
        var fx = ((bx + 0.5) * area.Width / box.Width) - 0.5 + area.Left;
        var fy = ((by + 0.5) * area.Height / box.Height) - 0.5 + area.Top;
        var x0 = Math.Clamp((int)Math.Floor(fx), 0, source.Width - 1);
        var y0 = Math.Clamp((int)Math.Floor(fy), 0, source.Height - 1);
        int x1 = Math.Min(source.Width - 1, x0 + 1), y1 = Math.Min(source.Height - 1, y0 + 1);
        var wx = (uint)Math.Clamp((int)((fx - x0) * 256), 0, 256);
        var wy = (uint)Math.Clamp((int)((fy - y0) * 256), 0, 256);
        var src = source.Pixels;
        var w = source.Width;
        uint p00 = src[(y0 * w) + x0], p10 = src[(y0 * w) + x1], p01 = src[(y1 * w) + x0], p11 = src[(y1 * w) + x1];
        uint output = 0;
        for (var shift = 0; shift < 32; shift += 8)
        {
            uint c00 = (p00 >> shift) & 0xFF, c10 = (p10 >> shift) & 0xFF, c01 = (p01 >> shift) & 0xFF, c11 = (p11 >> shift) & 0xFF;
            var top = (c00 * (256 - wx)) + (c10 * wx);
            var bottom = (c01 * (256 - wx)) + (c11 * wx);
            var value = ((top * (256 - wy)) + (bottom * wy)) >> 16;
            output |= Math.Min(value, 255u) << shift;
        }
        return output;
    }

    /// <summary>
    /// <c>COPY_BITS</c>: copia da <paramref name="sourcePosition"/> a <paramref name="box"/> nella stessa superficie.
    /// L'area sorgente viene prima fotografata: spostamenti sovrapposti (scorrimento) e più
    /// rettangoli di ritaglio non leggono mai pixel già sovrascritti.
    /// </summary>
    public SpiceRect CopyBits(SpicePoint sourcePosition, SpiceRect box, SpiceClip clip)
    {
        var dx = (long)box.Left - sourcePosition.X;
        var dy = (long)box.Top - sourcePosition.Y;
        var rects = PaintRects(box, clip);
        if (rects.Count == 0) return SpiceRect.Empty;
        var union = rects.Aggregate(SpiceRect.Empty, (a, b) => a.Union(b));
        var sourceUnion = union.Offset(-dx, -dy).Intersect(Bounds);
        if (sourceUnion.IsEmpty) return SpiceRect.Empty;
        var snapshot = Cropped(sourceUnion.Left, sourceUnion.Top, sourceUnion.Width, sourceUnion.Height);
        var dirty = SpiceRect.Empty;
        var reachable = sourceUnion.Offset(dx, dy);
        foreach (var rect in rects)
        {
            // Si copia solo la parte la cui sorgente sta nella superficie.
            var target = rect.Intersect(reachable);
            if (target.IsEmpty) continue;
            dirty = dirty.Union(target);
            for (var y = target.Top; y < target.Bottom; y++)
            {
                var sy = (int)(y - dy - sourceUnion.Top);
                var sx = (int)(target.Left - dx - sourceUnion.Left);
                snapshot.Pixels.AsSpan((sy * snapshot.Width) + sx, target.Width)
                    .CopyTo(Pixels.AsSpan((y * Width) + target.Left));
            }
        }
        return dirty;
    }

    /// <summary><c>DRAW_ALPHA_BLEND</c>: "source over" premoltiplicato con un alfa globale.</summary>
    public SpiceRect AlphaBlend(PixelBuffer source, SpiceRect sourceArea, SpiceRect box, SpiceClip clip, byte alpha, bool sourceHasAlpha)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsValid(sourceArea, source) || box.Width <= 0 || box.Height <= 0) return SpiceRect.Empty;
        var dirty = SpiceRect.Empty;
        foreach (var rect in PaintRects(box, clip))
        {
            dirty = dirty.Union(rect);
            for (var y = rect.Top; y < rect.Bottom; y++)
            {
                var sy = sourceArea.Top + ((long)(y - box.Top) * sourceArea.Height / box.Height);
                if (sy < 0 || sy >= source.Height) continue;
                for (var x = rect.Left; x < rect.Right; x++)
                {
                    var sx = sourceArea.Left + ((long)(x - box.Left) * sourceArea.Width / box.Width);
                    if (sx < 0 || sx >= source.Width) continue;
                    var s = source.Pixels[(sy * source.Width) + sx];
                    if (!sourceHasAlpha) s |= 0xFF00_0000;
                    var i = (y * Width) + x;
                    Pixels[i] = BlendOver(s, Pixels[i], alpha);
                }
            }
        }
        return dirty;
    }

    /// <summary>OVER premoltiplicato con un moltiplicatore alfa globale.</summary>
    internal static uint BlendOver(uint source, uint destination, uint globalAlpha)
    {
        static uint Scale(uint c, uint a) => ((c * a) + 127) / 255;
        var sa = Scale(source >> 24, globalAlpha);
        var inverse = 255 - sa;
        uint output = 0;
        for (var shift = 0; shift < 24; shift += 8)
        {
            var s = Scale((source >> shift) & 0xFF, globalAlpha);
            var d = (destination >> shift) & 0xFF;
            output |= Math.Min(255u, s + Scale(d, inverse)) << shift;
        }
        return output | (Math.Min(255u, sa + Scale(destination >> 24, inverse)) << 24);
    }

    /// <summary><c>DRAW_TRANSPARENT</c>: copia i pixel il cui RGB differisce da <paramref name="colorKey"/>.</summary>
    public SpiceRect TransparentCopy(PixelBuffer source, SpiceRect sourceArea, SpiceRect box, SpiceClip clip, uint colorKey)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsValid(sourceArea, source) || box.Width <= 0 || box.Height <= 0) return SpiceRect.Empty;
        var dirty = SpiceRect.Empty;
        var key = colorKey & 0x00FF_FFFF;
        foreach (var rect in PaintRects(box, clip))
        {
            dirty = dirty.Union(rect);
            for (var y = rect.Top; y < rect.Bottom; y++)
            {
                var sy = sourceArea.Top + ((long)(y - box.Top) * sourceArea.Height / box.Height);
                if (sy < 0 || sy >= source.Height) continue;
                for (var x = rect.Left; x < rect.Right; x++)
                {
                    var sx = sourceArea.Left + ((long)(x - box.Left) * sourceArea.Width / box.Width);
                    if (sx < 0 || sx >= source.Width) continue;
                    var s = source.Pixels[(sy * source.Width) + sx];
                    if ((s & 0x00FF_FFFF) != key) Pixels[(y * Width) + x] = s | 0xFF00_0000;
                }
            }
        }
        return dirty;
    }
}
