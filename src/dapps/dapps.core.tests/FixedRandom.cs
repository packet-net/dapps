namespace dapps.core.tests;

/// <summary>A <see cref="Random"/> that always draws the same number, for
/// testing a random spread exactly.</summary>
internal sealed class FixedRandom(double value) : Random
{
    public override double NextDouble() => value;
    protected override double Sample() => value;
}
