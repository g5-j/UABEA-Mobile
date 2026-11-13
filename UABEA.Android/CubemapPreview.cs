using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UABEA.Mobile;

/// <summary>
/// Software-rendered cubemap viewer. Face order is Unity's +X, -X, +Y, -Y, +Z, -Z.
/// The viewer renders a perspective view from inside the cube and supports drag/pinch.
/// </summary>
internal sealed class CubemapPreviewControl : Control, IDisposable
{
    private readonly int[][] _faces;
    private readonly int _faceWidth;
    private readonly int _faceHeight;
    private readonly Dictionary<IPointer, Point> _pointers = new();
    private readonly HashSet<IPointer> _captured = new();
    private WriteableBitmap? _frame;
    private int[] _pixels = Array.Empty<int>();
    private int _frameWidth, _frameHeight;
    private double _yaw, _pitch, _zoom = 1.0, _lastPinchDistance;
    private Point _lastPinchCenter;
    private double _brightness = 1.0;
    private bool _disposed;

    private const int MaxRenderDimension = 640;

    public double Brightness
    {
        get => _brightness;
        set { _brightness = Math.Clamp(value, 0.15, 2.5); InvalidateVisual(); }
    }

    public CubemapPreviewControl(int[][] faces, int width, int height)
    {
        if (faces == null || faces.Length != 6 || faces.Any(f => f == null || f.Length != checked(width * height)))
            throw new ArgumentException("A Cubemap viewer requires six equally sized, decoded faces.", nameof(faces));
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
            throw new ArgumentOutOfRangeException(nameof(width));

        _faces = faces;
        _faceWidth = width;
        _faceHeight = height;
        MinHeight = 260;
        Height = 340;
        ClipToBounds = true;
        Focusable = true;
        DetachedFromVisualTree += (_, _) => Dispose();
    }

    public void ResetView()
    {
        if (_disposed) return;
        _yaw = 0;
        _pitch = 0;
        _zoom = 1;
        Brightness = 1;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_disposed) return;
        _pointers[e.Pointer] = e.GetPosition(this);
        if (_pointers.Count >= 2)
        {
            foreach (var pointer in _pointers.Keys)
            {
                pointer.Capture(this);
                _captured.Add(pointer);
            }
            var pair = _pointers.Values.Take(2).ToArray();
            _lastPinchDistance = Distance(pair[0], pair[1]);
            _lastPinchCenter = Midpoint(pair[0], pair[1]);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_disposed || !_pointers.TryGetValue(e.Pointer, out Point previous)) return;
        Point current = e.GetPosition(this);
        _pointers[e.Pointer] = current;

