using System.Windows;

namespace MirrorsEdgeTweaks.Controls
{
    public static class SectionExpanderProperties
    {
        public static readonly DependencyProperty HorizontalHeaderStyleProperty =
            DependencyProperty.RegisterAttached(
                "HorizontalHeaderStyle",
                typeof(Style),
                typeof(SectionExpanderProperties),
                new PropertyMetadata(null));

        public static Style? GetHorizontalHeaderStyle(DependencyObject obj) =>
            (Style?)obj.GetValue(HorizontalHeaderStyleProperty);

        public static void SetHorizontalHeaderStyle(DependencyObject obj, Style? value) =>
            obj.SetValue(HorizontalHeaderStyleProperty, value);
    }
}
