using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using CAgent.Core.Abstractions;
using CAgent.Core.Protocol;

namespace CAgent.Tools;

public interface IScreenCaptureBackend
{
    Task<(int Width, int Height)> CapturePngAsync(string outputPath, CancellationToken cancellationToken);
}

public sealed class ScreenshotTool : ITool
{
    private readonly IScreenCaptureBackend _backend;

    public ScreenshotTool(IScreenCaptureBackend? backend = null)
    {
        _backend = backend ?? new DefaultScreenCaptureBackend();
    }

    public string Name => "screenshot";
    public bool IsMutating => false;

    public async Task<ToolExecutionResult> ExecuteAsync(ToolCallRequest request, ToolExecutionContext context)
    {
        var baseDir = !string.IsNullOrWhiteSpace(context.WorkspacePath) && Directory.Exists(context.WorkspacePath)
            ? Path.Combine(context.WorkspacePath, ".cagent", "screenshots")
            : Path.Combine(Path.GetTempPath(), "CAgent", "screenshots");

        Directory.CreateDirectory(baseDir);
        var filePath = Path.Combine(baseDir, $"screen_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss_fff}.png");

        var (width, height) = await _backend.CapturePngAsync(filePath, context.CancellationToken).ConfigureAwait(false);
        if (!File.Exists(filePath))
        {
            return ToolExecutionResult.Fail("屏幕截图失败，未生成图片文件。");
        }

        var message = width > 0 && height > 0
            ? $"已截取当前桌面屏幕 ({width}x{height})，图片路径: {filePath}"
            : $"已截取当前桌面屏幕，图片路径: {filePath}";

        return ToolExecutionResult.Ok(message, [filePath]);
    }
}

public sealed class DefaultScreenCaptureBackend : IScreenCaptureBackend
{
    public async Task<(int Width, int Height)> CapturePngAsync(string outputPath, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsGdiCapture.CaptureVirtualScreenToPng(outputPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            await RunExternalCaptureToolAsync("/usr/sbin/screencapture", ["-x", outputPath], cancellationToken).ConfigureAwait(false);
            return (0, 0);
        }

        // Linux: try Wayland (grim) -> GNOME -> scrot -> ImageMagick (import)
        var candidates = new (string Cmd, string[] Args)[]
        {
            ("grim", [outputPath]),
            ("gnome-screenshot", ["-f", outputPath]),
            ("scrot", [outputPath]),
            ("import", ["-window", "root", outputPath])
        };

        foreach (var (cmd, args) in candidates)
        {
            if (await TryRunExternalToolAsync(cmd, args, cancellationToken).ConfigureAwait(false) && File.Exists(outputPath))
            {
                return (0, 0);
            }
        }

        throw new PlatformNotSupportedException("未找到可用的 Linux 截图工具（请安装 grim、gnome-screenshot 或 scrot）。");
    }

    private static async Task RunExternalCaptureToolAsync(string fileName, string[] args, CancellationToken ct)
    {
        var ok = await TryRunExternalToolAsync(fileName, args, ct).ConfigureAwait(false);
        if (!ok)
        {
            throw new InvalidOperationException($"调用系统截图命令 '{fileName}' 失败。");
        }
    }

    private static async Task<bool> TryRunExternalToolAsync(string fileName, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

public static class PngEncoder
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrc32Table();

    /// <summary>
    /// Encodes top-down BGRA32 pixel bytes into a standard lossless RGBA PNG stream without external dependencies.
    /// </summary>
    public static void WriteBgra32AsPng(Stream output, int width, int height, ReadOnlySpan<byte> bgraPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgraPixels.Length < width * height * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than width * height * 4.", nameof(bgraPixels));
        }

        output.Write(PngSignature);

        // 1. IHDR chunk
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8;  // 8 bits per channel
        ihdr[9] = 6;  // Color type 6: RGBA
        ihdr[10] = 0; // Deflate compression
        ihdr[11] = 0; // Adaptive filtering
        ihdr[12] = 0; // No interlace
        WriteChunk(output, "IHDR"u8, ihdr);

        // 2. IDAT chunk (ZLib compressed scanlines)
        using (var compressedMs = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                var rowBytes = new byte[1 + width * 4];
                var stride = width * 4;
                for (var y = 0; y < height; y++)
                {
                    rowBytes[0] = 0; // Filter type 0 (None)
                    var srcRow = bgraPixels.Slice(y * stride, stride);
                    var dstIdx = 1;
                    for (var x = 0; x < stride; x += 4)
                    {
                        rowBytes[dstIdx] = srcRow[x + 2];     // R
                        rowBytes[dstIdx + 1] = srcRow[x + 1]; // G
                        rowBytes[dstIdx + 2] = srcRow[x];     // B
                        rowBytes[dstIdx + 3] = 255;           // Opaque Alpha
                        dstIdx += 4;
                    }
                    zlib.Write(rowBytes, 0, rowBytes.Length);
                }
            }
            WriteChunk(output, "IDAT"u8, compressedMs.GetBuffer().AsSpan(0, (int)compressedMs.Length));
        }

        // 3. IEND chunk
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> typeAscii4, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        output.Write(lenBytes);
        output.Write(typeAscii4);
        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        var crc = ComputeCrc32(typeAscii4, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in first)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        foreach (var b in second)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}

internal static partial class WindowsGdiCapture
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, int rop);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, [Out] byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    public static (int Width, int Height) CaptureVirtualScreenToPng(string outputPath)
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var screenDc = GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            throw new InvalidOperationException("无法获取桌面 DC 句柄。");
        }

        try
        {
            var memDc = CreateCompatibleDC(screenDc);
            var hBitmap = CreateCompatibleBitmap(screenDc, width, height);
            var oldObj = SelectObject(memDc, hBitmap);

            try
            {
                if (!BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SRCCOPY))
                {
                    throw new InvalidOperationException("BitBlt 屏幕拷贝失败。");
                }

                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // negative height = top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                };

                var pixels = new byte[width * height * 4];
                var lines = GetDIBits(memDc, hBitmap, 0, (uint)height, pixels, ref bmi, DIB_RGB_COLORS);
                if (lines == 0)
                {
                    throw new InvalidOperationException("GetDIBits 提取屏幕像素失败。");
                }

                using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                PngEncoder.WriteBgra32AsPng(fs, width, height, pixels);
                return (width, height);
            }
            finally
            {
                SelectObject(memDc, oldObj);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
            }
        }
        finally
        {
            ReleaseDC(nint.Zero, screenDc);
        }
    }
}