        if (_pointers.Count >= 2)
        {
            var pair = _pointers.Values.Take(2).ToArray();
            double distance = Distance(pair[0], pair[1]);
            Point center = Midpoint(pair[0], pair[1]);
            if (_lastPinchDistance > 1)
                _zoom = Math.Clamp(_zoom * (_lastPinchDistance / Math.Max(1, distance)), 0.55, 2.8);
            _yaw += (center.X - _lastPinchCenter.X) * 0.006;
            _pitch = Math.Clamp(_pitch + (center.Y - _lastPinchCenter.Y) * 0.006, -1.48, 1.48);
            _lastPinchDistance = distance;
            _lastPinchCenter = center;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!_captured.Contains(e.Pointer))
        {
            if (Distance(previous, current) < 2) return;
            e.Pointer.Capture(this);
            _captured.Add(e.Pointer);
        }
        _yaw -= (current.X - previous.X) * 0.006;
        _pitch = Math.Clamp(_pitch + (current.Y - previous.Y) * 0.006, -1.48, 1.48);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _pointers.Remove(e.Pointer);
        if (_captured.Remove(e.Pointer)) e.Pointer.Capture(null);
        _lastPinchDistance = 0;
        if (_pointers.Count == 1) _lastPinchCenter = _pointers.Values.First();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pointers.Clear();
        _captured.Clear();
        _lastPinchDistance = 0;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        Rect bounds = new(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(8, 10, 14)), bounds);
        if (_disposed || Bounds.Width < 1 || Bounds.Height < 1) return;

        double scale = Math.Min(1.0, MaxRenderDimension / Math.Max(Bounds.Width, Bounds.Height));
        int width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        int height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        EnsureFrame(width, height);

        double aspect = width / (double)height;
        double tanHalfFov = 0.72 / _zoom;
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw);
        double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        for (int y = 0; y < height; y++)
        {
            double ny = (1.0 - 2.0 * (y + 0.5) / height) * tanHalfFov;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                double nx = (2.0 * (x + 0.5) / width - 1.0) * tanHalfFov * aspect;
                // Camera looks along +Z. Apply pitch, then yaw to obtain world direction.
                double py = ny * cp - sp;
                double pz = ny * sp + cp;
                double dx = nx * cy + pz * sy;
                double dz = -nx * sy + pz * cy;
                SampleCube(dx, py, dz, out int color);
                int r = (color >> 16) & 255;
                int g = (color >> 8) & 255;
                int b = color & 255;
                r = Math.Clamp((int)(r * _brightness), 0, 255);
                g = Math.Clamp((int)(g * _brightness), 0, 255);
                b = Math.Clamp((int)(b * _brightness), 0, 255);
                _pixels[row + x] = unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
            }
        }

        using (var locked = _frame!.Lock())
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(_pixels, y * width, IntPtr.Add(locked.Address, y * locked.RowBytes), width);
        }
        context.DrawImage(_frame!, new Rect(0, 0, width, height), bounds);
    }

    private void SampleCube(double x, double y, double z, out int color)
    {
        double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
        int face;
        double u, v, major;
        if (ax >= ay && ax >= az)
        {
            major = Math.Max(ax, 1e-9);
            if (x >= 0) { face = 0; u = -z / major; v = -y / major; }
            else { face = 1; u = z / major; v = -y / major; }
        }
        else if (ay >= ax && ay >= az)
        {
            major = Math.Max(ay, 1e-9);
            if (y >= 0) { face = 2; u = x / major; v = z / major; }
            else { face = 3; u = x / major; v = -z / major; }
        }
        else
        {
            major = Math.Max(az, 1e-9);
            if (z >= 0) { face = 4; u = x / major; v = -y / major; }
            else { face = 5; u = -x / major; v = -y / major; }
        }

        double fx = Math.Clamp((u + 1) * 0.5 * (_faceWidth - 1), 0, _faceWidth - 1);
        double fy = Math.Clamp((v + 1) * 0.5 * (_faceHeight - 1), 0, _faceHeight - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, _faceWidth - 1), y1 = Math.Min(y0 + 1, _faceHeight - 1);
        double tx = fx - x0, ty = fy - y0;
        int[] data = _faces[face];
        int c00 = data[y0 * _faceWidth + x0], c10 = data[y0 * _faceWidth + x1];
        int c01 = data[y1 * _faceWidth + x0], c11 = data[y1 * _faceWidth + x1];
        int r = Blend((c00 >> 16) & 255, (c10 >> 16) & 255, (c01 >> 16) & 255, (c11 >> 16) & 255, tx, ty);
        int g = Blend((c00 >> 8) & 255, (c10 >> 8) & 255, (c01 >> 8) & 255, (c11 >> 8) & 255, tx, ty);
        int b = Blend(c00 & 255, c10 & 255, c01 & 255, c11 & 255, tx, ty);
        color = unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
    }

    private static int Blend(int a, int b, int c, int d, double x, double y)
    {
        double top = a + (b - a) * x;
        double bottom = c + (d - c) * x;
        return Math.Clamp((int)Math.Round(top + (bottom - top) * y), 0, 255);
    }

    private void EnsureFrame(int width, int height)
    {
        if (_frame != null && width == _frameWidth && height == _frameHeight) return;
        _frame?.Dispose();
        _frameWidth = width;
        _frameHeight = height;
        _frame = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        _pixels = new int[checked(width * height)];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frame?.Dispose();
        _frame = null;
        _pixels = Array.Empty<int>();
        _pointers.Clear();
        _captured.Clear();
    }

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
