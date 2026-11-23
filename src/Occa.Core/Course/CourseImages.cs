using System.Buffers.Binary;
using System.IO.Compression;

namespace Occa.Core.Courses;

/// <summary>
/// Course object images (JPG/PNG) in the project package's <c>course-images/</c> folder. Port of <c>CourseImages</c>.
///
/// Limits: JPG/JPEG/PNG only, file ≤ 10 MB, side ≤ 8000 px, max. 10 images per project. Large images are downscaled
/// by the app (Win2D) to a longest side of 2000 px (enough for 30 cm on paper at 170 dpi) to keep the package and PDF light;
/// this portable part validates, inspects headers, stores files and builds the PDF image objects.
/// </summary>
public static class CourseImages
{
    public const string Folder = "course-images";
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxSourcePixels = 8000;
    public const int StoredMaxPixels = 2000;
    public const int MaxImagesPerProject = 10;

    public enum ImageErrorKind { UnsupportedType, TooLarge, TooManyPixels, Unreadable, TooMany }

    public sealed class ImageException : Exception
    {
        public ImageErrorKind Kind { get; }
        public ImageException(ImageErrorKind kind, string message) : base(message) => Kind = kind;

        public static ImageException UnsupportedType() => new(ImageErrorKind.UnsupportedType, "Unsupported format. Use JPG, JPEG, or PNG.");
        public static ImageException TooLarge(int bytes) => new(ImageErrorKind.TooLarge, $"File of {Bytes(bytes)} is too large (max. {Bytes(MaxFileBytes)}).");
        public static ImageException TooManyPixels(int w, int h) => new(ImageErrorKind.TooManyPixels, $"Image of {w}×{h} px is too large (max. {MaxSourcePixels} px per side).");
        public static ImageException Unreadable() => new(ImageErrorKind.Unreadable, "The image cannot be read (corrupt file?).");
        public static ImageException TooMany() => new(ImageErrorKind.TooMany, $"Maximum {MaxImagesPerProject} images per project.");

