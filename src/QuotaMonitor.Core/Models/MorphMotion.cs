namespace QuotaMonitor.Core.Models;

// A bounded, monotone Hermite segment. Retarget from the sampled position, retaining
// velocity only towards the new target. No springs, overshoot, or endpoint resets.
public sealed class MorphMotion(double value)
{
    public double Value { get; private set; } = value;
    public double Velocity { get; private set; }
    public double Target { get; private set; } = value;
    private double start = value, tangent, began, duration;
    public bool Active { get; private set; }
    public void Set(double target) { Value = Target = start = target; Velocity = 0; Active = false; }
    public void Retarget(double target, double now, double seconds)
    {
        Sample(now);
        if (Math.Abs(target - Target) < 0.000001) return;
        start = Value; Target = target; began = now; duration = Math.Max(.001, seconds);
        var delta = target - start;
        tangent = Math.Sign(Velocity) == Math.Sign(delta) ? Math.CopySign(Math.Min(Math.Abs(Velocity * duration), 3 * Math.Abs(delta)), delta) : 0;
        Active = Math.Abs(delta) > .000001;
    }
    public double Sample(double now)
    {
        if (!Active) return Value;
        var t = Math.Clamp((now - began) / duration, 0, 1);
        if (t >= 1) { Set(Target); return Value; }
        var delta = Target - start;
        Value = start + delta * (3*t*t - 2*t*t*t) + tangent * (t*t*t - 2*t*t + t);
        Velocity = (delta * (6*t - 6*t*t) + tangent * (3*t*t - 4*t + 1)) / duration;
        return Value;
    }
}
