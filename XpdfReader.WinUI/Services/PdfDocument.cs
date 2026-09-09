using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XpdfReader.WinUI.Services;

public readonly record struct PageSize(double Width, double Height);
public readonly record struct OutlineEntry(int Depth, int Page, string Title);
public sealed record RenderedPage(byte[] Pixels, int Width, int Height, int Stride);

public sealed class PdfException : Exception
{
    public int Status { get; }
    public bool IsPasswordRequired => Status == 2;
    public bool IsPermissionDenied => Status == 4;

    internal PdfException(int status, string message) : base(message) => Status = status;
}

public sealed class PdfDocument : IDisposable
{
    private readonly PdfSafeHandle handle;
    private readonly object sync = new();
    private bool disposed;

    public int PageCount { get; }

    private PdfDocument(PdfSafeHandle handle, int pageCount)
    {
        this.handle = handle;
        PageCount = pageCount;
    }

    public static PdfDocument Open(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0') || password?.Contains('\0') == true)
            throw new ArgumentException("Paths and passwords cannot contain null characters.");
        ThrowIfFailed(NativeMethods.OpenDocument(Path.GetFullPath(path), password, out var pointer));
        var handle = new PdfSafeHandle(pointer);
        try
        {
            var count = NativeMethods.GetPageCount(handle);
            if (count <= 0)
                throw new PdfException(1, "The PDF does not contain any readable pages.");
            return new PdfDocument(handle, count);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public PageSize GetPageSize(int page, int rotation)
    {
        lock (sync)
        {
            ValidatePage(page);
            ThrowIfFailed(NativeMethods.GetPageSize(handle, page, rotation, out var width, out var height));
            return new PageSize(width, height);
        }
    }

    public PageSize[] GetAllPageSizes(int rotation)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (rotation is not (0 or 90 or 180 or 270))
                throw new ArgumentOutOfRangeException(nameof(rotation));
            var widths = new double[PageCount];
            var heights = new double[PageCount];
            ThrowIfFailed(NativeMethods.GetPageSizes(handle, rotation, widths, heights, PageCount));
            var sizes = new PageSize[PageCount];
            for (int index = 0; index < sizes.Length; index++)
                sizes[index] = new PageSize(widths[index], heights[index]);
            return sizes;
        }
    }

    public List<OutlineEntry> GetOutline()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ThrowIfFailed(NativeMethods.GetOutline(handle, out IntPtr text));
            try
            {
                var entries = new List<OutlineEntry>();
                if (text == IntPtr.Zero)
                    return entries;
                string raw = Marshal.PtrToStringUTF8(text) ?? string.Empty;
                foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    int first = line.IndexOf((char)0x1F);
                    if (first < 0)
                        continue;
                    int second = line.IndexOf((char)0x1F, first + 1);
                    if (second < 0)
                        continue;
                    if (int.TryParse(line.AsSpan(0, first), out int depth) &&
                        int.TryParse(line.AsSpan(first + 1, second - first - 1), out int page))
                    {
                        entries.Add(new OutlineEntry(depth, page, line[(second + 1)..]));
                    }
                }
                return entries;
            }
            finally
            {
                NativeMethods.FreeBuffer(text);
            }
        }
    }

    public RenderedPage RenderPage(int page, double dpi, int rotation)
    {
        lock (sync)
        {
            ValidatePage(page);
            ThrowIfFailed(NativeMethods.RenderPage(handle, page, dpi, rotation,
                out var pixels, out var width, out var height, out var stride));
            try
            {
                if (pixels == IntPtr.Zero || width <= 0 || height <= 0 || stride != checked(width * 4))
                    throw new PdfException(1, "The PDF renderer returned an invalid bitmap.");
                var data = new byte[checked(stride * height)];
                Marshal.Copy(pixels, data, 0, data.Length);
                return new RenderedPage(data, width, height, stride);
            }
            finally
            {
                NativeMethods.FreeBuffer(pixels);
            }
        }
    }

    public RenderedPage RenderPageTile(
        int page,
        double dpi,
        int rotation,
        int sliceX,
        int sliceY,
        int sliceWidth,
        int sliceHeight)
    {
        lock (sync)
        {
            ValidatePage(page);
            ThrowIfFailed(NativeMethods.RenderPageTile(handle, page, dpi, rotation,
                sliceX, sliceY, sliceWidth, sliceHeight,
                out var pixels, out var width, out var height, out var stride));
            try
            {
                if (pixels == IntPtr.Zero || width <= 0 || height <= 0 || stride != checked(width * 4))
                    throw new PdfException(1, "The PDF renderer returned an invalid tile bitmap.");
                var data = new byte[checked(stride * height)];
                Marshal.Copy(pixels, data, 0, data.Length);
                return new RenderedPage(data, width, height, stride);
            }
            finally
            {
                NativeMethods.FreeBuffer(pixels);
            }
        }
    }

    public string GetPageText(int page)
    {
        lock (sync)
        {
            ValidatePage(page);
            ThrowIfFailed(NativeMethods.GetPageText(handle, page, out var text));
            try
            {
                return Marshal.PtrToStringUTF8(text) ?? string.Empty;
            }
            finally
            {
                NativeMethods.FreeBuffer(text);
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            handle.Dispose();
        }
    }

    private void ValidatePage(int page)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (page < 1 || page > PageCount) throw new ArgumentOutOfRangeException(nameof(page));
    }

    private static void ThrowIfFailed(int status)
    {
        if (status != 0)
            throw new PdfException(status, Marshal.PtrToStringUTF8(NativeMethods.GetLastError())
                ?? "The PDF operation failed.");
    }
}

internal sealed class PdfSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal PdfSafeHandle(IntPtr value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle()
    {
        NativeMethods.CloseDocument(handle);
        return true;
    }
}

internal static class NativeMethods
{
    private const string Library = "xpdf_winui_native";

    [DllImport(Library, EntryPoint = "xpdf_open_document", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int OpenDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? password, out IntPtr document);

    [DllImport(Library, EntryPoint = "xpdf_close_document", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void CloseDocument(IntPtr document);

    [DllImport(Library, EntryPoint = "xpdf_get_page_count", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int GetPageCount(PdfSafeHandle document);

    [DllImport(Library, EntryPoint = "xpdf_get_page_size", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int GetPageSize(PdfSafeHandle document, int page, int rotation, out double width, out double height);

    [DllImport(Library, EntryPoint = "xpdf_get_page_sizes", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int GetPageSizes(PdfSafeHandle document, int rotation,
        [Out] double[] widths, [Out] double[] heights, int count);

    [DllImport(Library, EntryPoint = "xpdf_render_page", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int RenderPage(PdfSafeHandle document, int page, double dpi, int rotation,
        out IntPtr pixels, out int width, out int height, out int stride);

    [DllImport(Library, EntryPoint = "xpdf_render_page_tile", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int RenderPageTile(PdfSafeHandle document, int page, double dpi, int rotation,
        int sliceX, int sliceY, int sliceWidth, int sliceHeight,
        out IntPtr pixels, out int width, out int height, out int stride);

    [DllImport(Library, EntryPoint = "xpdf_get_outline", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int GetOutline(PdfSafeHandle document, out IntPtr text);

    [DllImport(Library, EntryPoint = "xpdf_get_page_text", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int GetPageText(PdfSafeHandle document, int page, out IntPtr text);

    [DllImport(Library, EntryPoint = "xpdf_free_buffer", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void FreeBuffer(IntPtr buffer);

    [DllImport(Library, EntryPoint = "xpdf_get_last_error", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr GetLastError();
}
