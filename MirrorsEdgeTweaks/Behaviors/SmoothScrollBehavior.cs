using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MirrorsEdgeTweaks.Behaviors
{
    public class SmoothScrollBehavior
    {
        private static readonly Dictionary<ScrollViewer, double> _targetOffsets = new Dictionary<ScrollViewer, double>();
        private static readonly Dictionary<ScrollViewer, bool> _isAnimating = new Dictionary<ScrollViewer, bool>();
        private static readonly Dictionary<ScrollViewer, EventHandler> _renderingHandlers = new Dictionary<ScrollViewer, EventHandler>();

        public static readonly DependencyProperty SmoothScrollProperty =
            DependencyProperty.RegisterAttached(
                "SmoothScroll",
                typeof(bool),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnSmoothScrollChanged));

        public static readonly DependencyProperty FadeEdgesProperty =
            DependencyProperty.RegisterAttached(
                "FadeEdges",
                typeof(bool),
                typeof(SmoothScrollBehavior),
                new PropertyMetadata(false, OnFadeEdgesChanged));

        public static bool GetSmoothScroll(DependencyObject obj)
        {
            return (bool)obj.GetValue(SmoothScrollProperty);
        }

        public static void SetSmoothScroll(DependencyObject obj, bool value)
        {
            obj.SetValue(SmoothScrollProperty, value);
        }

        public static bool GetFadeEdges(DependencyObject obj)
        {
            return (bool)obj.GetValue(FadeEdgesProperty);
        }

        public static void SetFadeEdges(DependencyObject obj, bool value)
        {
            obj.SetValue(FadeEdgesProperty, value);
        }

        private static void OnFadeEdgesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                scrollViewer.Loaded -= ScrollViewer_LoadedForFade;
                scrollViewer.ScrollChanged -= ScrollViewer_ScrollChangedForFade;
                scrollViewer.SizeChanged -= ScrollViewer_SizeChanged;

                if ((bool)e.NewValue)
                {
                    scrollViewer.Loaded += ScrollViewer_LoadedForFade;
                    scrollViewer.ScrollChanged += ScrollViewer_ScrollChangedForFade;
                    scrollViewer.SizeChanged += ScrollViewer_SizeChanged;
                }
            }
        }

        private static void ScrollViewer_LoadedForFade(object sender, RoutedEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                UpdateOpacityMask(scrollViewer);
            }
        }

        private static void ScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                UpdateOpacityMask(scrollViewer);
            }
        }

        private static void ScrollViewer_ScrollChangedForFade(object sender, ScrollChangedEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                UpdateOpacityMask(scrollViewer);
            }
        }

        private static void UpdateOpacityMask(ScrollViewer scrollViewer)
        {
            if (scrollViewer.ScrollableHeight == 0)
            {
                scrollViewer.OpacityMask = null;
                return;
            }

            double topFadeStart = 0;
            double topFadeEnd = 0.05;
            double bottomFadeStart = 0.95;
            double bottomFadeEnd = 1;

            bool atTop = scrollViewer.VerticalOffset <= 0.1;
            bool atBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 0.1;

            var gradientBrush = new LinearGradientBrush();
            gradientBrush.StartPoint = new System.Windows.Point(0, 0);
            gradientBrush.EndPoint = new System.Windows.Point(0, 1);

            if (atTop && atBottom)
            {
                scrollViewer.OpacityMask = null;
                return;
            }
            else if (atTop)
            {
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Black, 0));
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Black, bottomFadeStart));
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Transparent, bottomFadeEnd));
            }
            else if (atBottom)
            {
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Transparent, topFadeStart));
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Black, topFadeEnd));
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Black, 1));
            }
            else
            {
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Transparent, topFadeStart));
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Black, topFadeEnd));
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Black, bottomFadeStart));
                gradientBrush.GradientStops.Add(new GradientStop(Colors.Transparent, bottomFadeEnd));
            }

            scrollViewer.OpacityMask = gradientBrush;
        }

        private static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                scrollViewer.Loaded -= ScrollViewer_Loaded;
                scrollViewer.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;
                scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;

                if ((bool)e.NewValue)
                {
                    scrollViewer.Loaded += ScrollViewer_Loaded;
                    scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
                    scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
                }
                else
                {
                    _targetOffsets.Remove(scrollViewer);
                    _isAnimating.Remove(scrollViewer);

                    if (_renderingHandlers.ContainsKey(scrollViewer))
                    {
                        CompositionTarget.Rendering -= _renderingHandlers[scrollViewer];
                        _renderingHandlers.Remove(scrollViewer);
                    }
                }
            }
        }

        private static void ScrollViewer_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
                scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
            }
        }

        private static void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                if (!_isAnimating.ContainsKey(scrollViewer) || !_isAnimating[scrollViewer])
                {
                    if (e.VerticalChange != 0)
                    {
                        _targetOffsets[scrollViewer] = scrollViewer.VerticalOffset;
                    }
                }
            }
        }

        private static void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer scrollViewer)
            {
                if (ShouldIgnoreMouseWheel(e.OriginalSource as DependencyObject, scrollViewer))
                {
                    return;
                }

                if (!_targetOffsets.ContainsKey(scrollViewer))
                {
                    _targetOffsets[scrollViewer] = scrollViewer.VerticalOffset;
                }

                double newTargetOffset = _targetOffsets[scrollViewer] - (e.Delta * 0.5);
                newTargetOffset = Math.Max(0, Math.Min(scrollViewer.ScrollableHeight, newTargetOffset));
                _targetOffsets[scrollViewer] = newTargetOffset;

                if (!_renderingHandlers.ContainsKey(scrollViewer))
                {
                    EventHandler handler = (s, args) => OnRendering(scrollViewer);
                    _renderingHandlers[scrollViewer] = handler;
                    _isAnimating[scrollViewer] = true;
                    CompositionTarget.Rendering += handler;
                }
                else if (!_isAnimating.ContainsKey(scrollViewer) || !_isAnimating[scrollViewer])
                {
                    _isAnimating[scrollViewer] = true;
                    CompositionTarget.Rendering += _renderingHandlers[scrollViewer];
                }

                e.Handled = true;
            }
        }

        private static void OnRendering(ScrollViewer scrollViewer)
        {
            if (!_targetOffsets.ContainsKey(scrollViewer))
            {
                StopAnimation(scrollViewer);
                return;
            }

            double current = scrollViewer.VerticalOffset;
            double target = _targetOffsets[scrollViewer];
            double difference = target - current;

            if (Math.Abs(difference) < 0.5)
            {
                scrollViewer.ScrollToVerticalOffset(target);
                StopAnimation(scrollViewer);
                return;
            }

            double newOffset = current + (difference * 0.10);
            scrollViewer.ScrollToVerticalOffset(newOffset);
        }

        private static void StopAnimation(ScrollViewer scrollViewer)
        {
            if (_renderingHandlers.ContainsKey(scrollViewer))
            {
                CompositionTarget.Rendering -= _renderingHandlers[scrollViewer];
            }
            _isAnimating[scrollViewer] = false;
        }

        private static bool ShouldIgnoreMouseWheel(DependencyObject? originalSource, ScrollViewer mainScrollViewer)
        {
            DependencyObject? current = originalSource;

            while (current != null)
            {
                if (current == mainScrollViewer)
                {
                    return false;
                }

                if (current is ScrollViewer ||
                    current is System.Windows.Controls.ComboBox ||
                    current is System.Windows.Controls.Primitives.Selector)
                {
                    return true;
                }

                current = GetParent(current);
            }

            return true;
        }

        private static DependencyObject? GetParent(DependencyObject child)
        {
            if (child == null) return null;

            if (child is Visual || child is System.Windows.Media.Media3D.Visual3D)
            {
                var visualParent = VisualTreeHelper.GetParent(child);
                if (visualParent != null) return visualParent;
            }

            return LogicalTreeHelper.GetParent(child);
        }
    }
}

