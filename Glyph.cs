using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IMPWeldPhotos;

/// <summary>A Phosphor icon (Assets/Icons.xaml) drawn in its 256 x 256 box and
/// filled with the inherited Foreground, so it follows button hover and theme.</summary>
public sealed class Glyph : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Glyph));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }
}