        private static string Bytes(long n) => n >= 1024 * 1024 ? (n / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB"
            : (n / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB";
    }

    public enum Format { Jpeg, Png }

    public static string Extension(this Format f) => f == Format.Jpeg ? "jpg" : "png";

    /// <summary>File type from the contents (not the extension).</summary>
    public static Format? FormatOf(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return Format.Jpeg;
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return Format.Png;
        return null;
    }

    public sealed record Prepared(byte[] Data, Format Format, int Width, int Height)
    {
        public double Aspect => (double)Height / Math.Max(Width, 1);
    }

    /// <summary>Pixel size from the JPEG SOF / PNG IHDR header; null when unreadable.</summary>
    public static (int Width, int Height, int Components)? Dimensions(byte[] data)
    {
        switch (FormatOf(data))
        {
            case Format.Png:
                if (data.Length < 24) return null;
                return ((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20)), data[25] switch { 0 or 4 => 1, 3 => 3, _ => 3 });
            case Format.Jpeg:
            {
                int i = 2;
                while (i + 9 < data.Length)
                {
                    if (data[i] != 0xFF) { i++; continue; }
                    byte marker = data[i + 1];
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 2));
                    bool sof = marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (sof) return (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 5)), data[i + 9]);
                    if (marker == 0xDA) break;
                    i += 2 + len;
                }
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>JPEG with an EXIF orientation other than 1 (needs re-encoding so it is not shown rotated).</summary>
    public static bool HasExifOrientation(byte[] data)
    {
        if (FormatOf(data) != Format.Jpeg) return false;
        int i = 2;
        while (i + 4 < data.Length && data[i] == 0xFF)
        {
            byte marker = data[i + 1];
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i + 2));
            if (marker == 0xE1 && i + 10 < data.Length && data.AsSpan(i + 4, 4).SequenceEqual("Exif"u8))
            {
                int tiff = i + 10;
                if (tiff + 8 > data.Length) return false;
                bool little = data[tiff] == 0x49;
                uint ifd = little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(tiff + 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(tiff + 4));
                int e = tiff + (int)ifd;
                if (e + 2 > data.Length) return false;
                int count = little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(e)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(e));
                for (int k = 0; k < count && e + 2 + k * 12 + 12 <= data.Length; k++)
                {
                    int o = e + 2 + k * 12;
                    int tag = little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o));
                    if (tag != 0x0112) continue;
                    int v = little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o + 8)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(o + 8));
                    return v != 1;
                }
                return false;
            }
            if (marker == 0xDA) break;
            i += 2 + len;
        }
        return false;
    }

    /// <summary>
    /// Validate; returns the image unchanged when it is small enough (format kept: JPG stays JPG, PNG stays PNG so logo
    /// transparency is preserved). Larger images or rotated JPEGs are passed to <paramref name="downscale"/> (the app's
    /// Win2D resampler to <see cref="StoredMaxPixels"/>); without one they are rejected as unreadable.
    /// </summary>
    public static Prepared Prepare(byte[] data, Func<byte[], Prepared>? downscale = null)
    {
        var format = FormatOf(data) ?? throw ImageException.UnsupportedType();
        if (data.Length > MaxFileBytes) throw ImageException.TooLarge(data.Length);
        var dim = Dimensions(data) ?? throw ImageException.Unreadable();
        if (dim.Width <= 0 || dim.Height <= 0) throw ImageException.Unreadable();
        if (dim.Width > MaxSourcePixels || dim.Height > MaxSourcePixels) throw ImageException.TooManyPixels(dim.Width, dim.Height);
        if (Math.Max(dim.Width, dim.Height) <= StoredMaxPixels && !HasExifOrientation(data)) return new Prepared(data, format, dim.Width, dim.Height);
        if (downscale is null) throw ImageException.Unreadable();
        return downscale(data);
    }

    public static string PathOf(string name, string package) => Path.Combine(package, Folder, name);

    /// <summary>Store the prepared image; return the file name.</summary>
    public static string Store(Prepared p, Guid id, string package)
    {
        var dir = Path.Combine(package, Folder);
        Directory.CreateDirectory(dir);
        var name = $"{id.ToString().ToUpperInvariant()}.{p.Format.Extension()}";
        Project.ProjectPackage.WriteAtomic(Path.Combine(dir, name), p.Data);
        return name;
    }

    /// <summary>Name comes from courses.json: reject paths outside the image folder.</summary>
    public static bool IsValidName(string name) => name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..");

    public static byte[]? Load(string name, string package)
    {
        if (!IsValidName(name)) return null;
        var path = PathOf(name, package);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Delete files not used by any object (called on save).</summary>
    public static void RemoveUnused(ISet<string> keeping, string package)
    {
        var dir = Path.Combine(package, Folder);
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir))
            if (!keeping.Contains(Path.GetFileName(f))) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // MARK: PDF

    /// <summary>PDF image object: RGB/gray JPG embedded directly (DCTDecode); otherwise RGB pixels + alpha (SMask), FlateDecode.</summary>
    public sealed record PdfImage(string Dictionary, byte[] Stream, (string Dictionary, byte[] Stream)? Mask);

    public static PdfImage? PdfImageOf(byte[] data)
    {
        var dim = Dimensions(data);
        if (dim is null) return null;
        var (w, h, comps) = dim.Value;
        if (FormatOf(data) == Format.Jpeg)
        {
            if (!HasExifOrientation(data) && comps is 1 or 3)
            {
                var cs = comps == 3 ? "/DeviceRGB" : "/DeviceGray";
                return new PdfImage($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace {cs} /BitsPerComponent 8 /Filter /DCTDecode", data, null);
            }
            return null;   // CMYK or rotated JPEG: the app re-encodes before export
        }
        var rgba = PngDecoder.Decode(data);
        if (rgba is null) return null;
        var rgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        bool hasAlpha = false;
        for (int i = 0; i < w * h; i++)
        {
            byte a = rgba[i * 4 + 3];
            alpha[i] = a;
            if (a < 255) hasAlpha = true;
            rgb[i * 3] = rgba[i * 4];
            rgb[i * 3 + 1] = rgba[i * 4 + 1];
            rgb[i * 3 + 2] = rgba[i * 4 + 2];
        }
        var pixels = Zlib(rgb);
        (string, byte[])? mask = hasAlpha ? ($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", Zlib(alpha)) : null;
        return new PdfImage($"/Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", pixels, mask);
    }

    /// <summary>zlib stream (header + deflate + Adler-32) for FlateDecode.</summary>
    public static byte[] Zlib(byte[] input)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(input, 0, input.Length);
        return ms.ToArray();
    }
}

