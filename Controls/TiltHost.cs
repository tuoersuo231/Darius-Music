using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CurrentLyrics.Controls;

/// <summary>
/// Hosts a 2D element on a plane in 3D, tilted around the vertical axis and hinged at the left
/// edge — the same construction CurrentMusic uses for its “3D 倾斜歌词” (rotationY = -32° with a
/// close camera). Hit-testing still goes through to the hosted element.
/// </summary>
public sealed class TiltHost : Grid
{
    private readonly Viewport3D _viewport = new();
    private readonly Viewport2DVisual3D _host = new();
    private readonly PerspectiveCamera _camera = new();
    private UIElement? _child;
    private double _angle = -32;
    private double _width = 1;
    private double _height = 1;

    public TiltHost()
    {
        ClipToBounds = false;
        Children.Add(_viewport);
        _camera.FieldOfView = 45;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.LookDirection = new Vector3D(0, 0, -1);
        _viewport.Camera = _camera;
        _viewport.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
        _viewport.Children.Add(_host);
    }

    /// <summary>Rotation around Y in degrees; 0 removes the tilt.</summary>
    public double Angle
    {
        get => _angle;
        set
        {
            if (Math.Abs(_angle - value) < 0.001) return;
            _angle = value;
            UpdateTransform();
        }
    }

    public void Host(UIElement child)
    {
        if (ReferenceEquals(_child, child)) return;
        _child = child;
        _host.Visual = child;
        var material = new DiffuseMaterial { Brush = new SolidColorBrush(Colors.White) };
        Viewport2DVisual3D.SetIsVisualHostMaterial(material, true);
        _host.Material = material;
        _host.Geometry = BuildMesh();
        UpdateTransform();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (_child == null) return new Size(0, 0);
        _child.Measure(constraint);
        return _child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_child == null) return finalSize;
        _width = Math.Max(finalSize.Width, 1);
        _height = Math.Max(finalSize.Height, 1);

        // Viewport2DVisual3D lays the hosted visual out through its own brush; arranging it here
        // as well made the plane render empty.
        _host.Geometry = BuildMesh();
        UpdateTransform();
        return finalSize;
    }

    private MeshGeometry3D BuildMesh()
    {
        double halfW = _width / 2, halfH = _height / 2;
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new Point3D(-halfW, -halfH, 0),
                new Point3D(halfW, -halfH, 0),
                new Point3D(halfW, halfH, 0),
                new Point3D(-halfW, halfH, 0),
            },
            TextureCoordinates = new PointCollection
            {
                new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0),
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        mesh.Freeze();
        return mesh;
    }

    private void UpdateTransform()
    {
        if (Math.Abs(_angle) < 0.01)
        {
            _host.Transform = Transform3D.Identity;
            // A flat plane still needs a camera far enough away to look orthographic.
            _camera.Position = new Point3D(0, 0, Math.Max(_width, _height) * 6);
            return;
        }

        // Hinge on the left edge, mid height, exactly like CurrentMusic's transformOrigin.
        _host.Transform = new RotateTransform3D(
            new AxisAngleRotation3D(new Vector3D(0, 1, 0), _angle),
            new Point3D(-_width / 2, 0, 0));

        // A camera close to the plane gives the strong near/far difference in the reference.
        _camera.Position = new Point3D(0, 0, Math.Max(_width * 0.62, 200));
    }
}
