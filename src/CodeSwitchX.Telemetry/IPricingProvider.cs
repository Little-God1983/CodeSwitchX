namespace CodeSwitchX.Telemetry;

public interface IPricingProvider
{
    PricingTable Pricing { get; }
}