/// <summary>Minimal PNG decoder (8/16-bit, colour types 0/2/3/4/6, non-interlaced) → straight RGBA bytes.</summary>
public static class PngDecoder
{
    public static byte[]? Decode(byte[] data)
    {
        if (CourseImages.FormatOf(data) != CourseImages.Format.Png) return null;
        int pos = 8;
        int w = 0, h = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int body = pos + 8;
            if (len < 0 || body + len > data.Length) return null;
            switch (type)
            {
                case "IHDR":
                    w = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body));
                    h = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4));
                    depth = data[body + 8]; colorType = data[body + 9]; interlace = data[body + 12];
                    break;
                case "PLTE": palette = data.AsSpan(body, len).ToArray(); break;
                case "tRNS": trns = data.AsSpan(body, len).ToArray(); break;
                case "IDAT": idat.Write(data, body, len); break;
            }
            if (type == "IEND") break;
            pos = body + len + 4;
        }
        if (w <= 0 || h <= 0 || interlace != 0 || depth is not (8 or 16) || (long)w * h > 100_000_000) return null;
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0) return null;
        int bps = depth / 8;
        int stride = w * channels * bps;
        byte[] raw;
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            raw = new byte[(stride + 1) * h];
            int read = 0;
            while (read < raw.Length)
            {
                int n = z.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < raw.Length) return null;
        }
        catch (InvalidDataException) { return null; }

        // Unfilter.
        var img = new byte[stride * h];
        int bpp = Math.Max(1, channels * bps);
        for (int y = 0; y < h; y++)
        {
            int filter = raw[y * (stride + 1)];
            int src = y * (stride + 1) + 1, dst = y * stride, prev = (y - 1) * stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? img[dst + x - bpp] : 0;
                int b = y > 0 ? img[prev + x] : 0;
                int c = y > 0 && x >= bpp ? img[prev + x - bpp] : 0;
                int v = raw[src + x];
                img[dst + x] = (byte)(filter switch
                {
                    0 => v,
                    1 => v + a,
                    2 => v + b,
                    3 => v + (a + b) / 2,
                    4 => v + Paeth(a, b, c),
                    _ => v,
                });
            }
        }

        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * channels * bps;
            byte S(int k) => img[o + k * bps];   // 16-bit: the high byte
            byte r, g, b, a = 255;
            switch (colorType)
            {
                case 0:
                    r = g = b = S(0);
                    if (trns is { Length: >= 2 } && depth == 8 && trns[1] == S(0)) a = 0;
                    break;
                case 2:
                    r = S(0); g = S(1); b = S(2);
                    if (trns is { Length: >= 6 } && depth == 8 && trns[1] == r && trns[3] == g && trns[5] == b) a = 0;
                    break;
                case 3:
                {
                    int idx = S(0);
                    if (palette is null || idx * 3 + 2 >= palette.Length) return null;
                    r = palette[idx * 3]; g = palette[idx * 3 + 1]; b = palette[idx * 3 + 2];
                    if (trns is not null && idx < trns.Length) a = trns[idx];
                    break;
                }
                case 4: r = g = b = S(0); a = S(1); break;
                default: r = S(0); g = S(1); b = S(2); a = S(3); break;
            }
            rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
        }
        return rgba;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
