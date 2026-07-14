using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MirrorsEdgeTweaks.Controls
{
    // Snap chevron rotation until loaded, otherwise expanded sections spin on first layout.
    public class ExpanderChevronToggle : ToggleButton
    {
        private const string ChevronPartName = "ExpandPath";

        // Keep in sync with SectionExpandDuration / SectionCollapseDuration in Themes/SectionExpander.xaml.
        private static readonly Duration ExpandDuration = TimeSpan.FromMilliseconds(250);
        private static readonly Duration CollapseDuration = TimeSpan.FromMilliseconds(200);

        private bool _allowAnimation;
        private RotateTransform? _rotateTransform;

        public ExpanderChevronToggle()
        {
            Loaded += OnChevronLoaded;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            InitializeChevronTransform();
            ApplyChevronAngle(IsChecked == true ? 180 : 0, animate: false);
        }

        private void InitializeChevronTransform()
        {
            if (GetTemplateChild(ChevronPartName) is not UIElement chevron)
            {
                _rotateTransform = null;
                return;
            }

            _rotateTransform = new RotateTransform();
            chevron.RenderTransform = _rotateTransform;
        }

        private void OnChevronLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnChevronLoaded;
            ApplyChevronAngle(IsChecked == true ? 180 : 0, animate: false);
            _allowAnimation = true;
        }

        protected override void OnChecked(RoutedEventArgs e)
        {
            base.OnChecked(e);
            ApplyChevronAngle(180, animate: _allowAnimation);
        }

        protected override void OnUnchecked(RoutedEventArgs e)
        {
            base.OnUnchecked(e);
            ApplyChevronAngle(0, animate: _allowAnimation);
        }

        private void ApplyChevronAngle(double angle, bool animate)
        {
            if (_rotateTransform is null)
                return;

            if (!animate)
            {
                _rotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
                _rotateTransform.Angle = angle;
                return;
            }

            var duration = angle > _rotateTransform.Angle ? ExpandDuration : CollapseDuration;
            var animation = new DoubleAnimation(angle, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            _rotateTransform.BeginAnimation(RotateTransform.AngleProperty, animation);
        }
    }
}
