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

    public double Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, 0.55, 2.8); InvalidateVisual(); }
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

    public void SetViewForFace(int face)
    {
        if (_disposed) return;
        switch (face)
        {
            case 0: _yaw = Math.PI * 0.5; _pitch = 0; break;
            case 1: _yaw = -Math.PI * 0.5; _pitch = 0; break;
            case 2: _yaw = 0; _pitch = -Math.PI * 0.5; break;
            case 3: _yaw = 0; _pitch = Math.PI * 0.5; break;
            case 4: _yaw = 0; _pitch = 0; break;
            case 5: _yaw = Math.PI; _pitch = 0; break;
            default: return;
        }
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
                _zoom = Math.Clamp(_zoom * (distance / _lastPinchDistance), 0.55, 2.8);
            _yaw -= (center.X - _lastPinchCenter.X) * 0.006;
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

        // Orthographic projection of a sphere: only pixels inside the unit disk sample the environment.
        double diameter = Math.Min(Bounds.Width, Bounds.Height);
        double scale = Math.Min(1.0, MaxRenderDimension / diameter);
        int size = Math.Max(1, (int)Math.Ceiling(diameter * scale));
        EnsureFrame(size, size);
        double cy = Math.Cos(_yaw), sy = Math.Sin(_yaw);
        double cp = Math.Cos(_pitch), sp = Math.Sin(_pitch);
        double radius = size * 0.47;
        double center = (size - 1) * 0.5;

        for (int y = 0; y < size; y++)
        {
            double ny = (center - y) / radius;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                double nx = (x - center) / radius;
                double r2 = nx * nx + ny * ny;
                if (r2 > 1.0)
                {
                    _pixels[row + x] = unchecked((int)0xFF080A0E);
                    continue;
                }

                double nz = Math.Sqrt(Math.Max(0, 1.0 - r2));
                double py = ny * cp - nz * sp;
                double pz = ny * sp + nz * cp;
                double dx = nx * cy + pz * sy;
                double dz = -nx * sy + pz * cy;
                SampleCube(dx, py, dz, out int color);
                double light = 0.72 + 0.28 * Math.Max(0, nz);
                int red = Math.Clamp((int)(((color >> 16) & 255) * _brightness * light), 0, 255);
                int green = Math.Clamp((int)(((color >> 8) & 255) * _brightness * light), 0, 255);
                int blue = Math.Clamp((int)((color & 255) * _brightness * light), 0, 255);
                _pixels[row + x] = unchecked((int)(0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | (uint)blue));
            }
        }

        using (var locked = _frame!.Lock())
        {
            for (int y = 0; y < size; y++)
                Marshal.Copy(_pixels, y * size, IntPtr.Add(locked.Address, y * locked.RowBytes), size);
        }
        double left = (Bounds.Width - diameter) * 0.5;
        double top = (Bounds.Height - diameter) * 0.5;
        context.DrawImage(_frame!, new Rect(0, 0, size, size), new Rect(left, top, diameter, diameter));
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
