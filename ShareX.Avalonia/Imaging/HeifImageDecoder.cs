#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;
using System.Diagnostics;

namespace ShareX.AvaloniaUI.Imaging;

/// <summary>
/// HEIC/HEIF (iPhone photo) support for ShareX. Skia and Avalonia cannot decode HEIF,
/// so these helpers decode it with libheif and fall back to the normal decoders for other formats.
/// </summary>
public static class HeifImageDecoder
{
    /// <summary>File extensions without dot.</summary>
    public static readonly string[] FileExtensions = ["heic", "heif", "hif"];

    private const int HeaderLength = 64;

    /// <summary>"All images" file picker type that also lists HEIC/HEIF files.</summary>
    public static FilePickerFileType ImageAll { get; } = new(FilePickerFileTypes.ImageAll.Name)
    {
        Patterns = [.. FilePickerFileTypes.ImageAll.Patterns ?? [], .. FileExtensions.Select(x => "*." + x)],
        AppleUniformTypeIdentifiers = ["public.image"],
        MimeTypes = ["image/*"]
    };

    public static bool IsAvailable => LibHeifNative.IsAvailable;

    public static bool HasHeifExtension(string? filePath)
    {
        string extension = Path.GetExtension(filePath ?? "").TrimStart('.');
        return FileExtensions.Any(x => x.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHeif(ReadOnlySpan<byte> data) => LibHeifNative.IsHeif(data);

    /// <summary>Checks the file signature. The stream position is restored.</summary>
    public static bool IsHeif(Stream stream)
    {
        if (stream == null || !stream.CanRead || !stream.CanSeek) return false;

        long position = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[HeaderLength];
            int length = stream.ReadAtLeast(header, HeaderLength, false);
            return LibHeifNative.IsHeif(header[..length]);
        }
        finally
        {
            stream.Position = position;
        }
    }

    /// <summary>Checks the file signature, so iPhone photos are detected even with a wrong extension.</summary>
    public static bool IsHeifFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

        try
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return IsHeif(stream);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return false;
        }
    }

    /// <summary>Decodes a HEIF image into a BGRA premultiplied sRGB bitmap. Throws on failure.</summary>
    public static SKBitmap Decode(ReadOnlySpan<byte> data)
    {
        SKBitmap? decoded = null;
        SKColorSpace? colorSpace = null;

        try
        {
            LibHeifNative.Decode(data, (in HeifImageInfo info, out int rowBytes) =>
            {
                // iPhone photos are Display P3; keep the embedded profile so Skia can convert to sRGB below.
                colorSpace = info.IccProfile != null ? SKColorSpace.CreateIcc(info.IccProfile) : null;
                decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888,
                    info.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque, colorSpace));
                rowBytes = decoded.RowBytes;
                return decoded.GetPixels();
            });

            if (decoded == null) throw new InvalidDataException("The HEIF image could not be decoded.");

            // ShareX works with BGRA premultiplied sRGB bitmaps (see SkiaImageHelpers.CreateBitmap).
            SKImageInfo targetInfo = new(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            SKBitmap result = new(targetInfo);

            using SKPixmap source = decoded.PeekPixels();
            SKImageInfo conversionInfo = colorSpace != null ? targetInfo.WithColorSpace(SKColorSpace.CreateSrgb()) : targetInfo;

            if (source == null || !source.ReadPixels(conversionInfo, result.GetPixels(), result.RowBytes))
            {
                result.Dispose();
                throw new InvalidDataException("Unable to convert the HEIF image pixels.");
            }

            return result;
        }
        finally
        {
            decoded?.Dispose();
            colorSpace?.Dispose();
        }
    }

    /// <summary>Decodes a HEIF image from the current position of the stream. Throws on failure.</summary>
    public static SKBitmap Decode(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out ArraySegment<byte> buffer))
        {
            return Decode(buffer.AsSpan((int)memoryStream.Position));
        }

        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Decode(copy.GetBuffer().AsSpan(0, (int)copy.Length));
    }

    /// <summary>Decodes a HEIF file. Throws on failure.</summary>
    public static SKBitmap DecodeFile(string filePath) => Decode(File.ReadAllBytes(filePath));

    /// <summary>Like <see cref="SKBitmap.Decode(string)"/> but also decodes HEIF. Returns null on failure.</summary>
    public static SKBitmap? DecodeAnyFile(string filePath)
    {
        if (IsHeifFile(filePath))
        {
            try
            {
                return DecodeFile(filePath);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return null;
            }
        }

        return SKBitmap.Decode(filePath);
    }

    /// <summary>Like <see cref="SKBitmap.Decode(Stream)"/> but also decodes HEIF. Returns null on failure.</summary>
    public static SKBitmap? DecodeAny(Stream stream)
    {
        if (IsHeif(stream))
        {
            try
            {
                return Decode(stream);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return null;
            }
        }

        return SKBitmap.Decode(stream);
    }

    /// <summary>Same as <c>new Bitmap(filePath)</c> but also opens HEIF files.</summary>
    public static Bitmap LoadBitmap(string filePath)
    {
        if (IsHeifFile(filePath))
        {
            using SKBitmap bitmap = DecodeFile(filePath);
            return BitmapConversionHelpers.ToAvaloniBitmap(bitmap);
        }

        return new Bitmap(filePath);
    }

    /// <summary>Same as <c>new Bitmap(stream)</c> but also opens HEIF data.</summary>
    public static Bitmap LoadBitmap(Stream stream)
    {
        if (IsHeif(stream))
        {
            using SKBitmap bitmap = Decode(stream);
            return BitmapConversionHelpers.ToAvaloniBitmap(bitmap);
        }

        return new Bitmap(stream);
    }

    /// <summary>
    /// Returns a preview no larger than <paramref name="maximumSize"/> when the stream contains HEIF data,
    /// otherwise null so the caller can use its normal decoder. The stream position is kept for that case.
    /// </summary>
    public static Bitmap? TryLoadPreview(Stream stream, PixelSize maximumSize)
    {
        if (!IsHeif(stream)) return null;

        using SKBitmap bitmap = Decode(stream);
        return BitmapConversionHelpers.CreatePreview(bitmap, maximumSize);
    }
}
