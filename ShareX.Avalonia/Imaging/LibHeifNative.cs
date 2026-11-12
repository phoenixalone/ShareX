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

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ShareX.AvaloniaUI.Imaging;

/// <summary>Size and color information of a decoded HEIF image, passed to the pixel buffer allocator.</summary>
public readonly record struct HeifImageInfo(int Width, int Height, bool HasAlpha, byte[]? IccProfile);

/// <summary>Returns a writable buffer for 8-bit RGBA pixels (unpremultiplied) and its row stride in bytes.</summary>
public delegate IntPtr HeifPixelBufferAllocator(in HeifImageInfo info, out int rowBytes);

/// <summary>
/// Minimal binding to libheif (https://github.com/strukturag/libheif), loaded at runtime.
/// The native library is optional: when it is missing, <see cref="IsAvailable"/> is false and
/// ShareX behaves as before.
/// </summary>
public static unsafe class LibHeifNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HeifError
    {
        public int Code;
        public int Subcode;
        public IntPtr Message;
    }

    // Values from libheif/heif.h. They are part of the stable C API.
    private const int heif_colorspace_RGB = 1;
    private const int heif_chroma_interleaved_RGBA = 11;
    private const int heif_channel_interleaved = 10;
    private const int heif_color_profile_type_rICC = ('r' << 24) | ('I' << 16) | ('C' << 8) | 'C';
    private const int heif_color_profile_type_prof = ('p' << 24) | ('r' << 16) | ('o' << 8) | 'f';

    private static readonly string[] LibraryNames = OperatingSystem.IsWindows()
        ? ["heif.dll", "libheif.dll"]
        : OperatingSystem.IsMacOS()
            ? ["libheif.1.dylib", "libheif.dylib"]
            : ["libheif.so.1", "libheif.so"];

    // Brands of HEIF files that contain HEVC coded images (iPhone photos use "heic").
    private static readonly HashSet<string> HevcBrands = ["heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs"];
    private static readonly HashSet<string> GenericHeifBrands = ["mif1", "msf1", "mif2"];
    private static readonly HashSet<string> AvifBrands = ["avif", "avis"];

    private static readonly object loadLock = new();
    private static bool loadAttempted;
    private static bool isAvailable;

    private static delegate* unmanaged<IntPtr, HeifError> heif_init;
    private static delegate* unmanaged<IntPtr> heif_context_alloc;
    private static delegate* unmanaged<IntPtr, void> heif_context_free;
    private static delegate* unmanaged<IntPtr, void*, nuint, IntPtr, HeifError> heif_context_read_from_memory_without_copy;
    private static delegate* unmanaged<IntPtr, IntPtr*, HeifError> heif_context_get_primary_image_handle;
    private static delegate* unmanaged<IntPtr, void> heif_image_handle_release;
    private static delegate* unmanaged<IntPtr, int> heif_image_handle_has_alpha_channel;
    private static delegate* unmanaged<IntPtr, int> heif_image_handle_get_color_profile_type;
    private static delegate* unmanaged<IntPtr, nuint> heif_image_handle_get_raw_color_profile_size;
    private static delegate* unmanaged<IntPtr, void*, HeifError> heif_image_handle_get_raw_color_profile;
    private static delegate* unmanaged<IntPtr, IntPtr*, int, int, IntPtr, HeifError> heif_decode_image;
    private static delegate* unmanaged<IntPtr, int, int> heif_image_get_width;
    private static delegate* unmanaged<IntPtr, int, int> heif_image_get_height;
    private static delegate* unmanaged<IntPtr, int, nuint*, byte*> heif_image_get_plane_readonly2; // libheif >= 1.20
    private static delegate* unmanaged<IntPtr, int, int*, byte*> heif_image_get_plane_readonly; // older libheif
    private static delegate* unmanaged<IntPtr, void> heif_image_release;
    private static delegate* unmanaged<sbyte*> heif_get_version;

    /// <summary>Path of the loaded native library, or the reason it could not be loaded.</summary>
    public static string? LoadInfo { get; private set; }

    /// <summary>True when libheif was found and all required functions were resolved.</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return isAvailable;
        }
    }

    public static string? Version
    {
        get
        {
            if (!IsAvailable || heif_get_version == null) return null;
            return Marshal.PtrToStringAnsi((IntPtr)heif_get_version());
        }
    }

    /// <summary>Checks the ISO BMFF "ftyp" box at the start of a file for HEIF brands (HEIC, HEIF; not AVIF).</summary>
    public static bool IsHeif(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || header[4] != 'f' || header[5] != 't' || header[6] != 'y' || header[7] != 'p')
        {
            return false;
        }

        int boxSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header), (uint)header.Length);
        string majorBrand = ReadBrand(header, 8);
        List<string> brands = [majorBrand];

        // Compatible brands follow the 4-byte minor version.
        for (int offset = 16; offset + 4 <= boxSize; offset += 4)
        {
            brands.Add(ReadBrand(header, offset));
        }

        if (brands.Any(HevcBrands.Contains))
        {
            return true;
        }

        return GenericHeifBrands.Contains(majorBrand) && !brands.Any(AvifBrands.Contains);
    }

    private static string ReadBrand(ReadOnlySpan<byte> data, int offset)
    {
        Span<char> chars = stackalloc char[4];
        for (int i = 0; i < 4; i++) chars[i] = (char)data[offset + i];
        return new string(chars);
    }

    /// <summary>
    /// Decodes the primary image of a HEIF file into an RGBA buffer obtained from <paramref name="allocate"/>.
    /// Rotation and mirroring stored in the file (irot/imir) are applied by libheif.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> data, HeifPixelBufferAllocator allocate)
    {
        ArgumentNullException.ThrowIfNull(allocate);

        if (!IsAvailable)
        {
            throw new InvalidDataException("HEIC/HEIF decoder (libheif) is not available: " + LoadInfo);
        }

        IntPtr context = heif_context_alloc();
        if (context == IntPtr.Zero) throw new OutOfMemoryException("heif_context_alloc failed.");

        IntPtr handle = IntPtr.Zero;
        IntPtr image = IntPtr.Zero;

        try
        {
            fixed (byte* dataPointer = data)
            {
                // The context references the data without copying it, so everything happens inside this block.
                Check(heif_context_read_from_memory_without_copy(context, dataPointer, (nuint)data.Length, IntPtr.Zero));
                Check(heif_context_get_primary_image_handle(context, &handle));

                bool hasAlpha = heif_image_handle_has_alpha_channel(handle) != 0;
                byte[]? iccProfile = ReadIccProfile(handle);

                Check(heif_decode_image(handle, &image, heif_colorspace_RGB, heif_chroma_interleaved_RGBA, IntPtr.Zero));

                int width = heif_image_get_width(image, heif_channel_interleaved);
                int height = heif_image_get_height(image, heif_channel_interleaved);
                if (width <= 0 || height <= 0) throw new InvalidDataException("The HEIF image has an invalid size.");

                byte* source;
                long sourceStride;
                if (heif_image_get_plane_readonly2 != null)
                {
                    nuint stride;
                    source = heif_image_get_plane_readonly2(image, heif_channel_interleaved, &stride);
                    sourceStride = (long)stride;
                }
                else
                {
                    int stride;
                    source = heif_image_get_plane_readonly(image, heif_channel_interleaved, &stride);
                    sourceStride = stride;
                }

                if (source == null) throw new InvalidDataException("libheif did not return RGBA pixels.");

                HeifImageInfo info = new(width, height, hasAlpha, iccProfile);
                IntPtr destination = allocate(info, out int destinationStride);
                long rowLength = (long)width * 4;

                if (destination == IntPtr.Zero || destinationStride < rowLength || sourceStride < rowLength)
                {
                    throw new InvalidDataException("Invalid pixel buffer for the decoded HEIF image.");
                }

                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(source + y * sourceStride, (byte*)destination + (long)y * destinationStride,
                        destinationStride, rowLength);
                }
            }
        }
        finally
        {
            if (image != IntPtr.Zero) heif_image_release(image);
            if (handle != IntPtr.Zero) heif_image_handle_release(handle);
            heif_context_free(context);
        }
    }

    private static byte[]? ReadIccProfile(IntPtr handle)
    {
        int type = heif_image_handle_get_color_profile_type(handle);
        if (type != heif_color_profile_type_prof && type != heif_color_profile_type_rICC) return null;

        nuint size = heif_image_handle_get_raw_color_profile_size(handle);
        if (size == 0 || size > 16 * 1024 * 1024) return null;

        byte[] profile = new byte[(int)size];
        fixed (byte* profilePointer = profile)
        {
            HeifError error = heif_image_handle_get_raw_color_profile(handle, profilePointer);
            return error.Code == 0 ? profile : null;
        }
    }

    private static void Check(HeifError error)
    {
        if (error.Code != 0)
        {
            string message = error.Message != IntPtr.Zero ? Marshal.PtrToStringAnsi(error.Message) ?? "" : "";
            throw new InvalidDataException($"libheif error {error.Code}.{error.Subcode}: {message}");
        }
    }

    private static void EnsureLoaded()
    {
        if (loadAttempted) return;

        lock (loadLock)
        {
            if (loadAttempted) return;

            try
            {
                if (TryLoadLibrary(out IntPtr library, out string? path))
                {
                    isAvailable = ResolveExports(library);
                    LoadInfo = isAvailable ? path : $"{path} is missing required functions.";

                    if (isAvailable && heif_init != null)
                    {
                        // Required for libheif builds that load decoders as plugins.
                        heif_init(IntPtr.Zero);
                    }
                }
                else
                {
                    LoadInfo = $"{string.Join(" / ", LibraryNames)} was not found.";
                }
            }
            catch (Exception e)
            {
                isAvailable = false;
                LoadInfo = e.Message;
            }
            finally
            {
                loadAttempted = true;
            }
        }
    }

    private static bool TryLoadLibrary(out IntPtr library, out string? path)
    {
        string baseDirectory = AppContext.BaseDirectory;
        string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;

        foreach (string name in LibraryNames)
        {
            // Prefer the copy shipped next to ShareX so its own dependencies (libde265) resolve from there.
            foreach (string candidate in new[] { Path.Combine(baseDirectory, name), Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", name) })
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out library))
                {
                    path = candidate;
                    return true;
                }
            }
        }

        foreach (string name in LibraryNames)
        {
            if (NativeLibrary.TryLoad(name, Assembly.GetExecutingAssembly(), DllImportSearchPath.SafeDirectories, out library))
            {
                path = name;
                return true;
            }
        }

        library = IntPtr.Zero;
        path = null;
        return false;
    }

    private static bool ResolveExports(IntPtr library)
    {
        IntPtr Get(string name) => NativeLibrary.TryGetExport(library, name, out IntPtr address) ? address : IntPtr.Zero;

        heif_init = (delegate* unmanaged<IntPtr, HeifError>)Get(nameof(heif_init));
        heif_get_version = (delegate* unmanaged<sbyte*>)Get(nameof(heif_get_version));
        heif_context_alloc = (delegate* unmanaged<IntPtr>)Get(nameof(heif_context_alloc));
        heif_context_free = (delegate* unmanaged<IntPtr, void>)Get(nameof(heif_context_free));
        heif_context_read_from_memory_without_copy = (delegate* unmanaged<IntPtr, void*, nuint, IntPtr, HeifError>)Get(nameof(heif_context_read_from_memory_without_copy));
        heif_context_get_primary_image_handle = (delegate* unmanaged<IntPtr, IntPtr*, HeifError>)Get(nameof(heif_context_get_primary_image_handle));
        heif_image_handle_release = (delegate* unmanaged<IntPtr, void>)Get(nameof(heif_image_handle_release));
        heif_image_handle_has_alpha_channel = (delegate* unmanaged<IntPtr, int>)Get(nameof(heif_image_handle_has_alpha_channel));
        heif_image_handle_get_color_profile_type = (delegate* unmanaged<IntPtr, int>)Get(nameof(heif_image_handle_get_color_profile_type));
        heif_image_handle_get_raw_color_profile_size = (delegate* unmanaged<IntPtr, nuint>)Get(nameof(heif_image_handle_get_raw_color_profile_size));
        heif_image_handle_get_raw_color_profile = (delegate* unmanaged<IntPtr, void*, HeifError>)Get(nameof(heif_image_handle_get_raw_color_profile));
        heif_decode_image = (delegate* unmanaged<IntPtr, IntPtr*, int, int, IntPtr, HeifError>)Get(nameof(heif_decode_image));
        heif_image_get_width = (delegate* unmanaged<IntPtr, int, int>)Get(nameof(heif_image_get_width));
        heif_image_get_height = (delegate* unmanaged<IntPtr, int, int>)Get(nameof(heif_image_get_height));
        heif_image_get_plane_readonly2 = (delegate* unmanaged<IntPtr, int, nuint*, byte*>)Get(nameof(heif_image_get_plane_readonly2));
        heif_image_get_plane_readonly = (delegate* unmanaged<IntPtr, int, int*, byte*>)Get(nameof(heif_image_get_plane_readonly));
        heif_image_release = (delegate* unmanaged<IntPtr, void>)Get(nameof(heif_image_release));

        return heif_context_alloc != null && heif_context_free != null && heif_context_read_from_memory_without_copy != null &&
            heif_context_get_primary_image_handle != null && heif_image_handle_release != null &&
            heif_image_handle_has_alpha_channel != null && heif_image_handle_get_color_profile_type != null &&
            heif_image_handle_get_raw_color_profile_size != null && heif_image_handle_get_raw_color_profile != null &&
            heif_decode_image != null && heif_image_get_width != null && heif_image_get_height != null &&
            (heif_image_get_plane_readonly2 != null || heif_image_get_plane_readonly != null) && heif_image_release != null;
    }
}
