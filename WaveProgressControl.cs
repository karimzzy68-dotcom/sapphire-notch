using System;
using System.Windows;
using System.Windows.Media;

namespace SapphireNotch.Windows;

public sealed class WaveProgressControl : FrameworkElement
{
	public static readonly DependencyProperty AccentProperty = DependencyProperty.Register("Accent", typeof(Brush), typeof(WaveProgressControl), (PropertyMetadata)(object)new FrameworkPropertyMetadata((object)Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

	private double phase;

	public Brush Accent
	{
		get
		{
			return (Brush)((DependencyObject)this).GetValue(AccentProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(AccentProperty, (object)value);
		}
	}

	public WaveProgressControl()
	{
		CompositionTarget.Rendering += delegate
		{
			phase += 0.055;
			InvalidateVisual();
		};
	}

	protected override void OnRender(DrawingContext dc)
	{
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0171: Unknown result type (might be due to invalid IL or missing references)
		//IL_0196: Unknown result type (might be due to invalid IL or missing references)
		//IL_01de: Unknown result type (might be due to invalid IL or missing references)
		base.OnRender(dc);
		double num = base.ActualHeight * 0.68;
		double num2 = base.ActualWidth * 0.58;
		dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(65, byte.MaxValue, byte.MaxValue, byte.MaxValue)), null, new Rect(0.0, num - 1.25, base.ActualWidth, 2.5), 2.0, 2.0);
		StreamGeometry streamGeometry = new StreamGeometry();
		using (StreamGeometryContext streamGeometryContext = streamGeometry.Open())
		{
			streamGeometryContext.BeginFigure(new Point(0.0, num), isFilled: true, isClosed: true);
			for (double num3 = 0.0; num3 <= num2; num3 += 1.5)
			{
				double num4 = num3 / Math.Max(num2, 1.0);
				double num5 = Math.Sin(Math.Min(num4 * 1.7, 1.0) * Math.PI / 2.0);
				double num6 = 0.76 + 0.17 * Math.Sin(num4 * 10.5 + phase) + 0.09 * Math.Sin(num4 * 21.0 - phase * 0.72);
				streamGeometryContext.LineTo(new Point(num3, num - 11.0 * num5 * num6), isStroked: true, isSmoothJoin: false);
			}
			streamGeometryContext.LineTo(new Point(num2, num), isStroked: true, isSmoothJoin: false);
		}
		((Freezable)streamGeometry).Freeze();
		dc.DrawGeometry(Accent, null, streamGeometry);
		dc.DrawEllipse(Accent, new Pen(Brushes.White, 1.6), new Point(num2, num), 4.5, 4.5);
	}
}
