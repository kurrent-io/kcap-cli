using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Materials;

namespace Capacitor.App.Controls;

/// Inherited, so a subtree pins its own material.
public sealed class MaterialScope : AvaloniaObject {
    MaterialScope() { }

    public static readonly AttachedProperty<SurfaceMaterial> MaterialProperty =
        AvaloniaProperty.RegisterAttached<MaterialScope, Control, SurfaceMaterial>(
            "Material", SurfaceMaterial.Opaque, inherits: true);

    public static SurfaceMaterial GetMaterial(Control control) => control.GetValue(MaterialProperty);

    public static void SetMaterial(Control control, SurfaceMaterial value) => control.SetValue(MaterialProperty, value);
}
