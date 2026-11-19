using System.Runtime.InteropServices;
using SkiaSharp;

namespace PortIT.Core;

/// <summary>
/// Puts a picture on the Windows clipboard, so "Copy picture" then Ctrl+V in Discord just works (2026-11-19).
/// Two forms at once: "PNG" - what Discord, browsers and most modern programs read first, sharp and exact - and
/// CF_DIB, the classic bitmap every older program (Paint, Word) understands.
///
/// The clipboard is opened with an OWNER WINDOW (a hidden message-only window made once). Opened with no window,
/// EmptyClipboard leaves the clipboard with no owner, and Windows documents that SetClipboardData then fails - the
/// first version said "copied" while nothing could be pasted. And it does not trust the return values: after
/// copying it opens the clipboard again and checks the picture is really there.
/// </summary>
public static class ClipImage
{
    const uint CF_DIB = 8, GMEM_MOVEABLE = 0x0002;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalFree(IntPtr mem);
    [DllImport("kernel32.dll", SetLastError = true)] static extern UIntPtr GlobalSize(IntPtr mem);

    static IntPtr owner;
    static readonly object gate = new();

    /// <summary>a hidden message-only window, made once and kept, to own what we put on the clipboard</summary>
    static IntPtr Owner()
    {
        if (owner == IntPtr.Zero) owner = CreateWindowEx(0, "STATIC", "PortIT clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return owner;
    }

    static bool Open(IntPtr hwnd)
    {
        // (another program may hold the clipboard for a moment - try for about a second)
        for (int i = 0; i < 25; i++)
        {
            if (OpenClipboard(hwnd)) return true;
            Thread.Sleep(40);
        }
        return false;
    }

    /// <summary>true only when the picture is on the clipboard and was checked to be there</summary>
    public static bool SetPng(byte[] png)
    {
        byte[] dib = ToDib(png);
        lock (gate)
        {
            var hwnd = Owner();
            uint pngFormat = RegisterClipboardFormat("PNG");
            if (!Open(hwnd)) return false;
            try
            {
                if (!EmptyClipboard()) return false;
                bool a = Put(pngFormat, png);
                bool b = Put(CF_DIB, dib);
                if (!a && !b) return false;
            }
            finally { CloseClipboard(); }

            // read it back: is a picture really there now, and is it ours
            if (!Open(hwnd)) return false;
            try
            {
                if (IsClipboardFormatAvailable(pngFormat))
                {
                    var h = GetClipboardData(pngFormat);
                    if (h != IntPtr.Zero && (long)(ulong)GlobalSize(h) >= png.Length) return true;
                }
                return IsClipboardFormatAvailable(CF_DIB) && GetClipboardData(CF_DIB) != IntPtr.Zero;
            }
            finally { CloseClipboard(); }
        }
    }

    static bool Put(uint format, byte[] data)
    {
        IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (mem == IntPtr.Zero) return false;
        IntPtr p = GlobalLock(mem);
        if (p == IntPtr.Zero) { GlobalFree(mem); return false; }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(mem);
        if (SetClipboardData(format, mem) == IntPtr.Zero) { GlobalFree(mem); return false; }
        return true;                                    // (the clipboard owns the memory now)
    }

    /// <summary>a 32-bit bottom-up DIB: BITMAPINFOHEADER then the rows, last row first</summary>
    static byte[] ToDib(byte[] png)
    {
        using var src = SKBitmap.Decode(png) ?? throw new ArgumentException("not a picture");
        using var bgra = src.Copy(SKColorType.Bgra8888);
        int w = bgra.Width, h = bgra.Height, row = w * 4;
        var outb = new byte[40 + row * h];
        BitConverter.GetBytes(40).CopyTo(outb, 0);          // biSize
        BitConverter.GetBytes(w).CopyTo(outb, 4);           // biWidth
        BitConverter.GetBytes(h).CopyTo(outb, 8);           // biHeight (positive: bottom-up)
        BitConverter.GetBytes((short)1).CopyTo(outb, 12);   // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(outb, 14);  // biBitCount
        BitConverter.GetBytes(0).CopyTo(outb, 16);          // BI_RGB
        BitConverter.GetBytes(row * h).CopyTo(outb, 20);
        var px = new byte[row * h];
        Marshal.Copy(bgra.GetPixels(), px, 0, px.Length);
        for (int y = 0; y < h; y++) Array.Copy(px, y * row, outb, 40 + (h - 1 - y) * row, row);
        return outb;
    }
}
