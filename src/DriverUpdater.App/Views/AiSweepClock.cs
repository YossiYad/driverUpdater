using System.Windows;
using System.Windows.Media.Animation;

namespace DriverUpdater.App.Views;

/// <summary>
/// One animation clock shared by every "Ask AI" busy indicator in the driver grid. Each row used to
/// run its own indeterminate ProgressBar, so bars that started at different moments swept out of
/// phase. Binding every row to this single animated Offset keeps them in lockstep no matter when a
/// row starts or stops checking.
/// </summary>
public sealed class AiSweepClock : Animatable
{
    public const double TrackWidth = 58d;
    public const double SweepWidth = 26d;

    private static readonly Duration CycleDuration = new(TimeSpan.FromSeconds(1.3));

    [ThreadStatic]
    private static AiSweepClock? _instance;

    /// <summary>
    /// A DependencyObject belongs to the thread that created it, so the clock is per UI thread.
    /// The app has one, which is what makes every bar on it share a single sweep.
    /// </summary>
    public static AiSweepClock Instance => _instance ??= new AiSweepClock();

    private readonly HashSet<FrameworkElement> _running = new();

    public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
        nameof(Offset),
        typeof(double),
        typeof(AiSweepClock),
        new PropertyMetadata(-SweepWidth));

    public double Offset
    {
        get => (double)GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.RegisterAttached(
        "IsRunning",
        typeof(bool),
        typeof(AiSweepClock),
        new PropertyMetadata(false, OnIsRunningChanged));

    public static void SetIsRunning(DependencyObject element, bool value) => element.SetValue(IsRunningProperty, value);

    public static bool GetIsRunning(DependencyObject element) => (bool)element.GetValue(IsRunningProperty);

    protected override Freezable CreateInstanceCore() => new AiSweepClock();

    private static void OnIsRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            element.Loaded += OnElementLoaded;
            element.Unloaded += OnElementUnloaded;
            if (element.IsLoaded)
            {
                Instance.Acquire(element);
            }
        }
        else
        {
            element.Loaded -= OnElementLoaded;
            element.Unloaded -= OnElementUnloaded;
            Instance.Release(element);
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && GetIsRunning(element))
        {
            Instance.Acquire(element);
        }
    }

    private static void OnElementUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            Instance.Release(element);
        }
    }

    private void Acquire(FrameworkElement element)
    {
        if (_running.Add(element) && _running.Count == 1)
        {
            BeginAnimation(
                OffsetProperty,
                new DoubleAnimation(-SweepWidth, TrackWidth, CycleDuration) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    private void Release(FrameworkElement element)
    {
        if (_running.Remove(element) && _running.Count == 0)
        {
            BeginAnimation(OffsetProperty, null);
            Offset = -SweepWidth;
        }
    }
}
