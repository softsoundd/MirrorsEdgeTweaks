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
        private static readonly Dictionary<ScrollViewer, TimeSpan> _lastRenderTime = new Dictionary<ScrollViewer, TimeSpan>();

        private const double LerpPerFrame = 0.18;
        private const double ReferenceFps = 60.0;

        private static readonly LinearGradientBrush FadeBottomBrush;
        private static readonly LinearGradientBrush FadeTopBrush;
        private static readonly LinearGradientBrush FadeBothBrush;

        static SmoothScrollBehavior()
        {
            FadeBottomBrush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Colors.Black, 0),
                    new GradientStop(Colors.Black, 0.95),
                    new GradientStop(Colors.Transparent, 1)
                }
            };
            FadeBottomBrush.Freeze();

            FadeTopBrush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Black, 0.05),
                    new GradientStop(Colors.Black, 1)
                }
            };
            FadeTopBrush.Freeze();

            FadeBothBrush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Black, 0.05),
                    new GradientStop(Colors.Black, 0.95),
                    new GradientStop(Colors.Transparent, 1)
                }
            };
            FadeBothBrush.Freeze();
        }

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

            bool atTop = scrollViewer.VerticalOffset <= 0.1;
            bool atBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 0.1;

            if (atTop && atBottom)
                scrollViewer.OpacityMask = null;
            else if (atTop)
                scrollViewer.OpacityMask = FadeBottomBrush;
            else if (atBottom)
                scrollViewer.OpacityMask = FadeTopBrush;
            else
                scrollViewer.OpacityMask = FadeBothBrush;
        }

        private static void OnSmoothScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer scrollViewer)
            {
                scrollViewer.PreviewMouseWheel -= ScrollViewer_PreviewMouseWheel;
                scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;

                if ((bool)e.NewValue)
                {
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
                    EventHandler handler = (s, args) => OnRendering(scrollViewer, args);
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

        private static void OnRendering(ScrollViewer scrollViewer, EventArgs args)
        {
            if (!_targetOffsets.ContainsKey(scrollViewer))
            {
                StopAnimation(scrollViewer);
                return;
            }

            double deltaSeconds = 1.0 / ReferenceFps;
            if (args is RenderingEventArgs renderArgs)
            {
                TimeSpan now = renderArgs.RenderingTime;
                if (_lastRenderTime.TryGetValue(scrollViewer, out var last))
                    deltaSeconds = Math.Min((now - last).TotalSeconds, 0.1);
                _lastRenderTime[scrollViewer] = now;
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

            double factor = 1.0 - Math.Pow(1.0 - LerpPerFrame, deltaSeconds * ReferenceFps);
            double newOffset = current + (difference * factor);
            scrollViewer.ScrollToVerticalOffset(newOffset);
        }

        private static void StopAnimation(ScrollViewer scrollViewer)
        {
            if (_renderingHandlers.ContainsKey(scrollViewer))
            {
                CompositionTarget.Rendering -= _renderingHandlers[scrollViewer];
            }
            _isAnimating[scrollViewer] = false;
            _lastRenderTime.Remove(scrollViewer);
        }

        private static bool ShouldIgnoreMouseWheel(DependencyObject? originalSource, ScrollViewer mainScrollViewer)
        {
            DependencyObject? current = originalSource;

            while (current != null)
            {
                if (current == mainScrollViewer)
                    return false;

                if (current is System.Windows.Controls.ComboBox combo && combo.IsDropDownOpen)
                    return true;

                if (current is ScrollViewer nested && nested != mainScrollViewer
                    && nested.ScrollableHeight > 0
                    && !(nested.TemplatedParent is System.Windows.Controls.TextBox))
                    return true;

                current = GetParent(current);
            }

            return false;
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

