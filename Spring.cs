namespace SapphireNotch.Windows;

/// <summary>
/// Critically-tuned damped spring with the same dynamics as Coucou's island:
/// a period of 0.5 s and a damping ratio of 0.72, integrated in fixed 1/240 s steps.
/// </summary>
internal sealed class Spring
{
    private readonly double omega;
    private readonly double zeta;

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; set; }

    public Spring(double value = 0, double periodSeconds = 0.5, double dampingRatio = 0.72)
    {
        Value = value;
        Target = value;
        omega = 2 * Math.PI / periodSeconds;
        zeta = dampingRatio;
    }

    public bool IsSettled => Math.Abs(Target - Value) < 0.0005 && Math.Abs(Velocity) < 0.002;

    public void Snap(double value)
    {
        Value = value;
        Target = value;
        Velocity = 0;
    }

    public void Step(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
        {
            return;
        }
        int steps = Math.Max(1, (int)Math.Ceiling(seconds / (1.0 / 240.0)));
        double dt = seconds / steps;
        for (int i = 0; i < steps; i++)
        {
            double acceleration = omega * omega * (Target - Value) - 2 * zeta * omega * Velocity;
            Velocity += acceleration * dt;
            Value += Velocity * dt;
        }
        if (IsSettled)
        {
            Snap(Target);
        }
    }
}
